using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Media.SecureAgent;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Remote Control & Game Mode

        private bool CanHostSyncClipboard() => _isHosting && _allowRemoteControl && _allowClipboardSync;
        private bool CanViewerSyncClipboard() => _isViewing && _allowClipboardSync && _viewerPermissions.AllowClipboardSync;

        private EffectiveSessionPermissions GetHostGlobalPermissions() => new()
        {
            AllowRemoteControl = _allowRemoteControl,
            AllowSourceSwitch = _allowRemoteControl,
            AllowFileTransfer = true,
            AllowClipboardSync = _allowRemoteControl && _allowClipboardSync
        };

        private void UpdateHostControlServerState()
        {
            if (!_isHosting) return;
            if (_allowRemoteControl)
            {
                if (!_controlServer.IsRunning) _ = _controlServer.StartAsync(5003, CancellationToken.None);
            }
            else
            {
                _controlServer.Stop();
            }
        }

        private void InitializeControlCheckboxes()
        {
            var allowCb = this.FindControl<CheckBox>("AllowRemoteControlCheckBox");
            var controlAccessPanel = this.FindControl<StackPanel>("ControlAccessModePanel");

            if (allowCb != null)
            {
                allowCb.IsChecked = _appSettings.AllowRemoteControl;
                _allowRemoteControl = _appSettings.AllowRemoteControl;
                if (controlAccessPanel != null) controlAccessPanel.IsVisible = _allowRemoteControl;

                allowCb.IsCheckedChanged += (s, e) =>
                {
                    _allowRemoteControl = allowCb.IsChecked == true;
                    if (controlAccessPanel != null) controlAccessPanel.IsVisible = _allowRemoteControl;
                    UpdateHostControlServerState();
                    _ = _streamServer.BroadcastSessionPermissionsAsync();
                    Console.WriteLine($"[ControlHost] Permiso de control remoto: {_allowRemoteControl}");
                    SaveCurrentSettings();
                };
            }

            var clipboardSyncCb = this.FindControl<CheckBox>("ClipboardSyncCheckBox");
            if (clipboardSyncCb != null)
            {
                clipboardSyncCb.IsChecked = _appSettings.ClipboardSyncEnabled;
                _allowClipboardSync = _appSettings.ClipboardSyncEnabled;
                clipboardSyncCb.IsCheckedChanged += (s, e) =>
                {
                    bool enabled = clipboardSyncCb.IsChecked == true;
                    _allowClipboardSync = enabled;
                    _appSettings.ClipboardSyncEnabled = enabled;
                    _ = _streamServer.BroadcastSessionPermissionsAsync();
                    SaveCurrentSettings();
                };
            }

            InitializeSecureDesktopToggle();
        }

        private void InitializeSecureDesktopToggle()
        {
            var panel = this.FindControl<Grid>("SecureDesktopControlPanel");
            var toggle = this.FindControl<CheckBox>("AllowSecureDesktopServiceCheckBox");
            if (panel != null)
            {
                panel.IsVisible = OperatingSystem.IsWindows();
            }

            if (!OperatingSystem.IsWindows() || toggle == null)
                return;

            var status = _secureHostServiceManager.GetStatus();
            bool isReady = status == SecureHostServiceStatus.Running && _secureAgentClient is { IsConnected: true };
            SetSecureDesktopSwitchState(toggle, isReady);
            UpdateSecureDesktopServiceStatusLabel(status, isReady);

            toggle.IsCheckedChanged += async (s, e) =>
            {
                if (_isUpdatingSecureDesktopSwitch)
                    return;

                bool desired = toggle.IsChecked == true;
                await HandleSecureDesktopToggleChangedAsync(toggle, desired);
            };
        }

        private void SetSecureDesktopSwitchState(CheckBox toggle, bool isChecked)
        {
            _isUpdatingSecureDesktopSwitch = true;
            try
            {
                toggle.IsChecked = isChecked;
            }
            finally
            {
                _isUpdatingSecureDesktopSwitch = false;
            }
        }

        private void UpdateSecureDesktopServiceStatusLabel(SecureHostServiceStatus status, bool active)
        {
            var label = this.FindControl<TextBlock>("SecureDesktopServiceStatusText");
            if (label == null)
                return;

            if (active)
            {
                label.Text = "Servicio SYSTEM activo (UAC / Winlogon)";
                label.Foreground = new SolidColorBrush(Color.Parse("#34D399"));
                return;
            }

            label.Text = status == SecureHostServiceStatus.NotInstalled
                ? "Modo portátil · pedirá UAC al activar"
                : "Servicio SYSTEM en reposo";
            label.Foreground = new SolidColorBrush(Color.Parse("#7C8B9E"));
        }

        private async Task HandleSecureDesktopToggleChangedAsync(CheckBox toggle, bool enable)
        {
            toggle.IsEnabled = false;
            var label = this.FindControl<TextBlock>("SecureDesktopServiceStatusText");
            if (label != null)
            {
                label.Text = enable ? "Iniciando servicio SYSTEM..." : "Deteniendo servicio SYSTEM...";
                label.Foreground = new SolidColorBrush(Color.Parse("#F59E0B"));
            }

            try
            {
                if (enable)
                {
                    await EnableSecureDesktopServiceFromUiAsync(toggle, label);
                }
                else
                {
                    await DisableSecureDesktopServiceFromUiAsync();
                }
            }
            finally
            {
                toggle.IsEnabled = true;
            }
        }

        private async Task EnableSecureDesktopServiceFromUiAsync(CheckBox toggle, TextBlock? label)
        {
            string exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "LanScreenShare.SecureHost.exe");
            bool started = await Task.Run(() => _secureHostServiceManager.EnsureServiceRunningAsync(exePath));
            var client = started ? await Task.Run(TryConnectSecureAgentClient) : null;
            if (client == null)
            {
                SetSecureDesktopSwitchState(toggle, false);
                if (label != null)
                {
                    label.Text = "No se pudo activar el servicio SYSTEM";
                    label.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                }
                return;
            }

            _secureAgentClient?.Dispose();
            _secureAgentClient = client;
            _agentInputSimulatorProxy?.SetClient(client);
            _startedSecureHostServiceInSession = true;
            UpdateSecureDesktopServiceStatusLabel(SecureHostServiceStatus.Running, active: true);

            if (_isHosting)
            {
                SwitchCaptureSource(_currentSource, GetSelectedEngine(), BuildCurrentCaptureProfile());
            }
        }

        private async Task DisableSecureDesktopServiceFromUiAsync()
        {
            _agentInputSimulatorProxy?.SetClient(null);
            _secureAgentClient?.Dispose();
            _secureAgentClient = null;
            _startedSecureHostServiceInSession = false;

            if (_isHosting)
            {
                SwitchCaptureSource(_currentSource, GetSelectedEngine(), BuildCurrentCaptureProfile());
            }

            await Task.Run(() => _secureHostServiceManager.StopServiceAsync(removeIfTemporary: true));
            UpdateSecureDesktopServiceStatusLabel(_secureHostServiceManager.GetStatus(), active: false);
        }

        private bool CanHostHandleRemoteControl => _isHosting && _allowRemoteControl;

        private void WireControlServerEvents()
        {
            _controlServer.ClipboardTextReceived += (s, text) =>
            {
                if (CanHostSyncClipboard()) ApplyRemoteClipboardText(text);
            };
            _controlServer.ClientConnected += (s, id) => OnHostRemoteControlRequested();
            _controlServer.SourceCatalogRequested += (s, e) => OnHostRemoteControlRequested();
            _controlServer.SwitchMonitorRequested += (s, idx) =>
            {
                if (CanHostHandleRemoteControl) HandleRemoteMonitorSwitchOnHost(idx);
            };
            _controlServer.SelectSourceRequested += (s, srcId) =>
            {
                if (CanHostHandleRemoteControl) HandleRemoteSelectSourceOnHost(srcId);
            };
        }

        private void OnHostRemoteControlRequested()
        {
            if (CanHostHandleRemoteControl)
            {
                BroadcastHostSourceCatalog();
            }
        }

        private void WireControlClientEvents()
        {
            _controlClient.ClipboardTextReceived += (s, text) =>
            {
                if (CanViewerSyncClipboard()) ApplyRemoteClipboardText(text);
            };
            _controlClient.MonitorInfoReceived += (s, monitorLabel) =>
            {
                if (_isViewing) ApplyRemoteMonitorInfo(monitorLabel);
            };
            _controlClient.SecureDesktopStateReceived += (s, state) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    bool isBlocked = string.Equals(state, "SECURE_DESKTOP_ACTIVE", StringComparison.OrdinalIgnoreCase);
                    UpdateSecureDesktopBanner(isBlocked);
                });
            };
            _controlClient.Disconnected += ControlClient_Disconnected;
        }

        private void ControlClient_Disconnected(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isViewing)
                {
                    RevokeViewerRemoteControlUi();
                }
            });
        }

        private void UpdateSecureDesktopBanner(bool isBlocked)
        {
            var banner = this.FindControl<Border>("SecureDesktopBanner");
            if (banner != null)
            {
                banner.IsVisible = isBlocked;
            }
            _popoutWindow?.SetSecureDesktopState(isBlocked);
        }

        private void InitializeControlSubsystem()
        {
            _controlServer = new TcpControlServer(_sessionAuthority, _inputSimulatorService, _sslServerOptions);
            _controlServer.GlobalPermissionsProvider = GetHostGlobalPermissions;
            _controlClient = new TcpControlClient();
            _discoveryService.IsRemoteControlAllowedProvider = () => _allowRemoteControl;

            InitializeControlCheckboxes();
            WireControlServerEvents();
            WireControlClientEvents();
        }

        private bool TryGetNormalizedCoordinates(PointerEventArgs e, out float normX, out float normY) =>
            RemoteInputHelper.TryGetNormalizedCoordinates(this.FindControl<Image>(ScreenImageControlName), e, out normX, out normY);

        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (TryHandleShortcutKeys(e))
            {
                e.Handled = true;
            }
        }

        private bool TryHandleShortcutKeys(KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (hasCtrlAlt && TryHandleControlAltShortcut(e.Key))
                return true;

            if (e.Key == Key.F11) { ToggleFullscreen(); return true; }
            if (e.Key == Key.Escape && _isFullscreen) { SetFullscreen(false); return true; }

            return false;
        }

        private bool TryHandleControlAltShortcut(Key key) => key switch
        {
            Key.L => TryExecuteShortcut(_viewerPermissions.AllowRemoteControl, ToggleGameModeAsync),
            Key.M => TryExecuteShortcut(_viewerPermissions.AllowSourceSwitch, RequestRemoteMonitorSwitchAsync),
            _ => false
        };

        private bool TryExecuteShortcut(bool allowed, Func<Task> action)
        {
            if (_isViewing && allowed)
            {
                _ = action();
            }
            return true;
        }

        private bool CanSendRemoteControlInput()
        {
            if (!_viewerPermissions.AllowRemoteControl) return false;
            if (!_remoteControlEnabled) return false;
            return _controlClient.IsConnected;
        }

        private static bool IsShortcutKey(KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            return hasCtrlAlt && (e.Key == Key.L || e.Key == Key.M);
        }

        private bool HasAnyChannel5003Feature() =>
            _viewerPermissions.AllowRemoteControl || _viewerPermissions.AllowSourceSwitch || _viewerPermissions.AllowClipboardSync;

        private void ApplyControlConnectedUi()
        {
            if (_viewerPermissions.AllowRemoteControl)
            {
                _remoteControlEnabled = true;
                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsChecked = true;
                    remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
                }
                _popoutWindow?.SetRemoteControlState(true);
                UpdateStatus($"Conectado a {_currentConnectedDeviceName} • Control remoto activo", "#22C55E");
            }
            if (_viewerPermissions.AllowSourceSwitch)
            {
                _ = _controlClient.RequestSourceCatalogAsync();
            }
        }

        private async Task<bool> EnsureControlConnectedAsync()
        {
            if (_controlClient.IsConnected)
                return true;

            if (string.IsNullOrEmpty(_currentConnectedDeviceIp) || !HasAnyChannel5003Feature())
                return false;

            UpdateStatus("Solicitando acceso de control remoto al anfitrión...", "#F59E0B");

            try
            {
                var handshake = _streamClient.ActiveSessionHandshake;
                await _controlClient.ConnectAsync(new ControlClientConnectionOptions(_currentConnectedDeviceIp, 5003, _sslClientOptions, handshake?.SessionId, handshake?.ControlToken), CancellationToken.None);
                Console.WriteLine($"[ControlViewer] Conectado a canal de control remoto en {_currentConnectedDeviceIp}:5003");
                ApplyControlConnectedUi();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlClient] Control remoto denegado o no disponible: {ex.Message}");
                string detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                UpdateStatus($"Control remoto no disponible: {detail}", "#EF4444");
                return false;
            }
        }

        private bool IsPointerOverFullscreenHeader(PointerEventArgs e)
        {
            if (!_isFullscreen) return false;
            var header = this.FindControl<Border>("StreamHeaderBar");
            if (header == null || !header.IsVisible) return false;
            var pos = e.GetPosition(this);
            double headerHeight = header.Bounds.Height > 0 ? header.Bounds.Height : 50;
            return pos.Y <= headerHeight;
        }

        private bool CanProcessPointerInput(PointerEventArgs e) =>
            CanSendRemoteControlInput() && !IsPointerOverFullscreenHeader(e);

        private void ScreenImage_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!CanProcessPointerInput(e))
                return;

            if (_isGameMode)
            {
                HandleGameModePointerMoved();
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = InputEventType.MouseMove,
                    NormalizedX = x,
                    NormalizedY = y
                });
            }
        }

        private void DispatchPointerButtonEvent(PointerEventArgs e, InputEventType eventType, RemoteMouseButton button)
        {
            if (_isGameMode)
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = eventType,
                    Button = button,
                    NormalizedX = -1f,
                    NormalizedY = -1f
                });
                e.Handled = true;
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = eventType,
                    Button = button,
                    NormalizedX = x,
                    NormalizedY = y
                });
                e.Handled = true;
            }
        }

        private void HandleScreenImagePointerButton(PointerEventArgs e, InputEventType eventType, bool focusImage)
        {
            if (!CanProcessPointerInput(e))
                return;

            var screenImage = this.FindControl<Image>(ScreenImageControlName);
            if (focusImage)
            {
                screenImage?.Focus();
            }

            var props = e.GetCurrentPoint(screenImage).Properties;
            RemoteMouseButton button = eventType == InputEventType.MouseDown
                ? RemoteInputHelper.GetPressedMouseButton(props)
                : RemoteInputHelper.GetReleasedMouseButton(props);

            if (button != RemoteMouseButton.None)
            {
                DispatchPointerButtonEvent(e, eventType, button);
            }
        }

        private void ScreenImage_PointerPressed(object? sender, PointerPressedEventArgs e) =>
            HandleScreenImagePointerButton(e, InputEventType.MouseDown, focusImage: true);

        private void ScreenImage_PointerReleased(object? sender, PointerReleasedEventArgs e) =>
            HandleScreenImagePointerButton(e, InputEventType.MouseUp, focusImage: false);

        private void SendWheelEvent(short delta, float x, float y)
        {
            _ = _controlClient.SendInputAsync(new RemoteInputMessage
            {
                Type = InputEventType.MouseWheel,
                WheelDelta = delta,
                NormalizedX = x,
                NormalizedY = y
            });
        }

        private void ScreenImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!CanProcessPointerInput(e))
                return;

            short delta = (short)(e.Delta.Y * 120);

            if (_isGameMode)
            {
                SendWheelEvent(delta, -1f, -1f);
                e.Handled = true;
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                SendWheelEvent(delta, x, y);
                e.Handled = true;
            }
        }

        private void SendRemoteKeyEvent(Key key, InputEventType eventType)
        {
            if (!CanSendRemoteControlInput())
                return;

            int vk = RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(key);
            if (vk > 0)
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = eventType,
                    KeyCode = vk
                });
            }
        }

        private void HandleScreenImageKeyEvent(KeyEventArgs e, InputEventType eventType)
        {
            bool handledShortcut = eventType == InputEventType.KeyDown
                ? TryHandleShortcutKeys(e)
                : IsShortcutKey(e);

            if (!handledShortcut)
            {
                SendRemoteKeyEvent(e.Key, eventType);
            }
            e.Handled = true;
        }

        private void ScreenImage_KeyDown(object? sender, KeyEventArgs e) =>
            HandleScreenImageKeyEvent(e, InputEventType.KeyDown);

        private void ScreenImage_KeyUp(object? sender, KeyEventArgs e) =>
            HandleScreenImageKeyEvent(e, InputEventType.KeyUp);

        #endregion
    }
}
