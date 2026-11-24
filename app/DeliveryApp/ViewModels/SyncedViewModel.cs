using DeliveryApp.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeliveryApp.ViewModels;

/// <summary>
/// What both pages show about sync: the status line, and the banner for a write the server refused
/// (both from <see cref="Database"/>).
/// </summary>
public abstract partial class SyncedViewModel(Database database) : ObservableObject
{
    /// <summary>The pages bind <c>Database.StatusLine</c>.</summary>
    public Database Database => database;

    /// <summary>The latest rejected write, in the dismissable banner. Dismissing it clears it on both pages.</summary>
    [ObservableProperty]
    string? errorMessage;

    partial void OnErrorMessageChanged(string? value)
    {
        if (value is null && database.LastRejection is not null)
            database.ClearRejection();
    }

    protected void StartShowingRejections()
    {
        database.RejectionChanged += OnRejectionChanged;
        ShowRejection();
    }

    protected void StopShowingRejections() => database.RejectionChanged -= OnRejectionChanged;

    void OnRejectionChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(ShowRejection);

    void ShowRejection() =>
        ErrorMessage = database.LastRejection is { } rejection ? ErrorMessages.For(rejection) : null;
}
