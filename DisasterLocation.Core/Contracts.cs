namespace DisasterLocation.Core;

// El mismo contrato JSON para clientes web, móviles o llamadas HTTP.
public sealed record CreateDisasterRequest(
    string? Nombre, string? Ubicacion, int? Severidad,
    string? Descripcion, double? Latitud, double? Longitud);

public sealed record DisasterResponse(
    int Id, string Nombre, string Ubicacion, int Severidad,
    string Descripcion, double Latitud, double Longitud);

public sealed record CreateDisasterResult(DisasterResponse Disaster, bool Created);
