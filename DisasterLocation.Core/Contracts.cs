namespace DisasterLocation.Core;

// El mismo contrato JSON para clientes web, móviles o llamadas HTTP.
public sealed record CreateDisasterRequest(
    string? Nombre, string? Ubicacion, int? Severidad,
    string? Descripcion, double? Latitud, double? Longitud, double? Radio = null);

public sealed record DisasterResponse(
    int Id, string Nombre, string Ubicacion, int Severidad,
    string Descripcion, double Latitud, double Longitud, double? Radio = null,
    string Phase = "Registered", long Version = 1);

public sealed record CreateDisasterResult(DisasterResponse Disaster, bool Created);

public sealed record ChangeDisasterPhaseRequest(DisasterPhase Phase, long ExpectedVersion);
