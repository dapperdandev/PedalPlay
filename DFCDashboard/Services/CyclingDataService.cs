using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Storage; // For Preferences
using System.Text.Json; // For JSON serialization

namespace DFCDashboard.Services;

public class CyclingDataService : INotifyPropertyChanged
{
    private readonly IAdapter _adapter;
    private const string DeviceInfoKey = "saved_ble_device";
    
    private class DeviceInfo
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public bool IsConnectable { get; set; }
    }
    
    private IDevice? _connectedDevice;
    private IService? _cyclingPowerService;
    private ICharacteristic? _powerMeasurementCharacteristic;
    
    // Bluetooth SIG-defined UUIDs for services and characteristics
    public static class BluetoothUuids
    {
        // Services
        public static readonly Guid CyclingPowerService = new("00001818-0000-1000-8000-00805f9b34fb");
        
        // Characteristics
        public static readonly Guid CyclingPowerMeasurement = new("00002A63-0000-1000-8000-00805f9b34fb");
        
        // Descriptors
        public static readonly Guid ClientCharacteristicConfiguration = new("00002902-0000-1000-8000-00805f9b34fb");
    }

    private int _power;
    private int _cadence;
    private bool _isConnected;
    private bool _isReconnecting;

    private uint _lastCrankRevolutions = 0;
    private ushort _lastCrankEventTime = 0;
    private DateTime _lastCalculationTime = DateTime.MinValue;
    private static readonly TimeSpan CADENCE_TIMEOUT = TimeSpan.FromSeconds(3);

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

    public bool IsReconnecting
    {
        get => _isReconnecting;
        private set => SetProperty(ref _isReconnecting, value);
    }

    public CyclingDataService(IAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _adapter.DeviceConnected += OnDeviceConnected;
        _adapter.DeviceDisconnected += OnDeviceDisconnected;
    }

    private void OnDeviceConnected(object? sender, DeviceEventArgs e)
    {
        // Handle device connected event if needed
    }

    private void OnDeviceDisconnected(object? sender, DeviceEventArgs e)
    {
        // Handle device disconnected event
        if (_connectedDevice?.Id == e.Device.Id)
        {
            IsConnected = false;
            // Don't clear the saved device info here to allow auto-reconnect
        }
    }

    public async Task<bool> TryReconnectLastDeviceAsync()
    {
        if (!Preferences.ContainsKey(DeviceInfoKey))
        {
            return false;
        }

        try
        {
            IsReconnecting = true;

            var json = Preferences.Get(DeviceInfoKey, string.Empty);
            var deviceInfo = JsonSerializer.Deserialize<DeviceInfo>(json);
            
            if (deviceInfo == null)
            {
                return false;
            }

            // First try paired devices
            var devices = _adapter.GetSystemConnectedOrPairedDevices(
                new[] { BluetoothUuids.CyclingPowerService });
            
            var device = devices.FirstOrDefault(d => d.Id.ToString() == deviceInfo.Id);

            // If not found in paired devices, try scanning
            if (device == null)
            {
                var scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                
                // Set up a TaskCompletionSource for the device discovery
                var deviceFound = new TaskCompletionSource<IDevice>();
                
                void OnDeviceDiscovered(object? s, DeviceEventArgs args)
                {
                    if (args.Device?.Id.ToString() == deviceInfo.Id)
                    {
                        deviceFound.TrySetResult(args.Device);
                    }
                }

                try
                {
                    _adapter.DeviceDiscovered += OnDeviceDiscovered;
                    
                    // Start scanning
                    await _adapter.StartScanningForDevicesAsync(cancellationToken: scanCts.Token);
                    
                    // Wait for device to be found or timeout
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    device = await deviceFound.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Timeout or scan cancelled, device not found
                    return false;
                }
                finally
                {
                    _adapter.DeviceDiscovered -= OnDeviceDiscovered;
                    try { await _adapter.StopScanningForDevicesAsync(); } catch { }
                }
            }
            
            if (device != null)
            {
                await ConnectToDevice(device);
                return true;
            }
        }
        catch
        {
            // Ignore any errors during reconnection
            // but make sure we're in a clean state
            try
            {
                await Disconnect();
            }
            catch { }
        }
        finally
        {
            IsReconnecting = false;
        }
        
        return false;
    }

    public async Task ConnectToDevice(IDevice device)
    {
        if (device == null) throw new ArgumentNullException(nameof(device));

        try
        {
            // Save device info for reconnection
            var deviceInfo = new DeviceInfo
            {
                Id = device.Id.ToString(),
                Name = device.Name,
                IsConnectable = device.IsConnectable
            };
            
            var json = JsonSerializer.Serialize(deviceInfo);
            Preferences.Set(DeviceInfoKey, json);
            
            _connectedDevice = device;
            
            // Connect to the device
            await _adapter.ConnectToDeviceAsync(device);
            
            // Discover services
            var services = await device.GetServicesAsync();
            
            // Try to find the Cycling Power Service
            _cyclingPowerService = services.FirstOrDefault(s => s.Id == BluetoothUuids.CyclingPowerService);
            
            if (_cyclingPowerService == null)
            {
                throw new Exception("Cycling Power service not found");
            }
            
            // Get characteristics
            var characteristics = await _cyclingPowerService.GetCharacteristicsAsync();
            
            // Try to get power measurement characteristic
            _powerMeasurementCharacteristic = characteristics.FirstOrDefault(c => c.Id == BluetoothUuids.CyclingPowerMeasurement);
            
            // Enable notifications for power measurement if available
            if (_powerMeasurementCharacteristic != null)
            {
                _powerMeasurementCharacteristic.ValueUpdated += OnPowerMeasurementUpdated;
                await _powerMeasurementCharacteristic.StartUpdatesAsync();
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
            if (Preferences.ContainsKey(DeviceInfoKey))
            {
                Preferences.Remove(DeviceInfoKey);
            }
            
            if (_powerMeasurementCharacteristic != null)
            {
                _powerMeasurementCharacteristic.ValueUpdated -= OnPowerMeasurementUpdated;
                await _powerMeasurementCharacteristic.StopUpdatesAsync();
                _powerMeasurementCharacteristic = null;
            }
            
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
        catch
        {
            throw;
        }
    }

    private void OnPowerMeasurementUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        try
        {
            var data = e.Characteristic.Value;
            if (data == null || data.Length < 8) return;

            // First byte contains flags
            byte flags = data[0];
            bool hasCrankRevolutions = (flags & 0x20) != 0;  // Bit 5 indicates crank data

            // Power is always in bytes 2-3 (Little Endian)
            Power = BitConverter.ToInt16(data, 2);

            if (hasCrankRevolutions && data.Length >= 8)
            {
                // Crank Revolutions (uint16) at index 4-5
                uint crankRevolutions = BitConverter.ToUInt16(data, 4);
                // Last Crank Event Time (uint16) at index 6-7
                ushort crankEventTime = BitConverter.ToUInt16(data, 6);

                // Calculate cadence only if we have previous values
                if (_lastCrankEventTime != 0)
                {
                    // Handle rollover of event time (16-bit value)
                    ushort timeDiff;
                    if (crankEventTime < _lastCrankEventTime)
                    {
                        timeDiff = (ushort)((ushort.MaxValue - _lastCrankEventTime) + crankEventTime);
                    }
                    else
                    {
                        timeDiff = (ushort)(crankEventTime - _lastCrankEventTime);
                    }

                    // Handle rollover of crank revolutions
                    uint revDiff;
                    if (crankRevolutions < _lastCrankRevolutions)
                    {
                        revDiff = (ushort.MaxValue - _lastCrankRevolutions) + crankRevolutions;
                    }
                    else
                    {
                        revDiff = crankRevolutions - _lastCrankRevolutions;
                    }

                    // Only update if we actually have movement
                    if (timeDiff > 0 && revDiff > 0)
                    {
                        // Calculate cadence: (revolutions / time) * (1024 / 60) to get RPM
                        // 1024 because the event time is in 1/1024ths of a second
                        double cadence = (revDiff * 1024.0 * 60.0) / timeDiff;
                        Cadence = (int)Math.Round(cadence);
                        _lastCalculationTime = DateTime.UtcNow;
                    }
                    else if (DateTime.UtcNow - _lastCalculationTime > CADENCE_TIMEOUT)
                    {
                        // Reset cadence to 0 if no updates for 3 seconds
                        Cadence = 0;
                    }
                }

                _lastCrankRevolutions = crankRevolutions;
                _lastCrankEventTime = crankEventTime;
            }
        }
        catch
        {
            // Ignore any parsing errors
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
