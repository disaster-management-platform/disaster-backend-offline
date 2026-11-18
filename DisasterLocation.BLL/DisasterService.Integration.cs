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
        ReceiveDisasterReportRequest request, string actor, CancellationToken ct)
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
            preliminary.ClientRequestId = Guid.NewGuid();
            preliminary.RegisteringOrganizationId ??= request.RegisteringOrganizationId?.Trim();
            preliminary.Phase = DisasterPhase.Detected;
            preliminary.RegisteredAt = preliminary.UpdatedAt = Utc(DateTimeOffset.UtcNow);
        }
        var report = new DisasterReport { Id = Guid.NewGuid(), SourceKind = request.SourceKind!.Value,
            SourceId = request.SourceId!.Trim(), SourceReportId = request.SourceReportId!.Trim(),
            ReceivedAt = Utc(DateTimeOffset.UtcNow)!.Value, ReceivedBy = actor,
            RegisteringOrganizationId = request.RegisteringOrganizationId?.Trim(),
            OriginalContent = request.Content.GetRawText(),
            ReceiptSnapshot = JsonSerializer.Serialize(new { request.SourceKind, request.CreatePreliminary, Disaster = preliminary is null ? null : request.Disaster,
                RegisteringOrganizationId = request.RegisteringOrganizationId?.Trim(), Content = request.Content }) };
        var existing = await repository.GetReportBySourceAsync(report.SourceId, report.SourceReportId, ct);
        if (existing is null && preliminary?.DisasterTypeId is int typeId
            && !(await repository.GetTypesAsync(ct)).Any(x => x.Id == typeId && x.IsActive))
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["disasterTypeId"] = ["El tipo no existe o está desactivado."] });
        var result = existing is null ? await repository.ReceiveReportAsync(report, preliminary, ct) : new DisasterReportReceipt(existing, false);
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

    public async Task<DisasterResponse?> AssociateReportAsync(int id, Guid reportId, long expectedVersion, string actor, CancellationToken ct)
    {
        if (await repository.GetByIdAsync(id, ct) is null) return null;
        if (await repository.GetReportAsync(reportId, ct) is null) return null;
        if (!await repository.AssociateReportAsync(id, reportId, expectedVersion, actor, ct)) throw new DisasterConflictException();
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
        if (await repository.GetByIdAsync(id, ct) is null) return null;
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
