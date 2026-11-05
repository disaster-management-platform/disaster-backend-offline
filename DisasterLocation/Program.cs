using DisasterLocation.BLL;
using DisasterLocation.Core;
using DisasterLocation.DAL;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddScoped<DisasterService>();
builder.Services.AddDataAccess(
    builder.Configuration.GetConnectionString("Disasters") ?? "Data Source=App_Data/disasters.db",
    builder.Environment.ContentRootPath);

var app = builder.Build();
app.UseExceptionHandler();
// El DAL se encarga de crear y acceder a la base de datos.
await app.Services.InitializeDataAccessAsync();
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
    var idempotencyKey = httpRequest.Headers.TryGetValue("Idempotency-Key", out var values)
        ? values.ToString() : null;
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

app.Run();
