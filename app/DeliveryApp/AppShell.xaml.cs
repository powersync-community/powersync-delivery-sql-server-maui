using DeliveryApp.Views;

namespace DeliveryApp;

public static class Routes
{
    public const string Stop = "stop";
}

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Stop, typeof(StopPage));
    }
}
