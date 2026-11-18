using System.Collections.ObjectModel;
using System.Net;
using DeliveryApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeliveryApp.ViewModels;

public partial class StopViewModel(ApiClient api) : ObservableObject, IQueryAttributable
{
    public const string StopParameter = "stop";

    /// <summary>Earlier delivery attempts, oldest first.</summary>
    public ObservableCollection<Delivery> Deliveries { get; } = [];

    /// <summary>Starts as the stop from the list, then whatever the API last returned.</summary>
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

    /// <summary>Text of the dismissable error banner, or null when there's nothing to show.</summary>
    [ObservableProperty]
    string? errorMessage;

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

    public async Task OnAppearingAsync()
    {
        try
        {
            await ReloadAsync();
            ErrorMessage = null;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ErrorMessages.For(ex);
        }
    }

    [ObservableProperty]
    bool isRefreshing;

    /// <summary>Pull-to-refresh. No polling: attempts recorded elsewhere, or a cancellation by dispatch,
    /// only show up here after a refresh (or when the page appears again).</summary>
    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            await OnAppearingAsync();
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

    async Task RecordAsync(string outcome)
    {
        if (Stop is not { } stop)
            return;

        IsBusy = true;
        try
        {
            var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
            var result = await api.RecordDeliveryAsync(stop.Id, outcome, note);
            Stop = result.Stop;
            Note = "";
            ErrorMessage = null;
            // Reload rather than appending result.Delivery: attempts recorded elsewhere since the page
            // opened would otherwise stay missing. If the reload fails, show at least our own attempt.
            try
            {
                await ReloadAsync();
            }
            catch (ApiException)
            {
                if (Deliveries.All(d => d.Id != result.Delivery.Id))
                    Deliveries.Add(result.Delivery);
                OnPropertyChanged(nameof(ShowNoAttempts));
            }
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // The server said no (e.g. dispatch canceled the stop). Show why, then show its real status.
            ErrorMessage = ex.Message;
            try
            {
                await ReloadAsync();
            }
            catch (ApiException)
            {
                // Keep the server's message; it's the more useful one.
            }
        }
        catch (ApiException ex)
        {
            // Keep the note so the driver can press the button again. No queueing, no automatic retry.
            ErrorMessage = ErrorMessages.For(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    async Task ReloadAsync()
    {
        if (Stop is not { } stop)
            return;
        var detail = await api.GetStopAsync(stop.Id);
        Stop = detail.Stop;
        Deliveries.Clear();
        foreach (var delivery in detail.Deliveries)
            Deliveries.Add(delivery);
        HasLoaded = true;
        OnPropertyChanged(nameof(ShowNoAttempts));
    }
}
