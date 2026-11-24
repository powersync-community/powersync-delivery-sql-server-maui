using System.Collections.ObjectModel;
using DeliveryApp.Data;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerSync.Common.Client;

namespace DeliveryApp.ViewModels;

public partial class StopsViewModel(Database database, DeliveryConnector connector, DriverIdentity identity)
    : SyncedViewModel(database, connector)
{
    CancellationTokenSource? _watching;

    public ObservableCollection<Stop> Stops { get; } = [];

    public string DriverLabel { get; } = $"Driver: {identity.DriverId}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    bool hasLoaded;

    [ObservableProperty]
    Stop? selectedStop;

    public bool ShowEmptyState => HasLoaded && Stops.Count == 0;

    /// <summary>
    /// Shows today's stops from the local database, and keeps showing them as sync changes them: a stop
    /// that dispatch adds or cancels appears without the driver doing anything. Works offline.
    /// </summary>
    public void OnAppearing()
    {
        StartShowingRejections();
        if (_watching is not null)
            return;
        _watching = new CancellationTokenSource();
        _ = WatchStopsAsync(_watching.Token);
    }

    public void OnDisappearing()
    {
        StopShowingRejections();
        _watching?.Cancel();
        _watching = null;
    }

    async Task WatchStopsAsync(CancellationToken ct)
    {
        try
        {
            // TriggerImmediately: emit the current rows now, not only on the next change.
            var results = Database.Db.Watch<Stop>(
                "SELECT * FROM stops WHERE route_date = ? ORDER BY sequence", [AppSchema.Today()],
                new SQLWatchOptions { Signal = ct, TriggerImmediately = true });
            await foreach (var stops in results.WithCancellation(ct))
            {
                // Each result is the whole list. With 8-10 stops, just replace it.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Stops.Clear();
                    foreach (var stop in stops)
                        Stops.Add(stop);
                    HasLoaded = true;
                    OnPropertyChanged(nameof(ShowEmptyState));
                });
            }
        }
        catch (OperationCanceledException)
        {
            // The page disappeared.
        }
    }

    [RelayCommand]
    async Task OpenStopAsync()
    {
        if (SelectedStop is not { } stop)
            return;
        SelectedStop = null;
        await Shell.Current.GoToAsync(Routes.Stop, new ShellNavigationQueryParameters { [StopViewModel.StopParameter] = stop });
    }
}
