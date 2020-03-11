﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using LiveCharts.Geared;

namespace HeartMonitorWPF
{
    public class SpeedTestVm : INotifyPropertyChanged
    {
        static List<DeviceInformation> _deviceList = new List<DeviceInformation>();
        static BluetoothLEDevice _selectedDevice;

        static List<BluetoothLEAttributeDisplay> _services = new List<BluetoothLEAttributeDisplay>();
        static BluetoothLEAttributeDisplay _selectedService;

        static List<BluetoothLEAttributeDisplay> _characteristics = new List<BluetoothLEAttributeDisplay>();

        // Only one registered characteristic at a time.
        static List<GattCharacteristic> _subscribers = new List<GattCharacteristic>();

        static ManualResetEvent _notifyCompleteEvent;
        static bool _primed;
        private double LastReading = 80;

        static readonly string _aqsAllBLEDevices = "(System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";
        static readonly string[] _requestedBLEProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.Bluetooth.Le.IsConnectable" };

        // Current data format
        static readonly DataFormat _dataFormat = DataFormat.Dec;
        static readonly TimeSpan _timeout = TimeSpan.FromSeconds(3);

        private double _trend = 80;
        private double _count;
        private double _currentLecture;
        private bool _isHot;

        public SpeedTestVm()
        {
            Values = new GearedValues<double>().WithQuality(Quality.Highest);
            ReadCommand = new RelayCommand(Read);
            StopCommand = new RelayCommand(Stop);
            Values.AddRange(Enumerable.Repeat(_trend, Buffer));
            Task.Factory.StartNew(Commands);
            ReadCommand.Execute(null);
        }

        public bool IsReading { get; set; }
        public RelayCommand ReadCommand { get; }
        public RelayCommand StopCommand { get; }
        public GearedValues<double> Values { get; set; }

        public int Buffer { get; } = 8192;

        private string _flyoutMessage;
        public string FlyoutMessage
        {
            get => _flyoutMessage;
            set
            {
                FlyoutOpen = true;
                _flyoutMessage = value;
                OnPropertyChanged("FlyoutOpen");
                OnPropertyChanged("FlyoutMessage");
            }
        }
        public bool FlyoutOpen { get; set; }

        public double Count
        {
            get => _count;
            set
            {
                _count = value;
                OnPropertyChanged("Count");
            }
        }

        public double CurrentLecture
        {
            get => _currentLecture;
            set
            {
                _currentLecture = value;
                OnPropertyChanged("CurrentLecture");
            }
        }

        public bool IsHot
        {
            get => _isHot;
            set
            {
                var changed = value != _isHot;
                _isHot = value;
                if (changed) OnPropertyChanged("IsHot");
            }
        }

        private void Stop()
        {
            CloseDevice();
            IsReading = false;
            FlyoutMessage = "STOPPED";
        }

        private void Read()
        {
            FlyoutMessage = "READING";
            if (IsReading) return;

            //lets keep in memory only the last 20000 records,
            //to keep everything running faster
            IsReading = true;

            Task.Factory.StartNew(readFromTread);
        }
        private void readFromTread()
        {
            while (IsReading)
            {
                Thread.Sleep(10);

                _trend = LastReading;
                
                //Remove first value
                var first = Values.DefaultIfEmpty(0).FirstOrDefault();
                if (Values.Count > Buffer - 1) 
                    Values.Remove(first);

                if (Values.Count < Buffer) 
                    Values.Add(_trend);
                
                IsHot = _trend > 100;
                Count = Values.Count;
                CurrentLecture = _trend;
            }
        }

        /// <summary>
        /// Event handler for ValueChanged callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            if (_primed)
            {
                var newValue = Utilities.FormatValue(args.CharacteristicValue, _dataFormat);
                LastReading = Convert.ToDouble(newValue.Replace(" ", ""));
                //lastValues.Add(Utilities.FormatData(args.CharacteristicValue));
                if (Console.IsInputRedirected) Console.Write($"{newValue}");
                else Console.WriteLine($"Value changed for {sender.Uuid}: {newValue}");
                if (_notifyCompleteEvent != null)
                {
                    _notifyCompleteEvent.Set();
                    _notifyCompleteEvent = null;
                }
            }
            else _primed = true;
        }

        private async void Commands()
        {
            var watcher = DeviceInformation.CreateWatcher(_aqsAllBLEDevices, _requestedBLEProperties, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (sender, devInfo) =>
            {
                if (_deviceList.FirstOrDefault(d => d.Id.Equals(devInfo.Id) || d.Name.Equals(devInfo.Name)) == null) 
                    _deviceList.Add(devInfo);
            };
            watcher.Updated += (_, __) => { }; // We need handler for this event, even an empty!
            //Watch for a device being removed by the watcher
            watcher.Removed += (sender, devInfo) =>
            { _deviceList.Remove(FindKnownDevice(devInfo.Id)); };
            watcher.EnumerationCompleted += (sender, arg) => { sender.Stop(); };
            watcher.Stopped += (sender, arg) => { _deviceList.Clear(); sender.Start(); };
            watcher.Start();

            //Search for polar device
            FlyoutMessage = "Searching device...";

            DeviceInformation tempDevice = null;
            while (tempDevice == null)
            {
                tempDevice = _deviceList.FirstOrDefault(d => d.Name.Contains("Polar", StringComparison.OrdinalIgnoreCase));
                if (tempDevice == null)
                    Thread.Sleep(1000);
            }

            //Connecting to device
            FlyoutMessage = "Connecting...";
            await OpenDevice(tempDevice.Name);

            //Search for correct service
            FlyoutMessage = "Searching service...";

            BluetoothLEAttributeDisplay tempService = null;
            while (tempService == null)
            {
                tempService = _services.FirstOrDefault(d => d.Name.Contains("Heart", StringComparison.OrdinalIgnoreCase));
                if (tempService == null)
                    Thread.Sleep(1000);
            }

            Thread.Sleep(1000);
            //Subscribing to service
            FlyoutMessage = "Registerring service...";
            var attempts = 0;
            while (!await SetService(tempService.Name) && attempts < 5)
            {
                Thread.Sleep(1000);
                FlyoutMessage = "Retrying...";
                attempts++;
            }
            if (attempts >= 5)
            {
                FlyoutMessage = "Giving up";
                return;
            }
            await SubscribeToCharacteristic(tempService.Name);
        }
        
        /// <summary>
        /// Connecting to a device
        /// </summary>
        /// <param name="deviceName"></param>
        /// <returns></returns>
        private async Task OpenDevice(string deviceName)
        {
            if (!string.IsNullOrEmpty(deviceName))
            {
                var devs = _deviceList.OrderBy(d => d.Name).Where(d => !string.IsNullOrEmpty(d.Name)).ToList();
                string foundId = Utilities.GetIdByNameOrNumber(devs, deviceName);

                // If device is found, connect to device and enumerate all services
                if (!string.IsNullOrEmpty(foundId))
                {
                    _selectedService = null;
                    _services.Clear();

                    try
                    {
                        // only allow for one connection to be open at a time
                        if (_selectedDevice != null)
                            CloseDevice();

                        _selectedDevice = await BluetoothLEDevice.FromIdAsync(foundId).AsTask().TimeoutAfter(_timeout);
                        if (!Console.IsInputRedirected)
                            Console.WriteLine($"Connecting to {_selectedDevice.Name}.");

                        var result = await _selectedDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                        if (result.Status == GattCommunicationStatus.Success)
                        {
                            if (!Console.IsInputRedirected)
                                Console.WriteLine($"Found {result.Services.Count} services:");

                            for (int i = 0; i < result.Services.Count; i++)
                            {
                                var serviceToDisplay = new BluetoothLEAttributeDisplay(result.Services[i]);
                                _services.Add(serviceToDisplay);
                                if (!Console.IsInputRedirected)
                                    Console.WriteLine($"#{i:00}: {_services[i].Name}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Device {deviceName} is unreachable.");
                        }
                    }
                    catch
                    {
                        Console.WriteLine($"Device {deviceName} is unreachable.");
                    }
                }
            }
            else
            {
                Console.WriteLine("Device name can not be empty.");
            }
        }

        /// <summary>
        /// Disconnect current device and clear list of services and characteristics
        /// </summary>
        private void CloseDevice()
        {
            // Remove all subscriptions
            if (_subscribers.Count > 0) Unsubscribe("all");

            if (_selectedDevice == null) return;

            if (!Console.IsInputRedirected)
                Console.WriteLine($"Device {_selectedDevice.Name} is disconnected.");

            _services?.ForEach(s => { s.service?.Dispose(); });
            _services?.Clear();
            _characteristics?.Clear();
            _selectedDevice?.Dispose();
        }

        /// <summary>
        /// This function is used to unsubscribe from "ValueChanged" event
        /// </summary>
        /// <param name="param"></param>
        private async void Unsubscribe(string param)
        {
            if (_subscribers.Count == 0)
            {
                if (!Console.IsOutputRedirected)
                    Console.WriteLine("No subscription for value changes found.");
            }
            else if (string.IsNullOrEmpty(param))
            {
                if (!Console.IsOutputRedirected)
                    Console.WriteLine("Please specify characteristic name or # (for single subscription) or type \"unsubs all\" to remove all subscriptions");
            }
            // Unsubscribe from all value changed events
            else if (param.Replace("/", "").ToLower().Equals("all"))
            {
                foreach (var sub in _subscribers)
                {
                    await sub.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                    sub.ValueChanged -= Characteristic_ValueChanged;
                }
                _subscribers.Clear();
            }
            // unsubscribe from specific event
        }

        /// <summary>
        /// Set active service for current device
        /// </summary>
        /// <param name="serviceName"></param>
        private async Task<bool> SetService(string serviceName)
        {
            if (_selectedDevice == null) 
                return false;
            if (string.IsNullOrEmpty(serviceName)) 
                return false;

            string foundName = Utilities.GetIdByNameOrNumber(_services, serviceName);

            // If device is found, connect to device and enumerate all services
            if (string.IsNullOrEmpty(foundName)) return false;

            var attr = _services.FirstOrDefault(s => s.Name.Equals(foundName));

            try
            {
                // Ensure we have access to the device.
                if (attr != null) 
                {
                    var accessStatus = await attr.service.RequestAccessAsync();
                    if (accessStatus == DeviceAccessStatus.Allowed)
                    {
                        // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                        // and the new Async functions to get the characteristics of unpaired devices as well. 
                        var result = await attr.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                        if (result.Status == GattCommunicationStatus.Success)
                        {
                            var characteristics = result.Characteristics;
                            _selectedService = attr;
                            _characteristics.Clear();
                            if (!Console.IsInputRedirected) Console.WriteLine($"Selected service {attr.Name}.");

                            if (characteristics.Count == 0) 
                            { FlyoutMessage = "Service don't have any characteristic."; }

                            foreach (var characteristic in characteristics)
                            {
                                var charToDisplay = new BluetoothLEAttributeDisplay(characteristic);
                                _characteristics.Add(charToDisplay);
                            }
                            return true;
                            
                        }
                        Thread.Sleep(1000);
                        FlyoutMessage += $"\nError accessing service : {result.Status.ToString()}";
                        return false;
                    }
                    // Not granted access
                    FlyoutMessage = "Error accessing service.";
                }
            }
            catch (Exception ex)
            { FlyoutMessage = $"Restricted service. Can't read characteristics: {ex.Message}"; }
            return false;
        }

        /// <summary>
        /// This function used to add "ValueChanged" event subscription
        /// </summary>
        /// <param name="param"></param>
        private async Task SubscribeToCharacteristic(string param)
        {
            //Checking if a device is connected
            if (_selectedDevice == null)
            {
                DisplayError("No BLE device connected.");
                return;
            }

            //Checking if subscription is filled
            if (string.IsNullOrEmpty(param))
            {
                DisplayError("Nothing to subscribe, please specify characteristic name or #.");
                return;
            }

            List<BluetoothLEAttributeDisplay> chars = new List<BluetoothLEAttributeDisplay>();
            string charName = string.Empty;
            var parts = param.Split('/');

            switch (parts.Length)
            {
                // Do we have parameter is in "service/characteristic" format?
                case 2:
                    {
                        string serviceName = Utilities.GetIdByNameOrNumber(_services, parts[0]);
                        charName = parts[1];

                        //Checking if we got a name
                        if (string.IsNullOrEmpty(serviceName))
                            break;
                        var attri = _services.FirstOrDefault(s => s.Name.Equals(serviceName));
                        IReadOnlyList<GattCharacteristic> characteristics = new List<GattCharacteristic>();

                        try
                        {
                            if (attri == null)
                                break;

                            // Ensure we have access to the device.
                            var accessStatus = await attri.service.RequestAccessAsync();
                            if (accessStatus != DeviceAccessStatus.Allowed)
                                break;

                            //Check GattCommunication (if sevice is already connected, will return AccessDenied)
                            var result = await attri.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                            if (result.Status == GattCommunicationStatus.Success)
                                characteristics = result.Characteristics;
                        }
                        catch (Exception ex)
                        { DisplayError($"Restricted service. Can't subscribe to characteristics: {ex.Message}"); }

                        //Add characteristic
                        chars.AddRange(characteristics.Select(c => new BluetoothLEAttributeDisplay(c)));
                        break;
                    }
                case 1 when _selectedService == null:
                    {
                        DisplayError("No service is selected.");
                        return;
                    }
                case 1:
                    chars = new List<BluetoothLEAttributeDisplay>(_characteristics);
                    charName = parts[0];
                    break;
            }

            if (!(chars.Count > 0 && !string.IsNullOrEmpty(charName)))
            {
                DisplayError("Nothing to subscribe, please specify characteristic name or #.");
                return;
            }

            string useName = Utilities.GetIdByNameOrNumber(chars, charName);
            var attr = chars.FirstOrDefault(c => c.Name.Equals(useName));

            if (attr?.characteristic == null)
            {
                DisplayError($"Invalid characteristic {useName}");
                return;
            }

            if (_subscribers.Contains(attr.characteristic))
            {
                DisplayError($"Already subscribed to characteristic {useName}");
                return;
            }

            var status = await attr.characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                DisplayError($"Can't subscribe to characteristic {useName}");
                return;
            }

            _subscribers.Add(attr.characteristic);
            FlyoutMessage = "Listening...";
            attr.characteristic.ValueChanged += Characteristic_ValueChanged;
        }

        private bool DisplayError(string msg)
        {
            FlyoutMessage = msg;
            return false;
        }

        private DeviceInformation FindKnownDevice(string deviceId)
        { return _deviceList.FirstOrDefault(device => device.Id == deviceId); }


        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName = null)
        { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); }
    }
}
