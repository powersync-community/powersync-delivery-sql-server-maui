using System.Globalization;
using DeliveryApp.Services;
using Microsoft.Data.Sqlite;

namespace DeliveryApp.Data;

// Rows of the local SQLite tables, which Dotmim.Sync created on the first sync from SQL Server's schema.
// Values are as Dotmim.Sync stores them: IDs are GUID text (upper case from SQL Server, compared without
// case), DATE is "yyyy-MM-dd", DATETIME2 is "yyyy-MM-dd HH:mm:ss.FFFFFFF" in UTC, with no zone.

public record Stop(string Id, string DriverId, string RouteDate, int Sequence, string Recipient, string Address, string Status)
{
    public bool IsCanceled => Status == StopStatus.Canceled;

    public const string Columns = "id, driver_id, route_date, sequence, recipient, address, status";

    public static Stop From(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6));
}

public record Delivery(string Id, string StopId, string Outcome, string? Note, string RecordedAt)
{
    public const string Columns = "id, stop_id, outcome, note, recorded_at";

    public static Delivery From(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4));
}

public static class LocalValues
{
    /// <summary>Now, in the format Dotmim.Sync stores SQL Server's DATETIME2, so ORDER BY sorts local and synced rows alike.</summary>
    public static string UtcNow() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    /// <summary>Today's route date, as Dotmim.Sync stores SQL Server's DATE. The UTC date, like the server's.</summary>
    public static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
