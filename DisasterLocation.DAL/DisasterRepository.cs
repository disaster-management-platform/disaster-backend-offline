using DisasterLocation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DisasterLocation.DAL;

public sealed class DisasterRepository(DisasterDbContext context) : IDisasterRepository
{
    public async Task<IReadOnlyList<Disaster>> GetAllAsync(CancellationToken ct = default) =>
        await context.Disasters.AsNoTracking().OrderByDescending(d => d.Id).ToListAsync(ct);

    public Task<Disaster?> GetByIdAsync(int id, CancellationToken ct = default) =>
        context.Disasters.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);

    public Task<Disaster?> GetByOperationIdAsync(Guid operationId, CancellationToken ct = default) =>
        context.Disasters.AsNoTracking().SingleOrDefaultAsync(d => d.OperationId == operationId, ct);

    public async Task<DisasterSaveResult> AddAsync(Disaster disaster, CancellationToken ct = default)
    {
        context.Disasters.Add(disaster);
        try
        {
            await context.SaveChangesAsync(ct);
            return new DisasterSaveResult(disaster, Created: true);
        }
        catch (DbUpdateException exception) when (disaster.OperationId.HasValue
            && exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            // Dos solicitudes pueden consultar la clave antes de que alguna la guarde.
            // El índice decide cuál inserta; la otra recupera el resultado confirmado.
            context.Entry(disaster).State = EntityState.Detached;
            var existing = await GetByOperationIdAsync(disaster.OperationId.Value, ct);
            if (existing is null)
                throw;
            return new DisasterSaveResult(existing, Created: false);
        }
    }
}
