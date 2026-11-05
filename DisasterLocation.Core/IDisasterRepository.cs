namespace DisasterLocation.Core;

// La BLL conoce esta abstracción; no conoce SQLite ni EF Core.
public interface IDisasterRepository
{
    Task<IReadOnlyList<Disaster>> GetAllAsync(CancellationToken ct = default);
    Task<Disaster?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Disaster> AddAsync(Disaster disaster, CancellationToken ct = default);
}
