using DisasterLocation.BLL;
using DisasterLocation.Core;

namespace DisasterLocation.Security;

public sealed class DisasterAccess(IDisasterRepository repository, IHttpContextAccessor accessor,
    IConfiguration config, IHostEnvironment environment)
{
    public const string Read = "disasters.read";
    public const string Write = "disasters.write";
    public bool LocalDemo => environment.IsDevelopment() && config.GetValue<bool>("Security:LocalDemo")
        && accessor.HttpContext?.User.HasClaim("local_demo", "true") == true;
    public DisasterScope Scope => ForWrite(false);
    private DisasterScope ForWrite(bool write)
    {
        var user = accessor.HttpContext!.User;
        return new(user.FindFirst("sub")!.Value, user.FindFirst("organization_id")!.Value,
            user.IsInRole("disasters.admin") || !write && user.IsInRole("disasters.organization.read"),
            user.FindAll("disaster_id").Select(c => int.TryParse(c.Value, out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray());
    }
    public DisasterCreationIdentity Identity => new(Scope.Actor, Scope.Organization);
    public async Task<bool> CanAccessAsync(int id, CancellationToken ct, bool write = false) =>
        LocalDemo || await repository.GetByIdAsync(id, ct) is { } disaster && ForWrite(write).CanAccess(disaster);
    public async Task<bool> CanReplayAsync(string? key, CancellationToken ct)
    {
        if (!Guid.TryParse(key, out var identity)) return true;
        var existing = await repository.GetByClientRequestIdAsync(identity, ct);
        return existing is null || LocalDemo || ForWrite(true).CanAccess(existing);
    }
}
