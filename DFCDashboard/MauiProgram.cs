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
            
            // Register the ConnectTrainer page
            builder.Services.AddScoped<DFCDashboard.Components.Pages.ConnectTrainer>();
            
            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
