using System.Net;
using DeliveryApp.Services;
using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;
using PowerSync.Common.Client.Connection;
using PowerSync.Common.DB.Crud;

namespace DeliveryApp.Data;

/// <summary>A local write the server refused for good, e.g. a delivery on a stop dispatch canceled.</summary>
public record Rejection(int? StopSequence, string Outcome, string Error);

/// <summary>
/// Connects PowerSync to our backend: credentials come from the API's token endpoint, and local writes
/// are uploaded by calling the API's existing POST endpoint. This is the only class that knows about REST writes.
/// </summary>
public class DeliveryConnector(ApiClient api, ILogger<DeliveryConnector> logger) : IPowerSyncBackendConnector
{
    /// <summary>
    /// The latest write the server refused, until the driver dismisses it. Only the latest is kept.
    /// Production: this is lost if the app restarts before the driver sees it. Keep it in a local-only
    /// table, or have the server record rejections in a table that syncs back.
    /// </summary>
    public Rejection? LastRejection { get; private set; }

    public event EventHandler? RejectionChanged;

    public void ClearRejection()
    {
        LastRejection = null;
        RejectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The API's own token, which PowerSync checks against the API's JWKS.</summary>
    public async Task<PowerSyncCredentials?> FetchCredentials()
    {
        var token = await api.FetchTokenAsync();
        return new PowerSyncCredentials(AppConfig.PowerSyncUrl, token.Token, token.ExpiresAt);
    }

    /// <summary>
    /// Uploads the oldest local transaction. The SDK calls this whenever there are local writes, and calls
    /// it again (with backoff) if it throws. Recording an outcome is one transaction: PUT deliveries and
    /// PATCH stops (status).
    /// </summary>
    public async Task UploadData(IPowerSyncDatabase database)
    {
        var transaction = await database.GetNextCrudTransaction();
        if (transaction is null)
            return;

        foreach (var op in transaction.Crud)
        {
            if (op is { Table: "deliveries", Op: UpdateType.PUT })
                await UploadDeliveryAsync(database, op);
            else if (op is { Table: "stops", Op: UpdateType.PATCH } && op.OpData?.Keys.All(k => k == "status") == true)
                continue; // The server sets the status itself when it records the delivery. The local patch only updates the UI.
            else
                logger.LogWarning("Skipping a local change with no matching API call: {Op} {Table} {Id}", op.Op, op.Table, op.Id);
        }

        await transaction.Complete();
    }

    async Task UploadDeliveryAsync(IPowerSyncDatabase database, CrudEntry op)
    {
        var stopId = Text(op, "stop_id")!;
        var outcome = Text(op, "outcome")!;
        try
        {
            await api.RecordDeliveryAsync(stopId, op.Id, outcome, Text(op, "note"), Text(op, "recorded_at")!);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            // The server will never accept this one. Drop it so later writes aren't blocked behind it; the
            // local delivery disappears, and the stop's status comes back, at the next sync.
            logger.LogWarning("Delivery {Id} rejected with HTTP {Status}: {Error}", op.Id, (int)ex.StatusCode!, ex.Message);
            var stop = await database.GetOptional<Stop>("SELECT * FROM stops WHERE id = ?", [stopId]);
            LastRejection = new Rejection(stop?.Sequence, outcome, ex.Message);
            RejectionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized)
        {
            // The SDK retries the transaction, and the next call fetches a new token.
            api.ClearToken();
            throw;
        }
        // Anything else (no network, a timeout, a 5xx) propagates and the SDK retries later. Never complete those.
    }

    static string? Text(CrudEntry op, string column) => op.OpData?.GetValueOrDefault(column)?.ToString();
}
