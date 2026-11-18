using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApi.Data;

// Mapped to the tables created by db/init.sql. The database owns the schema; there are no EF migrations.

[Table("stops")]
public class Stop
{
    [Column("id")] public Guid Id { get; set; }
    [Column("driver_id")] public string DriverId { get; set; } = "";
    [Column("route_date")] public DateOnly RouteDate { get; set; }
    [Column("sequence")] public int Sequence { get; set; }
    [Column("recipient")] public string Recipient { get; set; } = "";
    [Column("address")] public string Address { get; set; } = "";
    [Column("status")] public string Status { get; set; } = StopStatus.Assigned;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("deliveries")]
public class Delivery
{
    [Column("id")] public Guid Id { get; set; }
    [Column("stop_id")] public Guid StopId { get; set; }
    [Column("driver_id")] public string DriverId { get; set; } = "";
    [Column("outcome")] public string Outcome { get; set; } = "";
    [Column("note")] public string? Note { get; set; }
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
