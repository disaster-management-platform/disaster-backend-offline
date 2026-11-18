using DisasterLocation.Core;
using System.Text.Json;

namespace DisasterLocation.BLL;

public sealed partial class DisasterService(IDisasterRepository repository)
{
    private static readonly IReadOnlyDictionary<DisasterPhase, DisasterPhase[]> Transitions =
        new Dictionary<DisasterPhase, DisasterPhase[]>
        {
            [DisasterPhase.Detected] = [DisasterPhase.Registered],
            [DisasterPhase.Registered] = [DisasterPhase.Assessment, DisasterPhase.Response, DisasterPhase.Recovery, DisasterPhase.Closed],
            [DisasterPhase.Assessment] = [DisasterPhase.Response, DisasterPhase.Recovery, DisasterPhase.Closed],
            [DisasterPhase.Response] = [DisasterPhase.Recovery, DisasterPhase.Closed],
            [DisasterPhase.Recovery] = [DisasterPhase.Reconstruction, DisasterPhase.Closed],
            [DisasterPhase.Reconstruction] = [DisasterPhase.Closed],
            [DisasterPhase.Closed] = []
        };

    public async Task<IReadOnlyList<DisasterTypeResponse>> GetTypesAsync(CancellationToken ct) =>
        (await repository.GetTypesAsync(ct)).Select(x => new DisasterTypeResponse(x.Id, x.Name, x.IsActive)).ToArray();
    public Task<bool> DeactivateTypeAsync(int id, CancellationToken ct) => repository.DeactivateTypeAsync(id, ct);
    public Task<IReadOnlyList<DisasterHistoryEntry>> GetHistoryAsync(int id, CancellationToken ct) => repository.GetHistoryAsync(id, ct);
    public async Task<DisasterTypeResponse?> AddTypeAsync(CreateDisasterTypeRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText("name", request.Name, 120, errors);
        if (errors.Count > 0) throw new DisasterValidationException(errors);
        var type = await repository.AddTypeAsync(request.Name!.Trim(), ct);
        return type is null ? null : new DisasterTypeResponse(type.Id, type.Name, type.IsActive);
    }

    private static DateTimeOffset? Utc(DateTimeOffset? value) => value is null ? null
        : new DateTimeOffset(value.Value.UtcTicks - value.Value.UtcTicks % 10, TimeSpan.Zero);

    public async Task<DisasterSyncResponse> GetChangesAsync(string? cursor, int limit, CancellationToken ct)
    {
        long after = 0;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 64) throw new FormatException();
                var value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                if (!value.StartsWith("1:") || !long.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out after) || after < 0 || Cursor(after) != cursor)
                    throw new FormatException();
            }
            catch (FormatException)
            {
                throw new DisasterValidationException(new Dictionary<string, string[]> { ["cursor"] = ["Cursor de sincronización inválido."] });
            }
        }
        var page = await repository.GetChangesAsync(after, limit, ct);
        return new DisasterSyncResponse(page.Items.Select(ToResponse).ToArray(), Cursor(page.Cursor), page.HasMore);
    }

    private static string Cursor(long value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
        "1:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public async Task<IReadOnlyList<DisasterResponse>> GetAllAsync(CancellationToken ct = default) =>
        (await repository.GetAllAsync(ct)).Select(ToResponse).ToArray();

    public async Task<DisasterResponse?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var disaster = await repository.GetByIdAsync(id, ct);
        return disaster is null ? null : ToResponse(disaster);
    }

    private static Disaster Normalize(CreateDisasterRequest request, string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>();
        // Sin cabecera se admite el cliente web anterior, pero no se deduplican sus reenvíos.
        Guid? clientRequestId = null;
        if (idempotencyKey is not null)
        {
            if (!Guid.TryParseExact(idempotencyKey, "D", out var parsedId) || parsedId == Guid.Empty)
                errors["Idempotency-Key"] = ["La clave debe ser un UUID válido, distinto de cero, con formato xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx."];
            else clientRequestId = parsedId;
        }
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
        if (request.Radio is not null && (!double.IsFinite(request.Radio.Value) || request.Radio <= 0 || request.Radio > 1_000_000))
            errors["radio"] = ["El radio debe estar entre 0 y 1.000.000 metros."];
        if (request.DisasterTypeId is <= 0) errors["disasterTypeId"] = ["Seleccione un tipo válido."];
        if (request.AttentionLevel is not null and (< 1 or > 4)) errors["attentionLevel"] = ["La clasificación de atención debe estar entre 1 y 4."];
        if (errors.Count > 0)
            throw new DisasterValidationException(errors);

        var disaster = new Disaster
        {
            ClientRequestId = clientRequestId,
            Nombre = request.Nombre!.Trim(),
            Ubicacion = request.Ubicacion!.Trim(),
            Severidad = request.Severidad!.Value,
            Descripcion = request.Descripcion!.Trim(),
            Latitud = request.Latitud!.Value,
            Longitud = request.Longitud!.Value,
            Radio = request.Radio,
            DisasterTypeId = request.DisasterTypeId,
            AttentionLevel = (AttentionLevel?)request.AttentionLevel,
            StartedAt = Utc(request.StartedAt)
        };
        return disaster;
    }

    public async Task<CreateDisasterResult> CreateAsync(CreateDisasterRequest request,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        var disaster = Normalize(request, idempotencyKey);
        disaster.ClientRequestId ??= Guid.NewGuid();
        disaster.RegisteredAt = disaster.UpdatedAt = Utc(DateTimeOffset.UtcNow);
        var clientRequestId = disaster.ClientRequestId.Value;
        var existing = await repository.GetByClientRequestIdAsync(clientRequestId, ct);
        if (existing is not null)
            return ResolveResult(disaster, new DisasterSaveResult(existing, Created: false));

        if (disaster.DisasterTypeId is int typeId && !(await repository.GetTypesAsync(ct)).Any(x => x.Id == typeId && x.IsActive))
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["disasterTypeId"] = ["El tipo no existe o está desactivado."] });
        return ResolveResult(disaster, await repository.AddAsync(disaster, ct));
    }

    public async Task<DisasterResponse?> UpdateAsync(int id, UpdateDisasterRequest request, string actor, CancellationToken ct)
    {
        var current = await repository.GetByIdAsync(id, ct);
        if (current is null) return null;
        if (request.ExpectedVersion != current.Version) throw new DisasterConflictException();
        var normalized = Normalize(new CreateDisasterRequest(request.Nombre, request.Ubicacion, request.Severidad,
            request.Descripcion, request.Latitud, request.Longitud, request.Radio,
            request.DisasterTypeId, request.StartedAt, request.AttentionLevel), null);
        if (normalized.DisasterTypeId != current.DisasterTypeId && normalized.DisasterTypeId is int typeId
            && !(await repository.GetTypesAsync(ct)).Any(x => x.Id == typeId && x.IsActive))
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["disasterTypeId"] = ["El tipo no existe o está desactivado."] });
        normalized.Id = id;
        if (!await repository.UpdateAsync(normalized, request.ExpectedVersion, actor, ct)) throw new DisasterConflictException();
        return await GetByIdAsync(id, ct);
    }

    public async Task<DisasterResponse?> ChangePhaseAsync(int id, ChangeDisasterPhaseRequest request,
        string actor, CancellationToken ct)
    {
        var disaster = await repository.GetByIdAsync(id, ct);
        if (disaster is null) return null;
        if (request.ExpectedVersion != disaster.Version) throw new DisasterConflictException();
        if (!Transitions.TryGetValue(disaster.Phase, out var allowed) || !allowed.Contains(request.Phase))
            throw new DisasterValidationException(new Dictionary<string, string[]>
            { ["phase"] = ["La transición no avanza en el ciclo de vida o el desastre está cerrado."] });
        var requiresReason = request.Phase == DisasterPhase.Closed || allowed[0] != request.Phase;
        if ((requiresReason && string.IsNullOrWhiteSpace(request.Reason)) || request.Reason?.Trim().Length > 2000)
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["reason"] = ["Justifique los saltos y cierres con un texto de hasta 2000 caracteres."] });
        if (request.Phase == DisasterPhase.Closed && !request.ConfirmNoCriticalCommitments)
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["confirmNoCriticalCommitments"] = ["Confirme que no quedan compromisos críticos pendientes."] });
        if (!await repository.ChangePhaseAsync(id, disaster.Phase, request.Phase, request.ExpectedVersion, actor, ct,
                request.Reason?.Trim(), request.Phase == DisasterPhase.Closed ? true : null))
            throw new DisasterConflictException();
        return await GetByIdAsync(id, ct);
    }

    private static CreateDisasterResult ResolveResult(Disaster requested, DisasterSaveResult result)
    {
        if (result.Disaster.CreationSnapshot is null)
            throw new DisasterConflictException("No es posible recuperar el contenido original de esta operación histórica; requiere revisión.");
        var saved = JsonSerializer.Deserialize<Disaster>(result.Disaster.CreationSnapshot)!;
        saved.Id = result.Disaster.Id;
        if (requested.Nombre != saved.Nombre || requested.Ubicacion != saved.Ubicacion
            || requested.Severidad != saved.Severidad || requested.Descripcion != saved.Descripcion
            || requested.Latitud != saved.Latitud || requested.Longitud != saved.Longitud || requested.Radio != saved.Radio
            || requested.DisasterTypeId != saved.DisasterTypeId || requested.StartedAt != saved.StartedAt
            || requested.AttentionLevel != saved.AttentionLevel)
            throw new DisasterConflictException();

        return new CreateDisasterResult(ToResponse(saved), result.Created);
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
        disaster.Descripcion, disaster.Latitud, disaster.Longitud, disaster.Radio, disaster.Phase.ToString(), disaster.Version,
        disaster.DisasterTypeId, disaster.StartedAt, disaster.RegisteredAt, disaster.UpdatedAt, (int?)disaster.AttentionLevel,
        disaster.RegisteringOrganizationId, disaster.EmergencyResponsibleId,
        disaster.GeographyJson is null ? null : JsonSerializer.Deserialize<DisasterGeography>(disaster.GeographyJson),
        disaster.OrganizationsJson is null ? null : JsonSerializer.Deserialize<DisasterOrganization[]>(disaster.OrganizationsJson));
}
