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

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.OnAppearing();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnDisappearing();
    }
}
