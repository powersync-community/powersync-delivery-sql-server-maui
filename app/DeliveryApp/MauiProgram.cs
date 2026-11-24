using DeliveryApp.Data;
using DeliveryApp.Services;
using DeliveryApp.ViewModels;
using DeliveryApp.Views;

namespace DeliveryApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<DriverIdentity>();
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<Database>();

        builder.Services.AddTransient<StopsViewModel>();
        builder.Services.AddTransient<StopsPage>();
        builder.Services.AddTransient<StopViewModel>();
        builder.Services.AddTransient<StopPage>();

        return builder.Build();
    }
}
