using CommunityToolkit.Datasync.Client.Authentication;
using CommunityToolkit.Datasync.Client.Http;
using CommunityToolkit.Datasync.Client.Offline;
using DeliveryApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeliveryApp.Data;

// The app's local database: an EF Core SQLite file that Datasync keeps in sync with the API's table
// controllers (/tables/stop and /tables/delivery). Every screen reads from it, and every write goes into
// it first; SaveChanges also records each change in Datasync's operations queue, which PushAsync sends.

/// <summary>Datasync's metadata, as the client sees it (the server owns all four).</summary>
public abstract class SyncedEntity
{
    public string Id { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Version { get; set; }
    public bool Deleted { get; set; }
}

public class Stop : SyncedEntity
{
    public string DriverId { get; set; } = "";
    public DateOnly RouteDate { get; set; }
    public int Sequence { get; set; }
    public string Recipient { get; set; } = "";
    public string Address { get; set; } = "";
    public string Status { get; set; } = "";

    public bool IsCanceled => Status == StopStatus.Canceled;
}

public class Delivery : SyncedEntity
{
    public string StopId { get; set; } = "";
    public string DriverId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Note { get; set; }
    public DateTime RecordedAt { get; set; }
}

public class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options, ApiClient api) : OfflineDbContext(options)
{
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
    {
        // The API's own token on every request, fetched again when it's about to expire.
        var auth = new GenericAuthenticationProvider(async _ =>
        {
            var token = await api.FetchTokenAsync();
            return new AuthenticationToken { Token = token.Token, UserId = api.DriverId, DisplayName = api.DriverId, ExpiresOn = token.ExpiresAt };
        });
        optionsBuilder.UseHttpClientOptions(new HttpClientOptions
        {
            Endpoint = new Uri(AppConfig.ApiBaseUrl),
            HttpPipeline = [auth],
            Timeout = TimeSpan.FromSeconds(15),
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // The docs recommend a value converter for dates in SQLite. Store UTC and read it back as UTC:
        // Datasync's JSON converter hands out local times, and SQLite keeps no time zone.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
