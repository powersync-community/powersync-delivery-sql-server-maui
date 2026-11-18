using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeliveryApi.Data;

public class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
{
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // IDs and timestamps come from the database defaults (NEWSEQUENTIALID(), SYSUTCDATETIME()), not from EF.
        modelBuilder.Entity<Stop>(e =>
        {
            e.Property(s => s.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(s => s.UpdatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });
        modelBuilder.Entity<Delivery>(e =>
        {
            e.Property(d => d.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
            e.Property(d => d.RecordedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        // All timestamps are stored as UTC. Mark them as UTC when reading, so they serialize with a "Z".
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
            {
                property.SetValueConverter(utc);
            }
        }
    }
}
