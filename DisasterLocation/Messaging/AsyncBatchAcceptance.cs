using System.Text.Json;
using DisasterLocation.BLL;
using DisasterLocation.Core;
using DisasterLocation.Security;
using Platform.DAL;
using Platform.DAL.Repositories.Disasters;

namespace DisasterLocation.Messaging;

public sealed class AsyncBatchAcceptance(IServiceScopeFactory scopes, IConfiguration config)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public async Task<IResult> AcceptAsync(BatchDisasterRequest request, HttpResponse response, DisasterAccess access, CancellationToken ct)
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
                var command = DisasterService.CreationCommand(operation.Disaster, access.Identity);
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<DisasterCommandStore>();
                if (!await access.CanReplayAsync(key, ct)) { results.Add(new(key, BatchDisasterStatus.Conflict)); continue; }
                var historical = await store.GetAsync(id, ct);
                if (historical is not null && !JsonSerializer.Deserialize<JsonElement>(historical.CommandJson).TryGetProperty("_identity", out _))
                {
                    if (!JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(historical.CommandJson),
                        JsonSerializer.Deserialize<JsonElement>(DisasterService.CreationCommand(operation.Disaster))))
                    { results.Add(new(key, BatchDisasterStatus.Conflict)); continue; }
                    var previous = historical.ResultJson is null ? null : JsonSerializer.Deserialize<BatchDisasterResult>(historical.ResultJson, Json);
                    if (previous?.Status is BatchDisasterStatus.ValidationError or BatchDisasterStatus.Conflict)
                        results.Add(previous with { ClientRequestId = key });
                    else if (previous?.Disaster is { } disaster && await access.CanAccessAsync(disaster.Id, ct)) results.Add(previous with { ClientRequestId = key });
                    else results.Add(new(key, BatchDisasterStatus.RetryableFailure, Errors: new Dictionary<string,string[]> {
                        ["recoveryExhausted"] = ["Recepción histórica sin ámbito autenticado verificable; se conserva para revisión administrativa."] }));
                    continue;
                }
                var accepted = await store.AcceptAsync(id, command, config.GetValue<int>("Ingestion:Capacity", 10000), ct, config.GetValue<int>("Ingestion:MaxRecoveries", 3));
                if (accepted.Conflict) { results.Add(new(key, BatchDisasterStatus.Conflict)); continue; }
                if (accepted.Receipt is null) { unaccepted++; results.Add(new(key, BatchDisasterStatus.RetryableFailure)); continue; }
                if (accepted.Receipt.ResultJson is string result) {
                    var saved = JsonSerializer.Deserialize<BatchDisasterResult>(result, Json)!;
                    if (saved.Status == BatchDisasterStatus.RetryableFailure && accepted.Receipt.RecoveryCount >= config.GetValue<int>("Ingestion:MaxRecoveries", 3))
                        saved = saved with { Errors = new Dictionary<string,string[]> { ["recoveryExhausted"] = ["Recuperación automática agotada; requiere intervención administrativa. Se conserva el comando original."] } };
                    results.Add(saved with { ClientRequestId = key });
                }
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
