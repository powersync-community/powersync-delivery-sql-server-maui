using DeliveryApp.Data;

namespace DeliveryApp;

public partial class App : Application
{
    public App(Database database)
    {
        InitializeComponent();
        // Open the local database and start syncing, at startup rather than on first page load.
        // The pages read local data, so they don't wait for this.
        _ = database.StartAsync();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new AppShell());
}
