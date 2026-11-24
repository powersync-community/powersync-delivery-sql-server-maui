using DeliveryApp.Data;
using DeliveryApp.Services;
using DeliveryApp.ViewModels;
using DeliveryApp.Views;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton<DriverIdentity>();
        builder.Services.AddSingleton<ApiClient>();
        // One local database for the app's lifetime. Database serializes access to it.
        builder.Services.AddDbContext<DeliveryDbContext>(
            o => o.UseSqlite($"Data Source={Path.Combine(FileSystem.AppDataDirectory, "delivery.db")}"),
            ServiceLifetime.Singleton);
        builder.Services.AddSingleton<Database>();

        builder.Services.AddTransient<StopsViewModel>();
        builder.Services.AddTransient<StopsPage>();
        builder.Services.AddTransient<StopViewModel>();
        builder.Services.AddTransient<StopPage>();

        return builder.Build();
    }
}
