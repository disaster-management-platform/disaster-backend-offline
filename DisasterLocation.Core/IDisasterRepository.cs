namespace DisasterLocation.Core;

// La BLL conoce esta abstracción; no conoce SQLite ni EF Core.
public interface IDisasterRepository
{
    Task<IReadOnlyList<Disaster>> GetAllAsync(CancellationToken ct = default);
    Task<Disaster?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Disaster?> GetByClientRequestIdAsync(Guid clientRequestId, CancellationToken ct = default);
    Task<DisasterSaveResult> AddAsync(Disaster disaster, CancellationToken ct = default);
}

public sealed record DisasterSaveResult(Disaster Disaster, bool Created);
