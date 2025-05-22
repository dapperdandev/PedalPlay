using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DFCDashboard.Services;

public class CyclingDataService : INotifyPropertyChanged
{
    private readonly IAdapter _adapter;
    private IDevice? _connectedDevice;
    private IService? _cyclingPowerService;
    private ICharacteristic? _powerMeasurementCharacteristic;
    private ICharacteristic? _cadenceMeasurementCharacteristic;
    
    // Bluetooth SIG-defined UUIDs for services and characteristics
    public static class BluetoothUuids
    {
        // Services
        public static readonly Guid CyclingPowerService = new("00001818-0000-1000-8000-00805f9b34fb");
        public static readonly Guid CyclingSpeedAndCadenceService = new("00001816-0000-1000-8000-00805f9b34fb");
        
        // Characteristics
        public static readonly Guid CyclingPowerMeasurement = new("00002A63-0000-1000-8000-00805f9b34fb");
        public static readonly Guid CyclingSpeedAndCadenceMeasurement = new("00002A5B-0000-1000-8000-00805f9b34fb");
        
        // Descriptors
        public static readonly Guid ClientCharacteristicConfiguration = new("00002902-0000-1000-8000-00805f9b34fb");
    }

    private int _power;
    private int _cadence;
    private bool _isConnected;

    public int Power
    {
        get => _power;
        private set => SetProperty(ref _power, value);
    }

    public int Cadence
    {
        get => _cadence;
        private set => SetProperty(ref _cadence, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public CyclingDataService(IAdapter adapter)
    {
        _adapter = adapter;
    }

    public async Task ConnectToDevice(IDevice device)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));

        try
        {
            _connectedDevice = device;
            
            // Connect to the device
            await _adapter.ConnectToDeviceAsync(device);
            
            // Discover services
            var services = await device.GetServicesAsync();
            
            // Try to find the Cycling Power Service or Cycling Speed and Cadence Service
            _cyclingPowerService = services.FirstOrDefault(s => s.Id == BluetoothUuids.CyclingPowerService) ??
                                 services.FirstOrDefault(s => s.Id == BluetoothUuids.CyclingSpeedAndCadenceService);
            
            if (_cyclingPowerService == null)
            {
                throw new Exception("Cycling Power or Speed and Cadence service not found");
            }
            
            // Get characteristics
            var characteristics = await _cyclingPowerService.GetCharacteristicsAsync();
            
            // Try to get power measurement characteristic
            _powerMeasurementCharacteristic = characteristics.FirstOrDefault(c => c.Id == BluetoothUuids.CyclingPowerMeasurement);
            
            // Try to get cadence measurement characteristic (could be in the same service or different one)
            _cadenceMeasurementCharacteristic = characteristics.FirstOrDefault(c => c.Id == BluetoothUuids.CyclingSpeedAndCadenceMeasurement);
            
            // If cadence not found in the same service, try to find it in the CSC service
            if (_cadenceMeasurementCharacteristic == null && _cyclingPowerService.Id != BluetoothUuids.CyclingSpeedAndCadenceService)
            {
                var cscService = services.FirstOrDefault(s => s.Id == BluetoothUuids.CyclingSpeedAndCadenceService);
                if (cscService != null)
                {
                    var cscCharacteristics = await cscService.GetCharacteristicsAsync();
                    _cadenceMeasurementCharacteristic = cscCharacteristics.FirstOrDefault(c => c.Id == BluetoothUuids.CyclingSpeedAndCadenceMeasurement);
                }
            }
            
            // Enable notifications for power measurement if available
            if (_powerMeasurementCharacteristic != null)
            {
                _powerMeasurementCharacteristic.ValueUpdated += OnPowerMeasurementUpdated;
                await _powerMeasurementCharacteristic.StartUpdatesAsync();
            }
            
            // Enable notifications for cadence measurement if available
            if (_cadenceMeasurementCharacteristic != null)
            {
                _cadenceMeasurementCharacteristic.ValueUpdated += OnCadenceMeasurementUpdated;
                await _cadenceMeasurementCharacteristic.StartUpdatesAsync();
            }
            
            IsConnected = true;
        }
        catch (Exception ex)
        {
            await Disconnect();
            throw new Exception($"Failed to connect to device: {ex.Message}", ex);
        }
    }

    public async Task Disconnect()
    {
        try
        {
            // Stop notifications
            if (_powerMeasurementCharacteristic != null)
            {
                _powerMeasurementCharacteristic.ValueUpdated -= OnPowerMeasurementUpdated;
                await _powerMeasurementCharacteristic.StopUpdatesAsync();
                _powerMeasurementCharacteristic = null;
            }
            
            if (_cadenceMeasurementCharacteristic != null)
            {
                _cadenceMeasurementCharacteristic.ValueUpdated -= OnCadenceMeasurementUpdated;
                await _cadenceMeasurementCharacteristic.StopUpdatesAsync();
                _cadenceMeasurementCharacteristic = null;
            }
            
            // Disconnect from device
            if (_connectedDevice != null)
            {
                await _adapter.DisconnectDeviceAsync(_connectedDevice);
                _connectedDevice = null;
            }
            
            _cyclingPowerService = null;
            IsConnected = false;
            Power = 0;
            Cadence = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error disconnecting: {ex.Message}");
            throw;
        }
    }

    private void OnPowerMeasurementUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        try
        {
            // Parse power data (first 2 bytes after flags)
            // This is a simplified parser - adjust based on your device's spec
            if (e.Characteristic.Value?.Length >= 4)
            {
                Power = BitConverter.ToInt16(e.Characteristic.Value, 2);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing power data: {ex.Message}");
        }
    }

    private void OnCadenceMeasurementUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        try
        {
            // Parse cadence data (second byte after flags)
            // This is a simplified parser - adjust based on your device's spec
            if (e.Characteristic.Value?.Length >= 2)
            {
                Cadence = e.Characteristic.Value[1];
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing cadence data: {ex.Message}");
        }
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    #endregion
}
