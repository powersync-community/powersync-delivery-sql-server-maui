using DeliveryApp.Data;

namespace DeliveryApp;

public partial class App : Application
{
    public App(Database database)
    {
        InitializeComponent();
        // First sync at startup: it creates the local tables and downloads the route. The pages read the
        // local database, so they don't wait for it.
        _ = database.SyncAsync();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new AppShell());
}
