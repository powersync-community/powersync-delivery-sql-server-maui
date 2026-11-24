using System.Collections.ObjectModel;
using DeliveryApp.Data;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerSync.Common.Client;

namespace DeliveryApp.ViewModels;

public partial class StopViewModel(Database database, DeliveryConnector connector)
    : SyncedViewModel(database, connector), IQueryAttributable
{
    public const string StopParameter = "stop";

    CancellationTokenSource? _watching;

    /// <summary>Earlier delivery attempts, oldest first.</summary>
    public ObservableCollection<Delivery> Deliveries { get; } = [];

    /// <summary>Starts as the stop from the list, then whatever the local database says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecordOutcome), nameof(ClosedMessage))]
    Stop? stop;

    [ObservableProperty]
    string note = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeliveredCommand), nameof(FailedCommand))]
    bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoAttempts))]
    bool hasLoaded;

    /// <summary>Assigned and failed stops can take an outcome. A failed stop can be attempted again.</summary>
    public bool CanRecordOutcome => Stop?.Status is StopStatus.Assigned or StopStatus.Failed;

    /// <summary>Shown instead of the buttons once the stop is closed.</summary>
    public string? ClosedMessage => Stop?.Status switch
    {
        StopStatus.Delivered => "This stop is delivered.",
        StopStatus.Canceled => "Dispatch canceled this stop.",
        _ => null,
    };

    public bool ShowNoAttempts => HasLoaded && Deliveries.Count == 0;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue(StopParameter, out var value) && value is Stop stop)
            Stop = stop;
    }

    /// <summary>
    /// Shows the stop and its attempts from the local database, live: a cancellation by dispatch, or an
    /// attempt recorded on another device, shows up as soon as it syncs.
    /// </summary>
    public void OnAppearing()
    {
        StartShowingRejections();
        if (_watching is not null || Stop is not { } stop)
            return;
        _watching = new CancellationTokenSource();
        _ = WatchStopAsync(stop.Id, _watching.Token);
        _ = WatchDeliveriesAsync(stop.Id, _watching.Token);
    }

    public void OnDisappearing()
    {
        StopShowingRejections();
        _watching?.Cancel();
        _watching = null;
    }

    async Task WatchStopAsync(string stopId, CancellationToken ct)
    {
        try
        {
            var results = Database.Db.Watch<Stop>(
                "SELECT * FROM stops WHERE id = ?", [stopId],
                new SQLWatchOptions { Signal = ct, TriggerImmediately = true });
            await foreach (var stops in results.WithCancellation(ct))
            {
                if (stops.FirstOrDefault() is { } stop)
                    await MainThread.InvokeOnMainThreadAsync(() => Stop = stop);
            }
        }
        catch (OperationCanceledException)
        {
            // The page disappeared.
        }
    }

    async Task WatchDeliveriesAsync(string stopId, CancellationToken ct)
    {
        try
        {
            var results = Database.Db.Watch<Delivery>(
                "SELECT * FROM deliveries WHERE stop_id = ? ORDER BY recorded_at", [stopId],
                new SQLWatchOptions { Signal = ct, TriggerImmediately = true });
            await foreach (var deliveries in results.WithCancellation(ct))
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Deliveries.Clear();
                    foreach (var delivery in deliveries)
                        Deliveries.Add(delivery);
                    HasLoaded = true;
                    OnPropertyChanged(nameof(ShowNoAttempts));
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The page disappeared.
        }
    }

    bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    Task DeliveredAsync() => RecordAsync(Outcomes.Delivered);

    [RelayCommand(CanExecute = nameof(CanSend))]
    Task FailedAsync() => RecordAsync(Outcomes.Failed);

    /// <summary>
    /// Writes the attempt to the local database, which works offline. PowerSync queues it and
    /// DeliveryConnector uploads it; the server still decides whether the stop could take it.
    /// </summary>
    async Task RecordAsync(string outcome)
    {
        if (Stop is not { } stop)
            return;

        IsBusy = true;
        try
        {
            var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
            await Database.Db.WriteTransaction(async tx =>
            {
                await tx.Execute(
                    "INSERT INTO deliveries (id, stop_id, driver_id, outcome, note, recorded_at) VALUES (?, ?, ?, ?, ?, ?)",
                    [AppSchema.NewId(), stop.Id, stop.DriverId, outcome, note, AppSchema.UtcNow()]);
                await tx.Execute("UPDATE stops SET status = ? WHERE id = ?", [outcome, stop.Id]);
            });
            Note = "";
            await Database.RefreshStatusLineAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
