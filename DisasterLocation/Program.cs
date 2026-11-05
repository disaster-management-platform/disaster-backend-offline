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

disasters.MapPost("/", async (CreateDisasterRequest request, DisasterService service, CancellationToken ct) =>
{
    try
    {
        var disaster = await service.CreateAsync(request, ct);
        return Results.Created($"/api/desastres/{disaster.Id}", disaster);
    }
    catch (DisasterValidationException exception)
    {
        return Results.ValidationProblem(exception.Errors);
    }
}).WithSummary("Registra un desastre con sus coordenadas.");

app.Run();
