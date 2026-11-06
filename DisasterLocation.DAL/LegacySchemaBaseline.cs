using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace DisasterLocation.DAL;

internal static class LegacySchemaBaseline
{
    public static async Task ApplyAsync(DisasterDbContext context, CancellationToken ct)
    {
        // Las versiones con EnsureCreated no tenían historial de migraciones.
        // Solo reconocemos sus esquemas; EF aplica los cambios pendientes después.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        if ((await context.Database.GetAppliedMigrationsAsync(ct)).Any())
        {
            await transaction.CommitAsync(ct);
            return;
        }

        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "PRAGMA table_info('Desastres');";
        var columns = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(1));
        }

        if (columns.Count > 0)
        {
            string[] originalColumns = ["Id", "Nombre", "Ubicacion", "Severidad", "Descripcion", "Latitud", "Longitud"];
            if (!columns.IsSupersetOf(originalColumns) || columns.Contains("ClientRequestId"))
                throw new InvalidOperationException("El esquema SQLite no corresponde a una versión anterior conocida.");

            var history = context.GetService<IHistoryRepository>();
            await context.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            var baselineCount = columns.Contains("OperationId") ? 2 : 1;
            foreach (var migrationId in context.Database.GetMigrations().Take(baselineCount))
            {
                await context.Database.ExecuteSqlRawAsync(
                    history.GetInsertScript(new HistoryRow(migrationId, ProductInfo.GetVersion())), ct);
            }
        }
        await transaction.CommitAsync(ct);
    }
}
