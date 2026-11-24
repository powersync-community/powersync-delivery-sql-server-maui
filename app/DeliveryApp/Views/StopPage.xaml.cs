using DeliveryApp.ViewModels;

namespace DeliveryApp.Views;

public partial class StopPage : ContentPage
{
    readonly StopViewModel _viewModel;

    public StopPage(StopViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    /// <summary>Close the keyboard when an outcome button is tapped (the command does the actual work).</summary>
    void OnOutcomeClicked(object? sender, EventArgs e) => NoteEntry.Unfocus();

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnDisappearing();
    }
}
