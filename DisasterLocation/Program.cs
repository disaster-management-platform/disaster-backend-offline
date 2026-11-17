using DisasterLocation.BLL;
using DisasterLocation.Core;
using DisasterLocation.DAL;
using Platform.DAL;
using Platform.Security;
using Reconstruction.Application.Ports;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Disasters")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Disasters para PostgreSQL.");
if (builder.Configuration.GetValue<bool>("Database:Migrate"))
{
    await PlatformDatabase.MigrateAsync(connectionString);
    return;
}
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddScoped<DisasterService>();
builder.Services.AddDataAccess(connectionString);
builder.Services.AddPlatformSecurity(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var status = exception is BadHttpRequestException request ? request.StatusCode : 500;
    await Results.Problem(statusCode: status, title: status == 400 ? "Solicitud inválida" : "Error interno",
        detail: status == 400 ? "Revise los tipos y el formato JSON de la solicitud." : "Consulte el registro del servidor.").ExecuteAsync(context);
}));
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var disasters = app.MapGroup("/api/desastres").WithTags("Desastres");
disasters.MapGet("/", async (DisasterService service, CancellationToken ct) =>
    Results.Ok(await service.GetAllAsync(ct)))
    .WithSummary("Lista los desastres registrados.");

disasters.MapGet("/{id:int}", async (int id, DisasterService service, CancellationToken ct) =>
{
    var disaster = await service.GetByIdAsync(id, ct);
    return disaster is null ? Results.NotFound() : Results.Ok(disaster);
}).WithName("GetDisaster").WithSummary("Consulta un desastre por su identificador.");

disasters.MapPost("/", async (HttpRequest httpRequest, CreateDisasterRequest request,
    DisasterService service, CancellationToken ct) =>
{
    string? idempotencyKey = null;
    if (httpRequest.Headers.TryGetValue("Idempotency-Key", out var values))
    {
        if (values.Count != 1)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Envía una sola clave de idempotencia por solicitud."]
            });
        idempotencyKey = values[0];
    }
    try
    {
        var result = await service.CreateAsync(request, idempotencyKey, ct);
        return result.Created
            ? Results.Created($"/api/desastres/{result.Disaster.Id}", result.Disaster)
            : Results.Ok(result.Disaster);
    }
    catch (DisasterValidationException exception)
    {
        return Results.ValidationProblem(exception.Errors);
    }
    catch (DisasterConflictException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "Conflicto de idempotencia", detail: exception.Message);
    }
}).WithSummary("Registra un desastre con sus coordenadas.");

disasters.MapPut("/{id:int}/fase", async (int id, ChangeDisasterPhaseRequest request, HttpContext http,
    DisasterService service, IDisasterAccess access, CancellationToken ct) =>
{
    if (!access.CanAccess(id)) return Results.Forbid();
    try
    {
        var result = await service.ChangePhaseAsync(id, request, PlatformSecurity.Actor(http.User), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (DisasterValidationException e) { return Results.ValidationProblem(e.Errors); }
    catch (DisasterConflictException) { return Results.Conflict(new { detail = "El desastre cambió; actualice su versión." }); }
}).RequireAuthorization(PlatformSecurity.Write);
app.Run();
public partial class Program { }
