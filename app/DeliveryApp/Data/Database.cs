using System.Text.Json;
using CommunityToolkit.Datasync.Client;
using CommunityToolkit.Datasync.Client.Offline;
using CommunityToolkit.Datasync.Client.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.Data;

/// <summary>A local write the server refused, e.g. a delivery on a stop dispatch canceled.</summary>
public record Rejection(int? StopSequence, string Outcome, string Error);

/// <summary>
/// Owns the app's local database (<see cref="DeliveryDbContext"/>) and syncs it with the API. A DbContext isn't
/// thread-safe, so every use goes through one lock. Datasync syncs only when asked: the pages call
/// <see cref="SynchronizeAsync"/> when they appear and on pull-to-refresh, as the toolkit's MAUI sample does.
/// </summary>
public partial class Database : ObservableObject
{
    readonly DeliveryDbContext _db;
    readonly SemaphoreSlim _lock = new(1, 1);
    bool _offline;

    /// <summary>One line of sync state for the pages, or null when online with nothing waiting to upload.</summary>
    [ObservableProperty]
    string? statusLine;

    public Database(DeliveryDbContext db)
    {
        _db = db;
        // Production: EnsureCreated never changes an existing database; a schema change needs EF migrations.
        _db.Database.EnsureCreated();
    }

    /// <summary>
    /// The latest write the server refused, until the driver dismisses it. Only the latest is kept.
    /// Production: this is lost if the app restarts before the driver sees it.
    /// </summary>
    public Rejection? LastRejection { get; private set; }

    public event EventHandler? RejectionChanged;

    public void ClearRejection()
    {
        LastRejection = null;
        RejectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a query or a write on the local database. Works offline.</summary>
    public async Task<T> UseAsync<T>(Func<DeliveryDbContext, Task<T>> action)
    {
        await _lock.WaitAsync();
        try
        {
            return await action(_db);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The sample's SynchronizeAsync: push the operations queue, then pull what changed on the server. A push
    /// with failures stops there (the sample throws); the server's reason goes in <see cref="LastRejection"/>.
    /// (Pulling wouldn't help anyway: PullAsync refuses a table that still has queued changes, failed ones included.)
    /// </summary>
    public async Task SynchronizeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Deliveries first, then stops (not one PushAsync()): the queue keeps one change per stop, already
            // holding its final status, and doesn't push in order, so the stop's "delivered" could arrive before
            // the attempts behind it and the server would refuse them as "already delivered".
            var pushes = new[] { await _db.Deliveries.PushAsync(), await _db.Stops.PushAsync() };
            if (pushes.Any(p => !p.IsSuccessful))
            {
                foreach (var push in pushes)
                    await ShowRejectionAsync(push);
                _offline = false;
                return;
            }

            var pull = await _db.PullAsync();
            _offline = pull.LocalExceptions.Values.Any(IsNetworkError);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            // No network: the push throws and its operations stay queued for next time.
            _offline = true;
        }
        finally
        {
            _lock.Release();
            await RefreshStatusLineAsync();
        }
    }

    static bool IsNetworkError(Exception ex) => ex is HttpRequestException or TimeoutException or TaskCanceledException;

    /// <summary>Turns a refused push into a banner, using the server's { "error": "…" } and the queued change.</summary>
    async Task ShowRejectionAsync(PushResult push)
    {
        foreach (var (operationId, response) in push.FailedRequests)
        {
            // 400/404/409 with a reason: refused for good. Anything else (401, a 5xx) is retried at the next sync.
            if (response.StatusCode is not (400 or 404 or 409) || ReadError(response) is not { } error)
                continue;
            var operation = await _db.DatasyncOperationsQueue.AsNoTracking().SingleAsync(o => o.Id == operationId);
            var (stopId, outcome) = operation.EntityType == typeof(Delivery).FullName
                ? JsonSerializer.Deserialize<Delivery>(operation.Item, DatasyncSerializer.JsonSerializerOptions) is { } d ? (d.StopId, d.Outcome) : ("", "")
                : JsonSerializer.Deserialize<Stop>(operation.Item, DatasyncSerializer.JsonSerializerOptions) is { } s ? (s.Id, s.Status) : ("", "");
            var stop = await _db.Stops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == stopId);
            LastRejection = new Rejection(stop?.Sequence, outcome, error);
        }
        MainThread.BeginInvokeOnMainThread(() => RejectionChanged?.Invoke(this, EventArgs.Empty));
    }

    static string? ReadError(ServiceResponse response)
    {
        try
        {
            response.ContentStream.Position = 0;
            using var json = JsonDocument.Parse(response.ContentStream);
            return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null; // e.g. a 412 carries the server's copy of the entity, not an error.
        }
    }

    /// <summary>Updates <see cref="StatusLine"/> from the operations queue and the last sync attempt.</summary>
    public async Task RefreshStatusLineAsync()
    {
        // The docs: "You can see what is in the operations queue by accessing the DatasyncOperationsQueue dataset".
        // It counts changed entities, not taps, so the line only says whether anything is pending.
        var pending = await UseAsync(db => db.DatasyncOperationsQueue.AnyAsync(o => o.State != OperationState.Completed));
        var line = _offline
            ? pending ? "Offline. Changes pending." : "Offline."
            : pending ? "Changes pending." : null;
        await MainThread.InvokeOnMainThreadAsync(() => StatusLine = line);
    }
}
