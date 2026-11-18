using System.Text.Json;
using DisasterLocation.BLL;
using DisasterLocation.Core;
using Platform.DAL;
using Platform.DAL.Repositories.Disasters;

namespace DisasterLocation.Messaging;

public sealed class AsyncBatchAcceptance(IServiceScopeFactory scopes, IConfiguration config)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public async Task<IResult> AcceptAsync(BatchDisasterRequest request, HttpResponse response, CancellationToken ct)
    {
        var results = new List<BatchDisasterResult>();
        var unaccepted = 0;
        foreach (var item in request.Operations!)
        {
            string? key = null;
            try
            {
                if (item.ValueKind == JsonValueKind.Object)
                    foreach (var property in item.EnumerateObject())
                        if (property.Name.Equals("clientRequestId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) key = property.Value.GetString();
                var operation = item.Deserialize<BatchDisasterOperation>(Json) ?? throw new JsonException();
                key = operation.ClientRequestId;
                if (!Guid.TryParseExact(key, "D", out var id) || id == Guid.Empty || operation.Disaster is null) throw new JsonException();
                var command = DisasterService.CreationCommand(operation.Disaster);
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<DisasterCommandStore>();
                var accepted = await store.AcceptAsync(id, command, config.GetValue<int>("Ingestion:Capacity", 10000), ct);
                if (accepted.Conflict) { results.Add(new(key, BatchDisasterStatus.Conflict)); continue; }
                if (accepted.Receipt is null) { unaccepted++; results.Add(new(key, BatchDisasterStatus.RetryableFailure)); continue; }
                if (accepted.Receipt.ResultJson is string result) results.Add((JsonSerializer.Deserialize<BatchDisasterResult>(result, Json)!) with { ClientRequestId = key });
                else results.Add(new(key, accepted.Receipt.PublishedAt is null ? BatchDisasterStatus.Accepted : BatchDisasterStatus.Processing));
            }
            catch (JsonException) { results.Add(new(key, BatchDisasterStatus.ValidationError, Errors: new Dictionary<string,string[]> { ["operation"] = ["UUID o comando inválido."] })); }
            catch (DisasterValidationException e) { results.Add(new(key, BatchDisasterStatus.ValidationError, Errors: e.Errors)); }
            catch (Exception e) when (PlatformDatabase.IsPersistenceFailure(e)) { unaccepted++; results.Add(new(key, BatchDisasterStatus.RetryableFailure)); }
        }
        response.Headers["Preference-Applied"] = "respond-async";
        if (results.Any(x => x.Status is BatchDisasterStatus.Accepted or BatchDisasterStatus.Processing))
        { response.Headers.RetryAfter = "10"; return Results.Json(new BatchDisasterResponse(results), Json, statusCode: 202); }
        if (unaccepted == results.Count) {
            response.Headers.RetryAfter = "10";
            return Results.Json(new BatchDisasterResponse(results), Json, statusCode: 503);
        }
        return Results.Ok(new BatchDisasterResponse(results));
    }
}
