using System.Text.Json;
using DisasterLocation.Core;

namespace DisasterLocation.BLL;

public sealed partial class DisasterService
{
    private static void ValidateReference(string field, string? value, Dictionary<string, string[]> errors)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200 || value.Any(char.IsControl)))
            errors[field] = ["Use un identificador estable de hasta 200 caracteres."];
    }

    public async Task<(DisasterReportResponse Report, bool Created)> ReceiveReportAsync(
        ReceiveDisasterReportRequest request, string actor, CancellationToken ct, string? organization = null)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText("sourceId", request.SourceId, 200, errors);
        ValidateText("sourceReportId", request.SourceReportId, 200, errors);
        ValidateReference("registeringOrganizationId", request.RegisteringOrganizationId, errors);
        if (request.SourceKind is not InformationSourceKind kind || !Enum.IsDefined(kind)) errors["sourceKind"] = ["Fuente inválida."];
        if (request.Content.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(request.Content.GetRawText()) > 65_536)
            errors["content"] = ["Envíe un objeto JSON de hasta 64 KiB."];
        if (request.CreatePreliminary && (request.SourceKind != InformationSourceKind.External || request.Disaster is null))
            errors["disaster"] = ["La detección preliminar requiere una fuente externa y los datos canónicos del desastre."];
        if (!request.CreatePreliminary && request.Disaster is not null)
            errors["disaster"] = ["Los datos del evento solo corresponden a una detección preliminar."];
        if (errors.Count != 0) throw new DisasterValidationException(errors);
        Disaster? preliminary = null;
        if (request.CreatePreliminary)
        {
            preliminary = Normalize(request.Disaster!, null);
            preliminary.CreatedBy = actor;
            preliminary.ClientRequestId = Guid.NewGuid();
            preliminary.RegisteringOrganizationId ??= organization ?? request.RegisteringOrganizationId?.Trim();
            preliminary.Phase = DisasterPhase.Detected;
            preliminary.RegisteredAt = preliminary.UpdatedAt = Utc(DateTimeOffset.UtcNow);
        }
        var report = new DisasterReport { Id = Guid.NewGuid(), SourceKind = request.SourceKind!.Value,
            SourceId = request.SourceId!.Trim(), SourceReportId = request.SourceReportId!.Trim(),
            ReceivedAt = Utc(DateTimeOffset.UtcNow)!.Value, ReceivedBy = actor,
            RegisteringOrganizationId = organization ?? request.RegisteringOrganizationId?.Trim(),
            OriginalContent = request.Content.GetRawText(),
            ReceiptSnapshot = JsonSerializer.Serialize(new { request.SourceKind, request.CreatePreliminary, Disaster = preliminary is null ? null : request.Disaster,
                RegisteringOrganizationId = request.RegisteringOrganizationId?.Trim(), Content = request.Content }) };
        var existing = await repository.GetReportBySourceAsync(report.SourceId, report.SourceReportId, ct);
        if (existing is null && preliminary?.DisasterTypeId is int typeId
            && !(await repository.GetTypesAsync(ct)).Any(x => x.Id == typeId && x.IsActive))
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["disasterTypeId"] = ["El tipo no existe o está desactivado."] });
        DisasterReportReceipt result;
        try { result = existing is null ? await repository.ReceiveReportAsync(report, preliminary, ct) : new DisasterReportReceipt(existing, false); }
        catch (System.Data.DBConcurrencyException) { throw new DisasterConflictException(); }
        if (!JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(report.ReceiptSnapshot),
                JsonSerializer.Deserialize<JsonElement>(result.Report.ReceiptSnapshot))) throw new DisasterConflictException();
        return (ReportResponse(result.Report), result.Created);
    }

    public async Task<IReadOnlyList<DisasterReportResponse>> GetReportsAsync(int? id, CancellationToken ct) =>
        (await repository.GetReportsAsync(id, ct)).Select(ReportResponse).ToArray();

    public static DisasterReportResponse ReportResponse(DisasterReport report) => new(report.Id, report.SourceKind,
        report.SourceId, report.SourceReportId, report.ReceivedAt, report.RegisteringOrganizationId,
        JsonSerializer.Deserialize<JsonElement>(report.OriginalContent), report.DisasterId);

    public Task<DisasterReport?> GetReportAsync(Guid id, CancellationToken ct) => repository.GetReportAsync(id, ct);
    public Task<IReadOnlyList<DisasterAlert>> GetAlertsAsync(int id, CancellationToken ct) => repository.GetAlertsAsync(id, ct);

    private static string CityKey(string location)
    {
        var city = location.Split(',')[0].Replace("D.C.", "", StringComparison.OrdinalIgnoreCase);
        var characters = city.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ').ToArray();
        return string.Join(' ', new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool Similar(Disaster disaster, int typeId, string location, DateTimeOffset startedAt) =>
        disaster.DisasterTypeId == typeId && disaster.StartedAt is DateTimeOffset existing
        && (existing - startedAt).Duration() <= TimeSpan.FromHours(72)
        && CityKey(disaster.Ubicacion) == CityKey(location);

    public async Task<IReadOnlyList<DisasterResponse>> FindSimilarAsync(int typeId, string location,
        DateTimeOffset startedAt, CancellationToken ct, DisasterScope? scope = null)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateText("ubicacion", location, 200, errors);
        if (typeId <= 0) errors["disasterTypeId"] = ["Seleccione un tipo válido."];
        if (errors.Count != 0) throw new DisasterValidationException(errors);
        return (await repository.GetAllAsync(ct)).Where(d => (scope is null || scope.CanAccess(d)) && Similar(d, typeId, location, startedAt))
            .OrderBy(d => (d.StartedAt!.Value - startedAt).Duration()).Select(ToResponse).ToArray();
    }

    public async Task<DisasterResponse?> RegisterReportAsync(int id, CreateDisasterRequest request,
        long expectedVersion, string key, string actor, string organization, CancellationToken ct)
    {
        if (!Guid.TryParse(key, out var identity) || identity == Guid.Empty)
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["Use un UUID estable."] });
        var normalized = Normalize(request, identity.ToString());
        var current = await repository.GetByIdAsync(id, ct);
        if (current is null) return null;
        const string source = "manual-web";
        var previous = await repository.GetReportBySourceAsync(source, identity.ToString(), ct);
        if (previous is not null && previous.ReceivedBy != actor) throw new DisasterConflictException();
        if (await repository.GetByClientRequestIdAsync(identity, ct) is not null) throw new DisasterConflictException();
        var command = new ReceiveDisasterReportRequest(InformationSourceKind.Internal, source,
            identity.ToString(), JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            RegisteringOrganizationId: organization);
        var result = previous is null ? null : (await ReceiveReportAsync(command, actor, ct)).Report;
        if (result?.DisasterId == id) return await GetByIdAsync(id, ct);
        if (result?.DisasterId is not null) throw new DisasterConflictException();
        if (current.Version != expectedVersion) throw new DisasterConflictException();
        if (normalized.DisasterTypeId is not int typeId || normalized.StartedAt is not DateTimeOffset date
            || !Similar(current, typeId, normalized.Ubicacion, date))
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["disasterId"] =
                ["La asociación requiere el mismo tipo y municipio o ciudad, dentro de 72 horas."] });
        result ??= (await ReceiveReportAsync(command, actor, ct)).Report;
        if (result.DisasterId == id) return await GetByIdAsync(id, ct);
        try { return await AssociateReportAsync(id, result.Id, expectedVersion, actor, ct); }
        catch (DisasterConflictException)
        {
            if ((await repository.GetReportAsync(result.Id, ct))?.DisasterId == id) return await GetByIdAsync(id, ct);
            throw;
        }
    }

    public async Task<DisasterResponse?> AssociateReportAsync(int id, Guid reportId, long expectedVersion, string actor, CancellationToken ct)
    {
        if (await repository.GetByIdAsync(id, ct) is null) return null;
        var report = await repository.GetReportAsync(reportId, ct);
        if (report is null) return null;
        if (report.DisasterId is not null) throw new DisasterConflictException();
        try
        {
            if (!await repository.AssociateReportAsync(id, reportId, expectedVersion, actor, ct)) throw new DisasterConflictException();
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["geography"] = [e.Message] });
        }
        return await GetByIdAsync(id, ct);
    }

    public async Task<DisasterResponse?> UpdateOrganizationsAsync(int id, UpdateDisasterOrganizationsRequest request, string actor, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        ValidateReference("registeringOrganizationId", request.RegisteringOrganizationId, errors);
        ValidateReference("emergencyResponsibleId", request.EmergencyResponsibleId, errors);
        if (request.Organizations is null || request.Organizations.Length > 50) errors["organizations"] = ["Envíe hasta 50 asociaciones."];
        else
        {
            foreach (var organization in request.Organizations)
            {
                if (organization is null) { errors["organizations"] = ["Asociación incompleta."]; continue; }
                ValidateText("organizationId", organization.OrganizationId, 200, errors);
                ValidateText("responsibility", organization.Responsibility, 500, errors);
            }
            if (request.Organizations.Where(x => x is not null).Select(x => (x.OrganizationId?.Trim(), x.Responsibility?.Trim())).Distinct().Count() != request.Organizations.Length)
                errors["organizations"] = ["No repita una responsabilidad del mismo organismo."];
        }
        if (errors.Count != 0) throw new DisasterValidationException(errors);
        var current = await repository.GetByIdAsync(id, ct);
        if (current is null) return null;
        if (DisasterScope.OriginalOrganization(current) is null && current.RegisteringOrganizationId is not null && request.RegisteringOrganizationId != current.RegisteringOrganizationId)
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["registeringOrganizationId"] = ["El organismo registrador no puede sustituirse ni eliminarse mediante una actualización de participantes."] });
        var organizations = request.Organizations!.Select(x => new DisasterOrganization(x.OrganizationId.Trim(), x.Responsibility.Trim())).ToArray();
        if (!await repository.UpdateContextAsync(id, request.ExpectedVersion, actor, "OrganizationsUpdated", null,
                JsonSerializer.Serialize(organizations), request.RegisteringOrganizationId?.Trim(), request.EmergencyResponsibleId?.Trim(), null, ct))
            throw new DisasterConflictException();
        return await GetByIdAsync(id, ct);
    }

    public async Task<DisasterResponse?> UpdateGeographyAsync(int id, UpdateDisasterGeographyRequest request, string actor, CancellationToken ct)
    {
        GeographyValidation.Validate(request.Geography);
        var errors = new Dictionary<string, string[]>();
        ValidateReference("sourceId", request.SourceId, errors);
        if (errors.Count != 0) throw new DisasterValidationException(errors);
        if (await repository.GetByIdAsync(id, ct) is null) return null;
        if (!await repository.UpdateContextAsync(id, request.ExpectedVersion, actor, "GeographyUpdated",
                JsonSerializer.Serialize(request.Geography), null, null, null, request.SourceId?.Trim(), ct)) throw new DisasterConflictException();
        return await GetByIdAsync(id, ct);
    }
}
