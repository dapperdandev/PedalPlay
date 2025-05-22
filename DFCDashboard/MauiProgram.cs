using Microsoft.Extensions.Logging;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;

namespace DFCDashboard
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            // Register Bluetooth services
            builder.Services.AddSingleton<IBluetoothLE>(_ => CrossBluetoothLE.Current);
            builder.Services.AddSingleton<IAdapter>(_ => 
            {
                var adapter = CrossBluetoothLE.Current.Adapter ?? throw new InvalidOperationException("Bluetooth adapter not available");
                
                // Configure the adapter
                adapter.ScanTimeout = 10000; // 10 seconds
                adapter.ScanMode = Plugin.BLE.Abstractions.Contracts.ScanMode.LowLatency;
                
                return adapter;
            });
            
            // Register services
            builder.Services.AddSingleton<Services.CyclingDataService>();
            
            builder.Services.AddMauiBlazorWebView();
            
            // Register pages
            builder.Services.AddSingleton<MainPage>();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            // Build the app
            var app = builder.Build();
            
            // Get the service provider
            var serviceProvider = app.Services;
            
            // Start the auto-reconnect process in the background
            _ = Task.Run(async () =>
            {
                try
                {
                    // Give the app a moment to fully initialize
                    await Task.Delay(2000);
                    
                    var cyclingData = serviceProvider.GetRequiredService<Services.CyclingDataService>();
                    
                    // Try to reconnect every 5 seconds until successful or bluetooth becomes available
                    while (!await cyclingData.TryReconnectLastDeviceAsync())
                    {
                        // Check if Bluetooth is available
                        var ble = serviceProvider.GetRequiredService<IBluetoothLE>();
                        if (!ble.IsAvailable || !ble.IsOn)
                        {
                            break;
                        }
                        await Task.Delay(5000);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Auto-reconnect error: {ex.Message}");
                }
            });

            return app;
        }
    }
}
