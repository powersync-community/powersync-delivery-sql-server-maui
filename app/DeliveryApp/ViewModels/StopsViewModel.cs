using System.Collections.ObjectModel;
using DeliveryApp.Data;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.ViewModels;

public partial class StopsViewModel(Database database, DriverIdentity identity) : SyncedViewModel(database)
{
    public ObservableCollection<Stop> Stops { get; } = [];

    public string DriverLabel { get; } = $"Driver: {identity.DriverId}";

    [ObservableProperty]
    bool isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    bool hasLoaded;

    [ObservableProperty]
    Stop? selectedStop;

    public bool ShowEmptyState => HasLoaded && Stops.Count == 0;

    /// <summary>
    /// Every time the page appears: show today's stops from the local database at once (works offline), then
    /// sync and show them again. No polling: a stop that dispatch cancels shows up at the next sync.
    /// </summary>
    public async Task OnAppearingAsync()
    {
        StartShowingRejections();
        await ShowLocalAsync();
        await SyncAsync();
    }

    public void OnDisappearing() => StopShowingRejections();

    /// <summary>Pull-to-refresh: sync now.</summary>
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
        var today = DateOnly.FromDateTime(DateTime.UtcNow); // The UTC date, like the server's route_date.
        var stops = await Database.UseAsync(db =>
            db.Stops.AsNoTracking().Where(s => s.RouteDate == today).OrderBy(s => s.Sequence).ToListAsync());
        Stops.Clear();
        foreach (var stop in stops)
            Stops.Add(stop);
        HasLoaded = true;
        OnPropertyChanged(nameof(ShowEmptyState));
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
