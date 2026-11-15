using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Remote Control & Game Mode

        private bool CanHostSyncClipboard() => _isHosting && _allowRemoteControl && _allowClipboardSync;
        private bool CanViewerSyncClipboard() => _isViewing && _remoteControlEnabled && _allowClipboardSync;

        private void UpdateHostControlServerState()
        {
            if (!_isHosting) return;
            if (_allowRemoteControl)
            {
                if (!_controlServer.IsRunning) _ = _controlServer.StartAsync(5003);
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
                    SaveCurrentSettings();
                };
            }
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
            if (hasCtrlAlt)
            {
                if (e.Key == Key.L) { ToggleGameMode(); return true; }
                if (e.Key == Key.M) { _ = RequestRemoteMonitorSwitchAsync(); return true; }
            }

            if (e.Key == Key.F11) { ToggleFullscreen(); return true; }
            if (e.Key == Key.Escape && _isFullscreen) { SetFullscreen(false); return true; }

            return false;
        }

        private static bool IsShortcutKey(KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            return hasCtrlAlt && (e.Key == Key.L || e.Key == Key.M);
        }

        private async Task<bool> EnsureControlConnectedAsync()
        {
            if (_controlClient.IsConnected)
                return true;

            if (string.IsNullOrEmpty(_currentConnectedDeviceIp))
                return false;

            UpdateStatus("Solicitando acceso de control remoto al anfitrión...", "#F59E0B");

            try
            {
                var handshake = _streamClient.ActiveSessionHandshake;
                await _controlClient.ConnectAsync(new ControlClientConnectionOptions(_currentConnectedDeviceIp, 5003, _sslClientOptions, handshake?.SessionId, handshake?.ControlToken));
                Console.WriteLine($"[ControlViewer] Conectado a canal de control remoto en {_currentConnectedDeviceIp}:5003");
                _remoteControlEnabled = true;

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null && remoteControlBtn.IsChecked != true)
                {
                    remoteControlBtn.IsChecked = true;
                    remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
                }
                _popoutWindow?.SetRemoteControlState(true);
                _ = _controlClient.RequestSourceCatalogAsync();
                UpdateStatus($"Conectado a {_currentConnectedDeviceName} • Control remoto activo", "#22C55E");
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
            _remoteControlEnabled && _controlClient.IsConnected && !IsPointerOverFullscreenHeader(e);

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
            if (_remoteControlEnabled && _controlClient.IsConnected)
            {
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
