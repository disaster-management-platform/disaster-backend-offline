using DisasterLocation.Core;

namespace DisasterLocation.BLL;

public sealed class DisasterService(IDisasterRepository repository)
{
    public async Task<IReadOnlyList<DisasterResponse>> GetAllAsync(CancellationToken ct = default) =>
        (await repository.GetAllAsync(ct)).Select(ToResponse).ToArray();

    public async Task<DisasterResponse?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var disaster = await repository.GetByIdAsync(id, ct);
        return disaster is null ? null : ToResponse(disaster);
    }

    public async Task<DisasterResponse> CreateAsync(CreateDisasterRequest request, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText("nombre", request.Nombre, 120, errors);
        ValidateText("ubicacion", request.Ubicacion, 200, errors);
        ValidateText("descripcion", request.Descripcion, 2000, errors);
        if (request.Severidad is null or < 1 or > 5)
            errors["severidad"] = ["La severidad debe ser un entero entre 1 y 5."];
        if (request.Latitud is null || !double.IsFinite(request.Latitud.Value)
            || request.Latitud is < -90 or > 90)
            errors["latitud"] = ["La latitud es obligatoria y debe estar entre -90 y 90."];
        if (request.Longitud is null || !double.IsFinite(request.Longitud.Value)
            || request.Longitud is < -180 or > 180)
            errors["longitud"] = ["La longitud es obligatoria y debe estar entre -180 y 180."];
        if (errors.Count > 0)
            throw new DisasterValidationException(errors);

        var disaster = new Disaster
        {
            Nombre = request.Nombre!.Trim(),
            Ubicacion = request.Ubicacion!.Trim(),
            Severidad = request.Severidad!.Value,
            Descripcion = request.Descripcion!.Trim(),
            Latitud = request.Latitud!.Value,
            Longitud = request.Longitud!.Value
        };
        return ToResponse(await repository.AddAsync(disaster, ct));
    }

    private static void ValidateText(string field, string? value, int maxLength,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors[field] = ["Este campo es obligatorio."];
        else if (value.Trim().Length > maxLength)
            errors[field] = [$"Este campo permite máximo {maxLength} caracteres."];
    }

    private static DisasterResponse ToResponse(Disaster disaster) => new(
        disaster.Id, disaster.Nombre, disaster.Ubicacion, disaster.Severidad,
        disaster.Descripcion, disaster.Latitud, disaster.Longitud);
}
