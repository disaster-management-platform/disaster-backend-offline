using DisasterLocation.Core;
using Microsoft.EntityFrameworkCore;

namespace DisasterLocation.DAL;

public sealed class DisasterDbContext(DbContextOptions<DisasterDbContext> options) : DbContext(options)
{
    public DbSet<Disaster> Disasters => Set<Disaster>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var disaster = modelBuilder.Entity<Disaster>();
        disaster.ToTable("Desastres", table =>
        {
            table.HasCheckConstraint("CK_Desastres_Severidad", "Severidad BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_Desastres_Latitud", "Latitud BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_Desastres_Longitud", "Longitud BETWEEN -180 AND 180");
        });
        disaster.HasKey(d => d.Id);
        disaster.HasIndex(d => d.OperationId).IsUnique();
        disaster.Property(d => d.Nombre).IsRequired().HasMaxLength(120);
        disaster.Property(d => d.Ubicacion).IsRequired().HasMaxLength(200);
        disaster.Property(d => d.Descripcion).IsRequired().HasMaxLength(2000);
    }
}
