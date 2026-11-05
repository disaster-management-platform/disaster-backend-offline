using DisasterLocation.Core;
using Microsoft.EntityFrameworkCore;

namespace DisasterLocation.DAL;

public sealed class DisasterRepository(DisasterDbContext context) : IDisasterRepository
{
    public async Task<IReadOnlyList<Disaster>> GetAllAsync(CancellationToken ct = default) =>
        await context.Disasters.AsNoTracking().OrderByDescending(d => d.Id).ToListAsync(ct);

    public Task<Disaster?> GetByIdAsync(int id, CancellationToken ct = default) =>
        context.Disasters.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);

    public async Task<Disaster> AddAsync(Disaster disaster, CancellationToken ct = default)
    {
        context.Disasters.Add(disaster);
        await context.SaveChangesAsync(ct);
        return disaster;
    }
}
