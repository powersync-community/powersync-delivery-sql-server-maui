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
        // Reload every time the page appears (e.g. coming back from a stop). No polling.
        _viewModel.LoadCommand.Execute(null);
    }
}
