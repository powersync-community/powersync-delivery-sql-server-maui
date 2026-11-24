using System.Collections.ObjectModel;
using DeliveryApp.Data;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeliveryApp.ViewModels;

public partial class StopsViewModel(Database database, DriverIdentity identity) : ObservableObject
{
    public ObservableCollection<Stop> Stops { get; } = [];

    public string DriverLabel { get; } = $"Driver: {identity.DriverId}";

    /// <summary>The page binds <c>Database.StatusLine</c>.</summary>
    public Database Database => database;

    [ObservableProperty]
    bool isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    bool hasLoaded;

    [ObservableProperty]
    Stop? selectedStop;

    public bool ShowEmptyState => HasLoaded && Stops.Count == 0;

    /// <summary>
    /// Shows today's stops from the local database, and shows them again after every sync and local write.
    /// A stop that dispatch adds or cancels only appears after the next sync. Works offline.
    /// </summary>
    public async Task OnAppearingAsync()
    {
        database.Changed += OnChanged;
        await LoadAsync();
    }

    public void OnDisappearing() => database.Changed -= OnChanged;

    void OnChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () => await LoadAsync());

    async Task LoadAsync()
    {
        var stops = await database.GetStopsAsync(LocalValues.Today());
        // The whole list each time. With 8-10 stops, just replace it.
        Stops.Clear();
        foreach (var stop in stops)
            Stops.Add(stop);
        HasLoaded = true;
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Pull-to-refresh: a sync, which is the only way to hear about dispatch's changes. The list reloads after it.</summary>
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

    [RelayCommand]
    async Task OpenStopAsync()
    {
        if (SelectedStop is not { } stop)
            return;
        SelectedStop = null;
        await Shell.Current.GoToAsync(Routes.Stop, new ShellNavigationQueryParameters { [StopViewModel.StopParameter] = stop });
    }
}
