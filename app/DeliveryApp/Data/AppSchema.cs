using System.Globalization;
using DeliveryApp.Services;
using PowerSync.Common.DB;
using PowerSync.Common.DB.Schema.Attributes;
using PS = PowerSync.Common.DB.Schema;

namespace DeliveryApp.Data;

// The local SQLite schema PowerSync keeps in sync with SQL Server. The classes double as query result
// types: [Column] declares each SQLite column, every other property must be [Ignored] (otherwise it
// becomes a column too), and FromRow reads a query row into the class. Values are as PowerSync sends
// them: IDs are upper-case GUID text, DATE is "yyyy-MM-dd", DATETIME2 is ISO 8601 UTC text.

[Table("stops")]
[Index("route", ["route_date", "sequence"])]
public class Stop : IFromRow<Stop>
{
    [Column("id")] public string Id { get; set; } = "";
    [Column("driver_id")] public string DriverId { get; set; } = "";
    [Column("route_date")] public string RouteDate { get; set; } = "";
    [Column("sequence")] public int Sequence { get; set; }
    [Column("recipient")] public string Recipient { get; set; } = "";
    [Column("address")] public string Address { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "";
    [Column("updated_at")] public string UpdatedAt { get; set; } = "";

    [Ignored] public bool IsCanceled => Status == StopStatus.Canceled;

    public static Stop FromRow(IQueryRow row) => new()
    {
        Id = row.GetString("id"),
        DriverId = row.GetString("driver_id"),
        RouteDate = row.GetString("route_date"),
        Sequence = row.GetInt("sequence"),
        Recipient = row.GetString("recipient"),
        Address = row.GetString("address"),
        Status = row.GetString("status"),
        UpdatedAt = row.GetString("updated_at"),
    };
}

[Table("deliveries")]
[Index("stop_recorded", ["stop_id", "recorded_at"])]
public class Delivery : IFromRow<Delivery>
{
    [Column("id")] public string Id { get; set; } = "";
    [Column("stop_id")] public string StopId { get; set; } = "";
    [Column("driver_id")] public string DriverId { get; set; } = "";
    [Column("outcome")] public string Outcome { get; set; } = "";
    [Column("note")] public string? Note { get; set; }
    [Column("recorded_at")] public string RecordedAt { get; set; } = "";

    public static Delivery FromRow(IQueryRow row) => new()
    {
        Id = row.GetString("id"),
        StopId = row.GetString("stop_id"),
        DriverId = row.GetString("driver_id"),
        Outcome = row.GetString("outcome"),
        Note = row.GetStringOrNull("note"),
        RecordedAt = row.GetString("recorded_at"),
    };
}

public static class AppSchema
{
    public static readonly PS.Schema Schema = new(new PS.Table(typeof(Stop)), new PS.Table(typeof(Delivery)));

    /// <summary>
    /// A new row ID. Upper case, because that's how SQL Server's UNIQUEIDENTIFIER comes back through
    /// PowerSync: a lower-case local ID would sync back as a second, different row.
    /// </summary>
    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>
    /// Now, in the format PowerSync sends SQL Server's DATETIME2 (9 fractional digits and a Z), so
    /// ORDER BY sorts local and synced rows the same way.
    /// </summary>
    public static string UtcNow() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'00Z'", CultureInfo.InvariantCulture);

    /// <summary>Today's route date as PowerSync sends SQL Server's DATE. The UTC date, like the server's.</summary>
    public static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
