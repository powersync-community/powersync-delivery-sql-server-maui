using DeliveryApp.ViewModels;

namespace DeliveryApp.Views;

public partial class StopsPage : ContentPage
{
    readonly StopsViewModel _viewModel;

    public StopsPage(StopsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Reload from the local database every time the page appears (e.g. coming back from a stop).
        await _viewModel.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnDisappearing();
    }
}
