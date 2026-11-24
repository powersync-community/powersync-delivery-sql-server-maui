using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeliveryApi.Data;

public class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
{
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Datasync's metadata, mapped onto the existing columns (see db/datasync.sql).
        MapDatasyncColumns(modelBuilder.Entity<Stop>(), "stops_datasync");
        MapDatasyncColumns(modelBuilder.Entity<Delivery>(), "deliveries_datasync");

        // recorded_at comes from the database default when not set (the old POST endpoint without a device time).
        modelBuilder.Entity<Delivery>().Property(d => d.RecordedAt).HasDefaultValueSql("SYSUTCDATETIME()");

        // All timestamps are stored as UTC. Mark them as UTC when reading, so they serialize with a "Z".
        // Datasync's JSON converter turns incoming times into *local* DateTimes, so convert those back to UTC
        // on the way in (otherwise recorded_at is off by the server's UTC offset outside a UTC container).
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
            {
                property.SetValueConverter(utc);
            }
        }
    }

    static void MapDatasyncColumns<T>(EntityTypeBuilder<T> e, string trigger) where T : EntityTableData
    {
        // Datasync IDs are strings; ours are UNIQUEIDENTIFIER. Convert, and keep NEWSEQUENTIALID() for rows
        // created without an ID ("" means "not set", e.g. the route seeder and the old POST endpoint).
        // The server sends IDs back as lower-case GUID text, so that's what the app must create too.
        e.Property(x => x.Id).HasColumnName("id")
            .HasConversion(v => Guid.Parse(v), v => v.ToString())
            .HasDefaultValueSql("NEWSEQUENTIALID()")
            .HasSentinel("");
        // DATETIME2 (UTC) in the database, DateTimeOffset in Datasync. The trigger sets it on every write.
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at")
            .HasConversion(v => v!.Value.UtcDateTime, v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
        e.Property(x => x.Version).HasColumnName("version"); // ROWVERSION
        e.Property(x => x.Deleted).HasColumnName("deleted");
        // The updated_at trigger means EF can't use OUTPUT to read back generated values (the docs' setup step).
        e.ToTable(t => t.HasTrigger(trigger));
    }
}
