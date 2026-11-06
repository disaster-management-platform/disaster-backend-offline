using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DisasterLocation.DAL;

// Permite generar migraciones sin iniciar la API ni modificar su base de datos.
public sealed class DisasterDbContextFactory : IDesignTimeDbContextFactory<DisasterDbContext>
{
    public DisasterDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<DisasterDbContext>()
            .UseSqlite("Data Source=disasters.db")
            .Options);
}
