namespace DisasterLocation.Core;

// El mismo contrato JSON para clientes web, móviles o llamadas HTTP.
public sealed record CreateDisasterRequest(
    string? Nombre, string? Ubicacion, int? Severidad,
    string? Descripcion, double? Latitud, double? Longitud,
    int? Radio, string? DesastreAsociado, string? FechaHora,
    string? Evaluador, string? Organizacion, string? TipoActivo,
    string? NivelDano, string? Evidencias, string? EstadoEvaluacion);

public sealed record DisasterResponse(
    int Id, string Nombre, string Ubicacion, int Severidad,
    string Descripcion, double Latitud, double Longitud,
    int Radio, string DesastreAsociado, string FechaHora,
    string Evaluador, string Organizacion, string TipoActivo,
    string NivelDano, string Evidencias, string EstadoEvaluacion);
