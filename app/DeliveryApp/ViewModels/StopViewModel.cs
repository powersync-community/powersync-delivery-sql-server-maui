using System.Collections.ObjectModel;
using DeliveryApp.Data;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeliveryApp.ViewModels;

public partial class StopViewModel(Database database) : ObservableObject, IQueryAttributable
{
    public const string StopParameter = "stop";

    /// <summary>Earlier delivery attempts, oldest first.</summary>
    public ObservableCollection<Delivery> Deliveries { get; } = [];

    /// <summary>The page binds <c>Database.StatusLine</c>.</summary>
    public Database Database => database;

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

    [ObservableProperty]
    bool isRefreshing;

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
    /// Shows the stop and its attempts from the local database, and again after every sync and local
    /// write: a cancellation by dispatch, or an attempt recorded on another device, shows up after the next sync.
    /// </summary>
    public async Task OnAppearingAsync()
    {
        database.Changed += OnChanged;
        await ReloadAsync();
    }

    public void OnDisappearing() => database.Changed -= OnChanged;

    void OnChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await ReloadAsync());

    async Task ReloadAsync()
    {
        if (Stop is not { } current)
            return;
        Stop = await database.GetStopAsync(current.Id) ?? current;
        var deliveries = await database.GetDeliveriesAsync(current.Id);
        Deliveries.Clear();
        foreach (var delivery in deliveries)
            Deliveries.Add(delivery);
        HasLoaded = true;
        OnPropertyChanged(nameof(ShowNoAttempts));
    }

    /// <summary>Pull-to-refresh: a sync. The page reloads after it.</summary>
    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            await database.SyncAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    bool CanSend() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSend))]
    Task DeliveredAsync() => RecordAsync(Outcomes.Delivered);

    [RelayCommand(CanExecute = nameof(CanSend))]
    Task FailedAsync() => RecordAsync(Outcomes.Failed);

    /// <summary>
    /// Writes the attempt to the local database, which works offline. The next sync uploads it; the server
    /// still decides whether the stop could take it.
    /// </summary>
    async Task RecordAsync(string outcome)
    {
        if (Stop is not { } stop)
            return;

        IsBusy = true;
        try
        {
            var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
            await database.RecordDeliveryAsync(stop, outcome, note);
            Note = "";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
