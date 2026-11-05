using DisasterLocation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DisasterLocation.DAL;

public static class DependencyInjection
{
    public static IServiceCollection AddDataAccess(this IServiceCollection services,
        string connectionString, string contentRootPath)
    {
        var connection = new SqliteConnectionStringBuilder(connectionString);
        if (connection.DataSource != ":memory:")
        {
            connection.DataSource = Path.GetFullPath(connection.DataSource, contentRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
        }
        services.AddDbContext<DisasterDbContext>(options => options.UseSqlite(connection.ToString()));
        services.AddScoped<IDisasterRepository, DisasterRepository>();
        return services;
    }

    public static async Task InitializeDataAccessAsync(this IServiceProvider services,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await context.Database.EnsureCreatedAsync(ct);

        // EnsureCreated no modifica tablas existentes. Esta actualización pequeña
        // conserva los reportes del taller; los antiguos quedan sin OperationId.
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "PRAGMA table_info('Desastres');";
        var hasOperationId = false;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                hasOperationId |= reader.GetString(1) == "OperationId";
        }

        if (!hasOperationId)
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Desastres ADD COLUMN OperationId TEXT NULL;", ct);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Desastres_OperationId ON Desastres (OperationId);", ct);
        await transaction.CommitAsync(ct);
    }
}
