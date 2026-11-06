using DisasterLocation.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await LegacySchemaBaseline.ApplyAsync(context, ct);
        await context.Database.MigrateAsync(ct);
    }
}
