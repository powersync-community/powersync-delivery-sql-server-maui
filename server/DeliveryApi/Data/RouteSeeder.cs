using Microsoft.EntityFrameworkCore;

namespace DeliveryApi.Data;

/// <summary>
/// DEMO CONVENIENCE: gives a driver something to deliver. In reality dispatch plans routes; here the
/// first token request of the day for a driver with no stops inserts a made-up route of 8 stops.
/// </summary>
public static class RouteSeeder
{
    public const int StopsPerRoute = 8;

    static readonly (string Recipient, string Address)[] Drops =
    [
        ("Amara Okafor", "14 Harbor View Rd, Unit 3"),
        ("Ben Whitaker", "220 Elm Street"),
        ("Chen Li", "7 Willow Lane"),
        ("Dana Kowalski", "89 Station Street, Suite 2"),
        ("Eli Navarro", "1503 Ridgeway Ave"),
        ("Fatima Haddad", "31 Orchard Court"),
        ("George Mensah", "402 Kingfisher Court, Apt 12"),
        ("Hannah Berg", "66 Mill Road"),
        ("Isaac Tremblay", "5 Quarry Hill Road"),
        ("Jade Nakamura", "118 Lighthouse Drive"),
        ("Kofi Asante", "27 Bramble Way"),
        ("Lena Fischer", "940 Market Street, Floor 4"),
    ];

    /// <summary>Today's route date. The UTC date: good enough for a demo.</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Inserts today's 8 stops if the driver has none for today. Safe to call repeatedly.</summary>
    public static async Task SeedTodayIfEmptyAsync(DeliveryDbContext db, string driverId)
    {
        var today = Today();

        await using var tx = await db.Database.BeginTransactionAsync();

        // UPDLOCK + HOLDLOCK holds a range lock on (driver, today) until commit, so two token requests
        // arriving together can't both see "no stops" and seed the route twice.
        var hasStops = await db.Stops
            .FromSqlInterpolated($"SELECT * FROM dbo.stops WITH (UPDLOCK, HOLDLOCK) WHERE driver_id = {driverId} AND route_date = {today}")
            .AnyAsync();
        if (hasStops)
            return;

        // Pick 8 of the drops, starting at a different place for each driver, so routes vary a little.
        // (string.GetHashCode is randomized per process, so use a simple stable sum instead.)
        var offset = driverId.Sum(c => c) % Drops.Length;
        for (var i = 0; i < StopsPerRoute; i++)
        {
            var (recipient, address) = Drops[(offset + i) % Drops.Length];
            db.Stops.Add(new Stop
            {
                DriverId = driverId,
                RouteDate = today,
                Sequence = i + 1,
                Recipient = recipient,
                Address = address,
                Status = StopStatus.Assigned,
            });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
