using DeliveryApp.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeliveryApp.ViewModels;

/// <summary>
/// What both pages show about sync: the status line (from <see cref="Database"/>), and the banner for
/// a write the server refused (from <see cref="DeliveryConnector"/>).
/// </summary>
public abstract partial class SyncedViewModel(Database database, DeliveryConnector connector) : ObservableObject
{
    /// <summary>The pages bind <c>Database.StatusLine</c>.</summary>
    public Database Database => database;

    /// <summary>The latest rejected write, in the dismissable banner. Dismissing it clears it on both pages.</summary>
    [ObservableProperty]
    string? errorMessage;

    partial void OnErrorMessageChanged(string? value)
    {
        if (value is null && connector.LastRejection is not null)
            connector.ClearRejection();
    }

    protected void StartShowingRejections()
    {
        connector.RejectionChanged += OnRejectionChanged;
        ShowRejection();
    }

    protected void StopShowingRejections() => connector.RejectionChanged -= OnRejectionChanged;

    void OnRejectionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(ShowRejection);

    void ShowRejection() =>
        ErrorMessage = connector.LastRejection is { } rejection ? ErrorMessages.For(rejection) : null;
}
