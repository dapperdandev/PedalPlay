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
            
            // Register the MainPage
            builder.Services.AddSingleton<MainPage>();
            
            // Register the App class
            builder.Services.AddSingleton<App>();
            
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
                    Console.WriteLine("App started, preparing for auto-reconnect...");
                    
                    // Give the app a moment to fully initialize
                    await Task.Delay(2000);
                    
                    var cyclingData = serviceProvider.GetRequiredService<Services.CyclingDataService>();
                    Console.WriteLine("Attempting to reconnect to last device...");
                    var success = await cyclingData.TryReconnectLastDeviceAsync();
                    Console.WriteLine($"Auto-reconnect {(success ? "succeeded" : "failed")}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during auto-reconnect: {ex}");
                }
            });
            
            return app;
        }
    }
}
