using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;
using PowerSync.Common.DB.Crud;
using PowerSync.Common.MDSQLite;

namespace DeliveryApp.Data;

/// <summary>
/// Owns the app's local database: a SQLite file that PowerSync keeps in sync with SQL Server.
/// Every screen reads from it, and every write goes into it first.
/// </summary>
public partial class Database : ObservableObject
{
    readonly DeliveryConnector _connector;
    readonly ILogger<Database> _logger;
    readonly SemaphoreSlim _statusLock = new(1, 1);
    Task? _started;

    public PowerSyncDatabase Db { get; }

    /// <summary>One line of sync state for the pages, or null when online with nothing waiting to upload.</summary>
    [ObservableProperty]
    string? statusLine;

    public Database(DeliveryConnector connector, ILogger<Database> logger)
    {
        _connector = connector;
        _logger = logger;
        Db = new PowerSyncDatabase(new PowerSyncDatabaseOptions
        {
            Database = new MDSQLiteDBOpenFactory(new MDSQLiteOpenFactoryOptions
            {
                DbFilename = Path.Combine(FileSystem.AppDataDirectory, "delivery.db"),
            }),
            Schema = AppSchema.Schema,
            Logger = logger,
        });
    }

    /// <summary>Opens the database and starts syncing. Call once at startup; works offline too.</summary>
    public Task StartAsync() => _started ??= StartCoreAsync();

    async Task StartCoreAsync()
    {
        await Db.Init();
        _ = WatchStatusAsync();
        await RefreshStatusLineAsync();
        // Connect keeps retrying in the background while the server is unreachable.
        await Db.Connect(_connector);
    }

    async Task WatchStatusAsync()
    {
        try
        {
            await foreach (var _ in Db.Events.OnStatusChanged.ListenAsync(CancellationToken.None))
                await RefreshStatusLineAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync status listener stopped");
        }
    }

    /// <summary>
    /// Checks the upload queue and updates <see cref="StatusLine"/>. Runs on every sync status change;
    /// call it after a local write too, since that doesn't change the sync status while offline.
    /// </summary>
    public async Task RefreshStatusLineAsync()
    {
        await _statusLock.WaitAsync();
        try
        {
            // Count is in row changes (one outcome is two: the delivery and the stop's status), so the
            // line only says whether anything is pending, not how much.
            var pending = (await Db.GetUploadQueueStats()).Count > 0;
            var line = Describe(Db.CurrentStatus, pending);
            await MainThread.InvokeOnMainThreadAsync(() => StatusLine = line);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    static string? Describe(SyncStatus status, bool pending)
    {
        // Not connected *and* a failed attempt: offline. (Not just "not connected yet", right after launch.)
        var offline = !status.Connected && status.DataFlowStatus.DownloadError is not null;
        if (offline)
            return pending ? "Offline. Changes pending." : "Offline.";
        if (!pending)
            return null;
        // Online, but uploads are failing (e.g. the API is down while PowerSync is up). Otherwise they're
        // on their way and gone in a moment, so don't flash a line for them.
        return status.DataFlowStatus.UploadError is not null || !status.Connected ? "Changes pending." : null;
    }
}
