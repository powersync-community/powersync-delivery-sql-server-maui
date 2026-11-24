using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;

namespace DeliveryApi.Data;

// Mapped to the tables created by db/init.sql (plus db/datasync.sql). The database owns the schema; there
// are no EF migrations. Both entities inherit Datasync's EntityTableData, which adds Id (a string),
// UpdatedAt, Version and Deleted; DeliveryDbContext maps those onto the existing columns. The same classes
// are what the table controllers send to the app (as camelCase JSON).

[Table("stops")]
public class Stop : EntityTableData
{
    [Column("driver_id")] public string DriverId { get; set; } = "";
    [Column("route_date")] public DateOnly RouteDate { get; set; }
    [Column("sequence")] public int Sequence { get; set; }
    [Column("recipient")] public string Recipient { get; set; } = "";
    [Column("address")] public string Address { get; set; } = "";
    [Column("status")] public string Status { get; set; } = StopStatus.Assigned;
}

[Table("deliveries")]
public class Delivery : EntityTableData
{
    [Column("stop_id")] public Guid StopId { get; set; }
    [Column("driver_id")] public string DriverId { get; set; } = "";
    // Checked by the table controller on every write (400 "Invalid entity").
    [Column("outcome"), Required, RegularExpression($"^({Outcomes.Delivered}|{Outcomes.Failed})$")]
    public string Outcome { get; set; } = "";
    [Column("note"), MaxLength(500)] public string? Note { get; set; }
    [Column("recorded_at")] public DateTime RecordedAt { get; set; }
}

public static class StopStatus
{
    public const string Assigned = "assigned";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}

public static class Outcomes
{
    public const string Delivered = "delivered";
    public const string Failed = "failed";

    public static bool IsValid(string? outcome) => outcome is Delivered or Failed;
}
