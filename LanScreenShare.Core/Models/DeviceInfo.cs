using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanScreenShare.Core.Models
{
    [Flags]
    public enum DeviceCapabilities
    {
        None = 0,
        ScreenHost = 1 << 0,
        WirelessDisplay = 1 << 1
    }

    public class DeviceInfo : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _ipAddress = string.Empty;
        private DateTime _lastSeen;
        private bool _isRemoteControlAllowed = true;
        private bool _isViewing;
        private bool _isExtending;
        private DeviceCapabilities _capabilities = DeviceCapabilities.None;
        private int _displaySignalingPort;
        private bool _isWirelessDisplayBusy;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string IPAddress
        {
            get => _ipAddress;
            set
            {
                if (_ipAddress != value)
                {
                    _ipAddress = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime LastSeen
        {
            get => _lastSeen;
            set
            {
                if (_lastSeen != value)
                {
                    _lastSeen = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsRemoteControlAllowed
        {
            get => _isRemoteControlAllowed;
            set
            {
                if (_isRemoteControlAllowed != value)
                {
                    _isRemoteControlAllowed = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsViewing
        {
            get => _isViewing;
            set
            {
                if (_isViewing != value)
                {
                    _isViewing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActionButtonText));
                }
            }
        }

        public DeviceCapabilities Capabilities
        {
            get => _capabilities;
            set
            {
                if (SetField(ref _capabilities, value))
                {
                    OnPropertyChanged(nameof(SupportsScreenHost));
                    OnPropertyChanged(nameof(IsWirelessDisplay));
                    OnPropertyChanged(nameof(ExtendButtonText));
                    OnPropertyChanged(nameof(CanExtendToDevice));
                }
            }
        }

        public int DisplaySignalingPort
        {
            get => _displaySignalingPort;
            set => SetField(ref _displaySignalingPort, value);
        }

        public bool IsWirelessDisplayBusy
        {
            get => _isWirelessDisplayBusy;
            set
            {
                if (SetField(ref _isWirelessDisplayBusy, value))
                {
                    OnPropertyChanged(nameof(ExtendButtonText));
                    OnPropertyChanged(nameof(CanExtendToDevice));
                }
            }
        }

        public bool IsExtending
        {
            get => _isExtending;
            set
            {
                if (SetField(ref _isExtending, value))
                {
                    OnPropertyChanged(nameof(ExtendButtonText));
                    OnPropertyChanged(nameof(CanExtendToDevice));
                }
            }
        }

        public bool SupportsScreenHost => (_capabilities & DeviceCapabilities.ScreenHost) != 0;

        public bool IsWirelessDisplay => (_capabilities & DeviceCapabilities.WirelessDisplay) != 0;

        public string ActionButtonText => _isViewing ? "Viendo" : "Ver pantalla";

        public string ExtendButtonText => _isExtending
            ? "Extendiendo"
            : (_isWirelessDisplayBusy ? "Ocupado" : "Extender pantalla");

        public bool CanExtendToDevice =>
            OperatingSystem.IsWindows() && IsWirelessDisplay && (!_isWirelessDisplayBusy || _isExtending);

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
