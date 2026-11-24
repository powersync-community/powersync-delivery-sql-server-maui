using System.Collections.ObjectModel;
using DeliveryApp.Data;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.ViewModels;

public partial class StopViewModel(Database database) : SyncedViewModel(database), IQueryAttributable
{
    public const string StopParameter = "stop";

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

    /// <summary>Show the stop from the local database, then sync and show it again (as the list does).</summary>
    public async Task OnAppearingAsync()
    {
        StartShowingRejections();
        await ShowLocalAsync();
        await SyncAsync();
    }

    public void OnDisappearing() => StopShowingRejections();

    [ObservableProperty]
    bool isRefreshing;

    /// <summary>Pull-to-refresh: sync now. No polling.</summary>
    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            await SyncAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    async Task SyncAsync()
    {
        await Database.SynchronizeAsync();
        await ShowLocalAsync();
    }

    async Task ShowLocalAsync()
    {
        if (Stop is not { } current)
            return;
        var (stop, deliveries) = await Database.UseAsync(async db => (
            await db.Stops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == current.Id),
            await db.Deliveries.AsNoTracking().Where(d => d.StopId == current.Id).OrderBy(d => d.RecordedAt).ToListAsync()));
        if (stop is not null)
            Stop = stop;
        Deliveries.Clear();
        foreach (var delivery in deliveries)
            Deliveries.Add(delivery);
        HasLoaded = true;
        OnPropertyChanged(nameof(ShowNoAttempts));
    }

    bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    Task DeliveredAsync() => RecordAsync(Outcomes.Delivered);

    [RelayCommand(CanExecute = nameof(CanSend))]
    Task FailedAsync() => RecordAsync(Outcomes.Failed);

    /// <summary>
    /// Writes the attempt to the local database, which works offline: a new delivery, and the stop's status.
    /// SaveChanges puts both in Datasync's operations queue; they upload at the next sync, and the server
    /// still decides whether the stop could take them.
    /// </summary>
    async Task RecordAsync(string outcome)
    {
        if (Stop is not { } current)
            return;

        IsBusy = true;
        try
        {
            var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
            await Database.UseAsync(async db =>
            {
                var stop = await db.Stops.SingleAsync(s => s.Id == current.Id);
                stop.Status = outcome;
                db.Deliveries.Add(new Delivery
                {
                    // Lower-case GUID text with dashes: the form the server sends IDs back in. Any other form
                    // (the sample's "N", or upper case) comes back as a second, different row on the next pull.
                    Id = Guid.NewGuid().ToString(),
                    StopId = stop.Id,
                    DriverId = stop.DriverId,
                    Outcome = outcome,
                    Note = note,
                    RecordedAt = DateTime.UtcNow,
                });
                return await db.SaveChangesAsync();
            });
            Note = "";
            await ShowLocalAsync();
            await Database.RefreshStatusLineAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
