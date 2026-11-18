namespace DisasterLocation.Core;

// El mismo contrato JSON para clientes web, móviles o llamadas HTTP.
public sealed record CreateDisasterRequest(
    string? Nombre, string? Ubicacion, int? Severidad,
    string? Descripcion, double? Latitud, double? Longitud, double? Radio = null,
    int? DisasterTypeId = null, DateTimeOffset? StartedAt = null, int? AttentionLevel = null);

public sealed record DisasterResponse(
    int Id, string Nombre, string Ubicacion, int Severidad,
    string Descripcion, double Latitud, double Longitud, double? Radio = null,
    string Phase = "Registered", long Version = 1,
    int? DisasterTypeId = null, DateTimeOffset? StartedAt = null, DateTimeOffset? RegisteredAt = null,
    DateTimeOffset? UpdatedAt = null, int? AttentionLevel = null);

public sealed record CreateDisasterResult(DisasterResponse Disaster, bool Created);

public sealed record ChangeDisasterPhaseRequest(DisasterPhase Phase, long ExpectedVersion,
    string? Reason = null, bool ConfirmNoCriticalCommitments = false);

public sealed record UpdateDisasterRequest(string? Nombre, string? Ubicacion, int? Severidad,
    string? Descripcion, double? Latitud, double? Longitud, double? Radio,
    int? DisasterTypeId, DateTimeOffset? StartedAt, int? AttentionLevel, long ExpectedVersion);

public sealed record CreateDisasterTypeRequest(string? Name);

public sealed record DisasterTypeResponse(int Id, string Name, bool IsActive);

public sealed record BatchDisasterRequest(System.Text.Json.JsonElement[]? Operations);
public sealed record BatchDisasterOperation(string? ClientRequestId, CreateDisasterRequest? Disaster);
public enum BatchDisasterStatus { Created, AlreadyProcessed, ValidationError, Conflict, RetryableFailure }
public sealed record BatchDisasterResult(string? ClientRequestId, BatchDisasterStatus Status,
    DisasterResponse? Disaster = null, IDictionary<string, string[]>? Errors = null);
public sealed record BatchDisasterResponse(IReadOnlyList<BatchDisasterResult> Results);
public sealed record DisasterSyncResponse(IReadOnlyList<DisasterResponse> Items, string NextCursor, bool HasMore);
