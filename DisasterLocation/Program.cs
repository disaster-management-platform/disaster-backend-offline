using DisasterLocation.BLL;
using DisasterLocation.Core;
using DisasterLocation.DAL;
using Platform.DAL;
using Platform.Security;
using Reconstruction.Application.Ports;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Disasters")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Disasters para PostgreSQL.");
if (builder.Configuration.GetValue<bool>("Database:Migrate"))
{
    await PlatformDatabase.MigrateAsync(connectionString);
    return;
}
var maxOperations = builder.Configuration.GetValue<int>("Synchronization:MaxBatchOperations", 50);
var maxBodyBytes = builder.Configuration.GetValue<long>("Synchronization:MaxBatchBodyBytes", 1_048_576);
var maxPageSize = builder.Configuration.GetValue<int>("Synchronization:MaxPageSize", 50);
var syncConcurrency = builder.Configuration.GetValue<int>("Synchronization:ConcurrentRequests", 2);
if (syncConcurrency < 1 || syncConcurrency >= new Npgsql.NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize)
    throw new InvalidOperationException("Reserve conexiones PostgreSQL para las operaciones interactivas.");
if (maxOperations <= 0 || maxBodyBytes <= 0 || maxPageSize <= 0) throw new InvalidOperationException("Límites de sincronización inválidos.");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddConcurrencyLimiter("synchronization", limiter =>
    {
        limiter.PermitLimit = syncConcurrency;
        limiter.QueueLimit = 0;
    });
    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        await Results.Problem(statusCode: 429, title: "Sincronización ocupada").ExecuteAsync(context.HttpContext);
    };
});
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddScoped<DisasterService>();
builder.Services.AddDataAccess(connectionString);
builder.Services.AddPlatformSecurity(builder.Configuration, builder.Environment);
var localDemo = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Security:LocalDemo");
builder.Services.AddAuthorization(options => options.AddPolicy("DisasterAdministration", policy =>
    policy.RequireAuthenticatedUser().RequireClaim("sub").RequireClaim("organization_id").RequireAssertion(context => context.User.IsInRole("disasters.admin")
        || (localDemo && context.User.HasClaim("local_demo", "true")))));

var app = builder.Build();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/desastres/batch", StringComparison.OrdinalIgnoreCase))
    {
        if (context.Request.ContentLength > maxBodyBytes) { context.Response.StatusCode = 413; return; }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = maxBodyBytes;
    }
    await next(context);
});
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

disasters.MapGet("/sync", async (string? cursor, int? limit, DisasterService service, CancellationToken ct) =>
{
    var pageSize = limit ?? maxPageSize;
    if (pageSize < 1 || pageSize > maxPageSize)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["limit"] = [$"El tamaño de página debe estar entre 1 y {maxPageSize}."] });
    try { return Results.Ok(await service.GetChangesAsync(cursor, pageSize, ct)); }
    catch (DisasterValidationException e) { return Results.ValidationProblem(e.Errors); }
}).RequireRateLimiting("synchronization");

disasters.MapPost("/batch", async (BatchDisasterRequest request, IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
    ILogger<Program> logger, CancellationToken ct) =>
{
    if (request.Operations is null || request.Operations.Length < 1 || request.Operations.Length > maxOperations)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["operations"] = [$"Envíe entre 1 y {maxOperations} operaciones."] });
    var results = new List<BatchDisasterResult>(request.Operations.Length);
    foreach (var item in request.Operations)
    {
        ct.ThrowIfCancellationRequested();
        string? key = null;
        if (item.ValueKind == JsonValueKind.Object)
            foreach (var property in item.EnumerateObject())
                if (property.Name.Equals("clientRequestId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String) key = property.Value.GetString();
        try
        {
            var operation = item.Deserialize<BatchDisasterOperation>(json.Value.SerializerOptions);
            if (operation?.ClientRequestId is null || operation.Disaster is null) throw new JsonException();
            await using var scope = scopes.CreateAsyncScope();
            var saved = await scope.ServiceProvider.GetRequiredService<DisasterService>()
                .CreateAsync(operation.Disaster, operation.ClientRequestId, ct);
            results.Add(new(key, saved.Created ? BatchDisasterStatus.Created : BatchDisasterStatus.AlreadyProcessed, saved.Disaster));
        }
        catch (JsonException) { results.Add(new(key, BatchDisasterStatus.ValidationError, Errors: new Dictionary<string, string[]> { ["operation"] = ["Operación de creación incompleta o inválida."] })); }
        catch (DisasterValidationException e) { results.Add(new(key, BatchDisasterStatus.ValidationError, Errors: e.Errors)); }
        catch (DisasterConflictException e) { results.Add(new(key, BatchDisasterStatus.Conflict, Errors: new Dictionary<string, string[]> { ["operation"] = [e.Message] })); }
        catch (Exception e) when (PlatformDatabase.IsPersistenceFailure(e))
        {
            logger.LogWarning("Batch persistence failed for operation {ClientRequestId}, error {ErrorType}", key, e.GetType().Name);
            results.Add(new(key, BatchDisasterStatus.RetryableFailure));
        }
    }
    return Results.Ok(new BatchDisasterResponse(results));
}).RequireRateLimiting("synchronization");

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
disasters.MapPut("/{id:int}", async (int id, UpdateDisasterRequest request, HttpContext http,
    DisasterService service, IDisasterAccess access, CancellationToken ct) =>
{
    if (!access.CanAccess(id)) return Results.Forbid();
    try
    {
        var result = await service.UpdateAsync(id, request, PlatformSecurity.Actor(http.User), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (DisasterValidationException e) { return Results.ValidationProblem(e.Errors); }
    catch (DisasterConflictException) { return Results.Conflict(new { detail = "El desastre cambió; actualice su versión." }); }
}).RequireAuthorization(PlatformSecurity.Write);

disasters.MapGet("/{id:int}/historial", async (int id, DisasterService service, IDisasterAccess access, CancellationToken ct) =>
{
    if (!access.CanAccess(id)) return Results.Forbid();
    if (await service.GetByIdAsync(id, ct) is null) return Results.NotFound();
    return Results.Ok(await service.GetHistoryAsync(id, ct));
}).RequireAuthorization(PlatformSecurity.Read);

var types = app.MapGroup("/api/tipos-desastre").WithTags("Tipos de desastre");
types.MapGet("/", async (DisasterService service, CancellationToken ct) => Results.Ok(await service.GetTypesAsync(ct)))
    .AllowAnonymous();
types.MapPost("/", async (CreateDisasterTypeRequest request, DisasterService service, CancellationToken ct) =>
{
    try
    {
        var type = await service.AddTypeAsync(request, ct);
        return type is null ? Results.Conflict(new { detail = "Ya existe un tipo con ese nombre." })
            : Results.Created($"/api/tipos-desastre/{type.Id}", type);
    }
    catch (DisasterValidationException e) { return Results.ValidationProblem(e.Errors); }
}).RequireAuthorization("DisasterAdministration");
types.MapPut("/{id:int}/desactivar", async (int id, DisasterService service, CancellationToken ct) =>
    await service.DeactivateTypeAsync(id, ct) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization("DisasterAdministration");
app.Run();
public partial class Program { }
