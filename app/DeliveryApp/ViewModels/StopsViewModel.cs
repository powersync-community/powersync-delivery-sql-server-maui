using System.Collections.ObjectModel;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeliveryApp.ViewModels;

public partial class StopsViewModel(ApiClient api, DriverIdentity identity) : ObservableObject
{
    public ObservableCollection<Stop> Stops { get; } = [];

    public string DriverLabel { get; } = $"Driver: {identity.DriverId}";

    [ObservableProperty]
    bool isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    bool hasLoaded;

    /// <summary>Text of the dismissable error banner, or null when there's nothing to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    string? errorMessage;

    [ObservableProperty]
    Stop? selectedStop;

    /// <summary>"No stops today" only makes sense when the list actually loaded.</summary>
    public bool ShowEmptyState => HasLoaded && ErrorMessage is null && Stops.Count == 0;

    /// <summary>
    /// Called every time the page appears, and by pull-to-refresh. No polling: a stop that dispatch
    /// cancels only shows up here after the next refresh.
    /// </summary>
    [RelayCommand]
    async Task LoadAsync()
    {
        try
        {
            var stops = await api.GetStopsAsync();
            Stops.Clear();
            foreach (var stop in stops)
                Stops.Add(stop);
            ErrorMessage = null;
            HasLoaded = true;
        }
        catch (ApiException ex)
        {
            // No cache: show what the API last returned, which is nothing.
            Stops.Clear();
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(ShowEmptyState));
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
