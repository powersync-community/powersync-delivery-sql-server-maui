using System.Data.Common;
using DeliveryApi.Data;
using Dotmim.Sync;
using Dotmim.Sync.Enumerations;

namespace DeliveryApi.Sync;

/// <summary>What the app syncs, and the server-side rules for rows the app uploads.</summary>
public static class DeliverySync
{
    /// <summary>The filter parameter. The server always sets it from the token (see SyncController).</summary>
    public const string DriverParameter = "driver_id";

    /// <summary>
    /// Both tables, each filtered to one driver. The client gets all of the driver's rows and shows today's.
    /// Production: limit this to a date window too (a filter parameter on route_date).
    /// </summary>
    public static SyncSetup Setup()
    {
        var setup = new SyncSetup("stops", "deliveries");
        setup.Filters.Add("stops", DriverParameter);
        setup.Filters.Add("deliveries", DriverParameter);
        return setup;
    }

    /// <summary>
    /// The business rule, for rows a client uploads. Runs inside the sync's transaction, just before a batch
    /// of rows is applied to SQL Server. A row that breaks a rule is taken out of the batch, so it's never
    /// applied. (The same rule as POST /api/stops/{id}/deliveries, which stays for other clients.)
    /// Dotmim.Sync has no way to tell the client which rows were dropped, or why: see NOTES.md.
    /// </summary>
    public static async Task ApplyRulesAsync(RowsChangesApplyingArgs args, string driverId, ILogger logger)
    {
        var table = args.SchemaTable.TableName;

        // The app never deletes. Production: also reject changes to columns other than stops.status.
        if (args.State == SyncRowState.Deleted)
        {
            logger.LogWarning("Rejected {Count} deletes on {Table} from {Driver}", args.SyncRows.Count, table, driverId);
            args.SyncRows.Clear();
            return;
        }

        foreach (var row in args.SyncRows.ToList())
        {
            var reason = table switch
            {
                "stops" => await CheckStopAsync(args, row, driverId),
                "deliveries" => await CheckDeliveryAsync(args, row, driverId),
                _ => "Unknown table.",
            };
            if (reason is null)
            {
                if (table == "deliveries")
                    logger.LogInformation("Stop {Id}: {Outcome}", row["stop_id"], row["outcome"]);
                continue;
            }
            logger.LogWarning("Rejected {Table} {Id} from {Driver}: {Reason}", table, row["id"], driverId, reason);
            args.SyncRows.Remove(row);
        }
    }

    /// <summary>A driver only changes a stop's status, to an outcome, and only while it's open.</summary>
    static async Task<string?> CheckStopAsync(RowsChangesApplyingArgs args, SyncRow row, string driverId)
    {
        // UPDLOCK holds the stop until the sync commits, so a cancel from dispatch can't slip in between
        // this check and the update (the same idea as the API's UPDATE ... updated_at).
        var current = await ScalarAsync(args,
            "SELECT status FROM dbo.stops WITH (UPDLOCK) WHERE id = @id AND driver_id = @driver",
            ("@id", row["id"]), ("@driver", driverId));
        var status = row["status"] as string;
        if (current is null)
            return "Stop not found.";
        if (status == current)
            return null;
        return current switch
        {
            StopStatus.Canceled => "Dispatch canceled this stop.",
            StopStatus.Delivered => "This stop is already delivered.",
            _ => Outcomes.IsValid(status) ? null : "A driver can only record delivered or failed.",
        };
    }

    /// <summary>A delivery for one of the driver's own stops, which must not be canceled or delivered already.</summary>
    static async Task<string?> CheckDeliveryAsync(RowsChangesApplyingArgs args, SyncRow row, string driverId)
    {
        if (row["driver_id"] as string != driverId || !Outcomes.IsValid(row["outcome"] as string))
            return "Stop not found.";

        // Runs after this sync's stop updates (Dotmim.Sync applies parent tables first), so "delivered" is
        // checked against the deliveries table: another device's delivery, not this one's own stop update.
        var current = await ScalarAsync(args,
            "SELECT status FROM dbo.stops WITH (UPDLOCK) WHERE id = @stop AND driver_id = @driver",
            ("@stop", row["stop_id"]), ("@driver", driverId));
        if (current is null)
            return "Stop not found.";
        if (current == StopStatus.Canceled)
            return "Dispatch canceled this stop.";

        // The same delivery uploaded again (a retried sync) isn't "another" delivery, so it passes.
        var otherDelivery = await ScalarAsync(args,
            "SELECT TOP 1 'x' FROM dbo.deliveries WHERE stop_id = @stop AND outcome = 'delivered' AND id <> @id",
            ("@stop", row["stop_id"]), ("@id", row["id"]));
        return otherDelivery is null ? null : "This stop is already delivered.";
    }

    static async Task<string?> ScalarAsync(RowsChangesApplyingArgs args, string sql, params (string Name, object Value)[] parameters)
    {
        await using DbCommand command = args.Connection.CreateCommand();
        command.Transaction = args.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteScalarAsync() as string;
    }
}
