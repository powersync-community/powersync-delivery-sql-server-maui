using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using DeliveryApp.Services;
using Dotmim.Sync;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;

namespace DeliveryApp.Data;

/// <summary>
/// Owns the app's local database: a SQLite file that Dotmim.Sync keeps in sync with SQL Server, one
/// sync session at a time. Every screen reads from it, and every write goes into it first. Nothing
/// happens by itself: a sync runs at startup, on pull-to-refresh and after a local write.
/// </summary>
public partial class Database : ObservableObject
{
    // Foreign Keys=False: Dotmim.Sync copies deliveries' foreign key (stop_id, driver_id) to SQLite but not
    // the unique constraint on stops (id, driver_id) it points at, and SQLite then refuses every write to
    // deliveries ("foreign key mismatch"). SQL Server still enforces it. See NOTES.md.
    static readonly string ConnectionString =
        $"Data Source={Path.Combine(FileSystem.AppDataDirectory, "delivery.db")};Foreign Keys=False";

    readonly ApiClient _api;
    // The token goes on this client's default headers before each sync (as in the docs' auth sample).
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly SyncAgent _agent;
    readonly SemaphoreSlim _syncLock = new(1, 1);
    bool _offline;

    /// <summary>One line of sync state for the pages, or null when the last sync worked and nothing is waiting.</summary>
    [ObservableProperty]
    string? statusLine;

    /// <summary>Raised after every sync and every local write. Dotmim.Sync has no watched queries, so pages re-query.</summary>
    public event EventHandler? Changed;

    public Database(ApiClient api)
    {
        _api = api;
        var server = new WebRemoteOrchestrator(AppConfig.SyncUrl, client: _http);
        // Where Dotmim.Sync keeps batch files during a sync, as in the MAUI sample.
        var options = new SyncOptions { BatchDirectory = Path.Combine(FileSystem.AppDataDirectory, "dms") };
        _agent = new SyncAgent(new SqliteSyncProvider(ConnectionString), server, options);
    }

    /// <summary>
    /// One sync session: upload local changes, then download the server's. The first one also creates the
    /// local tables. Waits for a sync that's already running, then runs another, so a write made during a
    /// sync isn't left behind. Never throws: a failure only shows in the status line.
    /// </summary>
    public async Task SyncAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _api.GetTokenAsync());
            await _agent.SynchronizeAsync();
            _offline = false;
        }
        catch (Exception ex)
        {
            // Dotmim.Sync already retried each request twice. Nothing is lost: local changes stay in the
            // tracking tables until a later sync uploads them. But nothing retries later by itself either.
            if (ex is HttpSyncWebException { StatusCode: HttpStatusCode.Unauthorized })
                _api.ClearToken();
            _offline = IsUnreachable(ex);
            Console.WriteLine($"Sync failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _syncLock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshStatusLineAsync();
    }

    public Task<List<Stop>> GetStopsAsync(string routeDate) =>
        QueryAsync($"SELECT {Stop.Columns} FROM stops WHERE route_date = $p0 ORDER BY sequence", Stop.From, routeDate);

    public async Task<Stop?> GetStopAsync(string id) =>
        (await QueryAsync($"SELECT {Stop.Columns} FROM stops WHERE id = $p0", Stop.From, id)).FirstOrDefault();

    public Task<List<Delivery>> GetDeliveriesAsync(string stopId) =>
        QueryAsync($"SELECT {Delivery.Columns} FROM deliveries WHERE stop_id = $p0 ORDER BY recorded_at", Delivery.From, stopId);

    /// <summary>
    /// Records an outcome locally (works offline): the delivery and the stop's new status, in one transaction.
    /// Dotmim.Sync's triggers track both rows; the next sync uploads them, and the server decides whether
    /// the stop could take them. Then starts that sync.
    /// </summary>
    public async Task RecordDeliveryAsync(Stop stop, string outcome, string? note)
    {
        var now = LocalValues.UtcNow();
        await using (var connection = await OpenAsync())
        await using (var tx = connection.BeginTransaction())
        {
            await ExecuteAsync(tx,
                "INSERT INTO deliveries (id, stop_id, driver_id, outcome, note, recorded_at) VALUES ($p0, $p1, $p2, $p3, $p4, $p5)",
                Guid.NewGuid().ToString(), stop.Id, stop.DriverId, outcome, note, now);
            await ExecuteAsync(tx, "UPDATE stops SET status = $p0, updated_at = $p1 WHERE id = $p2", outcome, now, stop.Id);
            await tx.CommitAsync();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (_offline)
            await RefreshStatusLineAsync();
        _ = SyncAsync();
    }

    /// <summary>
    /// Updates <see cref="StatusLine"/>. "Offline" means the last sync couldn't reach the server; "pending"
    /// is Dotmim.Sync's estimate of local changes not uploaded yet.
    /// </summary>
    async Task RefreshStatusLineAsync()
    {
        var pending = await HasPendingChangesAsync();
        var line = _offline
            ? pending ? "Offline. Changes pending." : "Offline."
            : pending ? "Changes pending." : null;
        await MainThread.InvokeOnMainThreadAsync(() => StatusLine = line);
    }

    async Task<bool> HasPendingChangesAsync()
    {
        try
        {
            var scope = await _agent.LocalOrchestrator.GetScopeInfoClientAsync();
            var changes = await _agent.LocalOrchestrator.GetEstimatedChangesCountAsync(scope);
            return changes.ClientChangesSelected.TotalChangesSelected > 0;
        }
        catch (Exception)
        {
            return false; // Not provisioned yet: there's been no sync, so there's nothing local to upload.
        }
    }

    /// <summary>No network, the server's down, or the token endpoint couldn't be reached.</summary>
    static bool IsUnreachable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // Dotmim.Sync reports a request that got no response as HttpSyncWebException with status 0
            // (e.g. "Connection refused (localhost:7070)"), with no inner exception.
            if (e is HttpSyncWebException web && (int)web.StatusCode == 0)
                return true;
            if (e is HttpRequestException or SocketException or TimeoutException or TaskCanceledException
                or ApiException { StatusCode: null })
                return true;
        }
        return false;
    }

    static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    static SqliteCommand Command(SqliteConnection connection, string sql, object?[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            command.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
        return command;
    }

    static async Task ExecuteAsync(SqliteTransaction tx, string sql, params object?[] args)
    {
        await using var command = Command(tx.Connection!, sql, args);
        command.Transaction = tx;
        await command.ExecuteNonQueryAsync();
    }

    static async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, params object?[] args)
    {
        var rows = new List<T>();
        try
        {
            await using var connection = await OpenAsync();
            await using var command = Command(connection, sql, args);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(read(reader));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // "no such table": the first sync hasn't created the local tables yet.
        }
        return rows;
    }
}
