using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        private const string StatusWarningAmberHex = "#F59E0B";

        #region Discovery & Client Viewing Logic

        private void UpdateDevicesSectionUI()
        {
            var countText = this.FindControl<TextBlock>("DevicesCountText");
            if (countText != null)
            {
                countText.Text = Devices.Count == 1 ? "1 encontrado" : $"{Devices.Count} encontrados";
            }

            var placeholder = this.FindControl<Control>("NoDevicesPlaceholder");
            if (placeholder != null)
            {
                placeholder.IsVisible = Devices.Count == 0;
            }

            var list = this.FindControl<ListBox>("DevicesList");
            if (list != null)
            {
                list.IsVisible = Devices.Count > 0;
            }

            UpdateDevicesViewingState();
        }

        private void UpdateDevicesViewingState()
        {
            foreach (var d in Devices)
            {
                d.IsViewing = _isViewing && !string.IsNullOrEmpty(_currentConnectedDeviceIp) && d.IPAddress == _currentConnectedDeviceIp;
                d.IsExtending = _isExtendingDisplay && !string.IsNullOrEmpty(_extendedTargetIp) && d.IPAddress == _extendedTargetIp;
            }
        }

        private void StopDeviceSearch()
        {
            _searchTimer?.Stop();
            _searchTimer = null;
            _discoveryService.StopListening();

            var searchBtn = this.FindControl<Button>("SearchButton");
            var searchBtnText = this.FindControl<TextBlock>("SearchButtonText");
            if (searchBtnText != null)
            {
                searchBtnText.Text = "Buscar dispositivos";
            }
            if (searchBtn != null)
            {
                searchBtn.IsEnabled = true;
            }

            var localInfo = this.FindControl<TextBlock>("LocalInfoText");
            if (localInfo != null)
            {
                localInfo.Text = Devices.Count > 0
                    ? $"Listo para transmitir o recibir · {Devices.Count} detectado(s)"
                    : "Listo para transmitir o recibir";
            }
        }

        private void SearchButton_Click(object? sender, RoutedEventArgs e)
        {
            _searchTimer?.Stop();
            _discoveryService.StartListening();

            var searchBtn = this.FindControl<Button>("SearchButton");
            var searchBtnText = this.FindControl<TextBlock>("SearchButtonText");
            if (searchBtnText != null) searchBtnText.Text = "Buscando en LAN...";
            if (searchBtn != null) searchBtn.IsEnabled = false;

            var localInfo = this.FindControl<TextBlock>("LocalInfoText");
            if (localInfo != null) localInfo.Text = "Buscando equipos en tu red local (UDP 5000)...";

            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _searchTimer.Tick += (s, args) => StopDeviceSearch();
            _searchTimer.Start();
        }

        private void UpsertDiscoveredDevice(DeviceInfo device)
        {
            var existing = Devices.FirstOrDefault(d => d.IPAddress == device.IPAddress);
            if (existing == null)
            {
                Devices.Add(device);
                return;
            }

            existing.Name = device.Name;
            existing.LastSeen = device.LastSeen;
            existing.Capabilities |= device.Capabilities;
            if (device.IsWirelessDisplay)
            {
                existing.DisplaySignalingPort = device.DisplaySignalingPort;
                existing.IsWirelessDisplayBusy = device.IsWirelessDisplayBusy;
            }
            if (device.SupportsScreenHost)
            {
                existing.IsRemoteControlAllowed = device.IsRemoteControlAllowed;
            }
        }

        private void SyncActiveDeviceRemoteControlState(DeviceInfo device)
        {
            if (!_streamClient.IsConnected || _currentConnectedDeviceIp != device.IPAddress)
                return;

            // El descubrimiento UDP solo actúa como límite global a la baja;
            // nunca debe sobreescribir restricciones individuales de la sesión.
            if (!device.IsRemoteControlAllowed)
            {
                var capped = _viewerPermissions with
                {
                    AllowRemoteControl = false,
                    AllowSourceSwitch = false,
                    AllowClipboardSync = false
                };
                if (capped != _viewerPermissions)
                {
                    ApplyViewerPermissions(capped, isLiveUpdate: true);
                }
            }
        }

        private void ApplyViewerPermissions(EffectiveSessionPermissions permissions, bool isLiveUpdate)
        {
            var previous = _viewerPermissions;
            _viewerPermissions = permissions;

            ConfigureControlButtonsForPermissions(permissions);
            SetViewerControlVisibility("SendFileButton", permissions.AllowFileTransfer);
            _popoutWindow?.ApplyPermissions(
                permissions.AllowRemoteControl,
                permissions.AllowSourceSwitch,
                permissions.AllowFileTransfer);

            if (!permissions.AllowRemoteControl)
            {
                RevokeViewerRemoteControlUi();
            }

            if (!permissions.AllowSourceSwitch)
            {
                CloseAndClearRemoteSourceMenu();
            }

            if (isLiveUpdate)
            {
                NotifyRevokedPermissionsIfNeeded(previous, permissions);
            }
        }

        private void ConfigureControlButtonsForPermissions(EffectiveSessionPermissions permissions)
        {
            ConfigurePermissionToggleButton("RemoteControlButton", permissions.AllowRemoteControl, "Habilitar interacción con mouse y teclado");
            ConfigurePermissionToggleButton("GameModeButton", permissions.AllowRemoteControl);

            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (switchMonitorBtn != null)
            {
                switchMonitorBtn.IsVisible = permissions.AllowSourceSwitch;
                switchMonitorBtn.IsEnabled = true;
            }
        }

        private void ConfigurePermissionToggleButton(string controlName, bool allowed, string? tooltip = null)
        {
            var btn = this.FindControl<ToggleButton>(controlName);
            if (btn == null) return;

            btn.IsVisible = allowed;
            btn.IsEnabled = true;
            if (!allowed) btn.IsChecked = false;
            if (tooltip != null) ToolTip.SetTip(btn, tooltip);
        }

        private void RevokeViewerRemoteControlUi()
        {
            _remoteControlEnabled = false;
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsChecked = false;
                remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#27272A"));
            }

            if (_isGameMode)
            {
                ExitGameModeWithoutRecaptureHint();
            }

            _popoutWindow?.SetRemoteControlState(false);

            bool anyChannel5003Feature = _viewerPermissions.AllowRemoteControl
                || _viewerPermissions.AllowSourceSwitch
                || _viewerPermissions.AllowClipboardSync;

            if (!anyChannel5003Feature && _controlClient.IsConnected)
            {
                _ = _controlClient.DisconnectAsync();
            }
        }

        private void CloseAndClearRemoteSourceMenu()
        {
            _remoteSourceMenu?.Close();
            _remoteMonitorCatalog = null;
        }

        private void NotifyRevokedPermissionsIfNeeded(
            EffectiveSessionPermissions previous,
            EffectiveSessionPermissions current)
        {
            if (WasAnyPermissionRevoked(previous, current))
            {
                UpdateStatus("El anfitrión actualizó o revocó permisos de esta sesión", StatusWarningAmberHex);
            }
        }

        private static bool WasAnyPermissionRevoked(
            EffectiveSessionPermissions previous,
            EffectiveSessionPermissions current)
        {
            ReadOnlySpan<bool> before = [previous.AllowRemoteControl, previous.AllowSourceSwitch, previous.AllowFileTransfer, previous.AllowClipboardSync];
            ReadOnlySpan<bool> after = [current.AllowRemoteControl, current.AllowSourceSwitch, current.AllowFileTransfer, current.AllowClipboardSync];
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] && !after[i])
                {
                    return true;
                }
            }
            return false;
        }

        private void StreamClient_PermissionsUpdated(object? sender, EffectiveSessionPermissions permissions)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isViewing || !_streamClient.IsConnected)
                    return;

                ApplyViewerPermissions(permissions, isLiveUpdate: true);
                if (permissions.AllowFileTransfer && !_fileTransferClient.IsConnected)
                {
                    ConnectFileTransferChannelIfAllowed(_currentConnectedDeviceIp, _streamClient.ActiveSessionHandshake);
                }
            });
        }

        private void DiscoveryService_OnDeviceDiscovered(object? sender, DeviceInfo device)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpsertDiscoveredDevice(device);
                UpdateDevicesSectionUI();
                SyncActiveDeviceRemoteControlState(device);
            });
        }

        private async void ConnectDevice_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DeviceInfo device)
            {
                if (device.IsViewing)
                    return;

                await ConnectToDeviceAsync(device);
            }
        }

        private void SetConnectingPlaceholder(string name, string ip)
        {
            var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
            var placeholderActions = this.FindControl<StackPanel>("PlaceholderActionsPanel");
            if (placeholderTitle != null) placeholderTitle.Text = "Conectando al anfitrión...";
            if (placeholderSubtitle != null) placeholderSubtitle.Text = $"Estableciendo conexión segura con {name} ({ip})...";
            if (placeholderActions != null) placeholderActions.IsVisible = false;
        }

        private void ShowViewerControls(DeviceInfo device)
        {
            var canvasSourceTitle = this.FindControl<TextBlock>("CanvasSourceTitleText");
            if (canvasSourceTitle != null) canvasSourceTitle.Text = $"Viendo: {device.Name}";

            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
            if (placeholderSubtitle != null) placeholderSubtitle.Text = $"Conectado a {device.Name}. Esperando primer fotograma...";

            SetViewerControlVisibility("FullscreenButton", true);
            SetViewerControlVisibility("StretchModeButton", true);
            SetViewerControlVisibility("PopoutWindowButton", true);
            SetViewerControlVisibility("DisconnectButton", true);
            SetViewerControlVisibility("AudioControlPanel", true);
            UpdateAudioUI();

            SetViewerControlVisibility("MicControlPanel", true);
            UpdateMicUI();

            SetViewerControlVisibility("ViewerVoiceChatPanel", true);
            UpdateViewerVoiceChatUI(_isViewerVoiceChatActive);

            ApplyViewerPermissions(_viewerPermissions, isLiveUpdate: false);
        }

        private void SetViewerControlVisibility(string controlName, bool isVisible)
        {
            var control = this.FindControl<Control>(controlName);
            if (control != null) control.IsVisible = isVisible;
        }

        private void StartViewerAuxiliaryChannels(string ipAddress, SessionHandshakeMessage? handshake)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var audioCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _audioClient.ConnectAsync(new AudioClientConnectionOptions(ipAddress, 5002, _sslClientOptions, handshake?.SessionId, handshake?.AudioToken), audioCts.Token);
                    if (_isViewerVoiceChatActive)
                    {
                        _micCaptureService?.StartCapture();
                        Console.WriteLine("[Viewer] Micrófono de retorno conectado e iniciado.");
                    }
                    Dispatcher.UIThread.Post(UpdateAudioUI);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioClient] Audio no disponible en el host: {ex.Message}");
                }
            }, CancellationToken.None);

            if (_viewerPermissions.AllowFileTransfer)
            {
                ConnectFileTransferChannelIfAllowed(ipAddress, handshake);
            }
        }

        private void ConnectFileTransferChannelIfAllowed(string? ipAddress, SessionHandshakeMessage? handshake)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var fileCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _fileTransferClient.ConnectAsync(new FileTransferClientConnectionOptions(ipAddress, 5004, _sslClientOptions, handshake?.SessionId, handshake?.FileTransferToken), fileCts.Token);
                    Console.WriteLine($"[Viewer] Canal de transferencia de archivos conectado a {ipAddress}:5004.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FileTransferClient] Canal de archivos no disponible en el host: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        private async Task ConnectToDeviceAsync(DeviceInfo device)
        {
            StopDeviceSearch();
            _currentConnectedDeviceName = device.Name;
            _currentConnectedDeviceIp = device.IPAddress;
            _viewerPermissions = new EffectiveSessionPermissions
            {
                AllowRemoteControl = device.IsRemoteControlAllowed,
                AllowSourceSwitch = device.IsRemoteControlAllowed,
                AllowFileTransfer = true,
                AllowClipboardSync = device.IsRemoteControlAllowed
            };
            UpdateStatus($"Conectando a {device.Name} ({device.IPAddress}:5001)...", StatusWarningAmberHex);
            ResetMetrics();
            SetConnectingPlaceholder(device.Name, device.IPAddress);

            try
            {
                await _streamClient.ConnectAsync(device.IPAddress, 5001, _sslClientOptions, CancellationToken.None);
                _isViewing = true;
                UpdateStreamStateBadge();
                UpdateDevicesViewingState();

                var handshake = _streamClient.ActiveSessionHandshake;
                if (handshake != null)
                {
                    _viewerPermissions = handshake.GetEffectivePermissions();
                }

                ShowViewerControls(device);
                StartViewerAuxiliaryChannels(device.IPAddress, handshake);
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                await HandleViewerDisconnectedAsync($"No se pudo conectar a {device.Name}: {detail}");
                Console.WriteLine($"[ConnectError] No se pudo conectar a {device.Name}: {ex}");
            }
        }

        private void StreamClient_FrameReceived(object? sender, CapturedFrame frame)
        {
            Interlocked.Increment(ref _metricFramesCount);
            Interlocked.Add(ref _metricBytesCount, frame.Data.Length);
            _lastFrameWidth = frame.Width;
            _lastFrameHeight = frame.Height;
            _lastHostFps = frame.HostFps;
            _lastHostFrameTimeMs = frame.HostFrameTimeMs;
            _avSyncController.OnVideoFrameReceived(frame.Timestamp);

            try
            {
                if (frame.Format == "h264")
                {
                    HandleH264FrameReceived(frame);
                }
                else
                {
                    HandleJpegFrameReceived(frame);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamViewer] Error renderizando frame: {ex.Message}");
            }
        }

        private void HandleH264FrameReceived(CapturedFrame frame)
        {
            if (_h264Decoder == null || !_h264Decoder.TryDecodeFrame(frame.Data, frame.Width, frame.Height, out var decodedFrame))
            {
                return;
            }

            byte[] bgraPixels = decodedFrame.Pixels;
            lock (_bitmapLock)
            {
                if (_latestDecodedBgra == null || _latestDecodedBgra.Length != bgraPixels.Length)
                {
                    _latestDecodedBgra = new byte[bgraPixels.Length];
                }
                Buffer.BlockCopy(bgraPixels, 0, _latestDecodedBgra, 0, bgraPixels.Length);
                _latestDecodedWidth = decodedFrame.Width;
                _latestDecodedHeight = decodedFrame.Height;
            }

            if (Interlocked.Exchange(ref _isUiRenderPending, 1) == 0)
            {
                Dispatcher.UIThread.Post(RenderLatestH264Frame, DispatcherPriority.Render);
            }
        }

        private static bool HasDecodedPixels(byte[]? bgra, int width, int height) =>
            bgra != null && width > 0 && height > 0;

        private void RenderLatestH264Frame()
        {
            Interlocked.Exchange(ref _isUiRenderPending, 0);
            try
            {
                lock (_bitmapLock)
                {
                    if (!HasDecodedPixels(_latestDecodedBgra, _latestDecodedWidth, _latestDecodedHeight))
                        return;

                    EnsureViewerWriteableBitmap(_latestDecodedWidth, _latestDecodedHeight);

                    using var fb = _viewerWriteableBitmap!.Lock();
                    int copyLength = Math.Min(_latestDecodedBgra!.Length, fb.RowBytes * _latestDecodedHeight);
                    Marshal.Copy(_latestDecodedBgra, 0, fb.Address, copyLength);
                }

                ApplyViewerFrameToUi(_viewerWriteableBitmap, "H.264 GPU");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamViewer] Error renderizando frame H.264: {ex.Message}");
            }
        }

        private bool MatchesBitmapDimensions(int width, int height) =>
            _viewerWriteableBitmap != null &&
            _viewerWriteableBitmap.PixelSize.Width == width &&
            _viewerWriteableBitmap.PixelSize.Height == height;

        private void EnsureViewerWriteableBitmap(int width, int height)
        {
            if (MatchesBitmapDimensions(width, height))
                return;

            _viewerWriteableBitmap?.Dispose();
            _viewerWriteableBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        private void HandleJpegFrameReceived(CapturedFrame frame)
        {
            using var ms = new MemoryStream(frame.Data);
            var bitmap = new Bitmap(ms);

            Dispatcher.UIThread.Post(() => ApplyViewerFrameToUi(bitmap, "JPEG"));
        }

        private void ApplyViewerFrameToUi(IImage? frameImage, string codecLabel)
        {
            if (frameImage == null) return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                if (screenImage.Source != frameImage)
                {
                    screenImage.Source = frameImage;
                }
                screenImage.InvalidateVisual();
            }

            if (_isPopoutActive && _popoutWindow != null)
            {
                _popoutWindow.UpdateFrame(frameImage);
            }

            var placeholder = this.FindControl<StackPanel>("PlaceholderPanel");
            if (placeholder != null && placeholder.IsVisible)
            {
                placeholder.IsVisible = false;
            }

            UpdateStatus($"Recibiendo pantalla de {_currentConnectedDeviceName} ({codecLabel})", "#22C55E");
        }

        private void StreamClient_StatusChanged(object? sender, string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (status.StartsWith("Conectando", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(status, StatusWarningAmberHex);
                    return;
                }

                if (!_streamClient.IsConnected)
                {
                    if (!_isHosting && _isViewing)
                    {
                        HandleViewerDisconnected(status);
                        return;
                    }

                    UpdateStatus(status, "#7C8B9E");
                    HideViewerControls();
                    return;
                }

                UpdateStatus(status, "#22C55E");
                SetViewerControlVisibility("DisconnectButton", true);
                SetViewerControlVisibility("AudioControlPanel", true);
                UpdateAudioUI();

                SetViewerControlVisibility("MicControlPanel", true);
                UpdateMicUI();

                SetViewerControlVisibility("ViewerVoiceChatPanel", true);
                UpdateViewerVoiceChatUI(_isViewerVoiceChatActive);

                ApplyViewerPermissions(_viewerPermissions, isLiveUpdate: false);
            });
        }

        private async void DisconnectButton_Click(object? sender, RoutedEventArgs? e = null)
        {
            if (_isExtendingDisplay)
            {
                await StopExtendedProjectionAsync();
                return;
            }

            if (_isHosting)
            {
                StopHosting();
                return;
            }

            await HandleViewerDisconnectedAsync("Te has desconectado de la transmisión.");
        }

        private void HandleViewerDisconnected(string reason)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await HandleViewerDisconnectedAsync(reason);
            });
        }

        private async Task DisconnectViewerServicesAsync()
        {
            try { await _streamClient.DisconnectAsync(); } catch { /* Ignore disconnect errors during viewer teardown */ }
            try { await _audioClient.DisconnectAsync(); } catch { /* Ignore disconnect errors during viewer teardown */ }
            try { await _controlClient.DisconnectAsync(); } catch { /* Ignore disconnect errors during viewer teardown */ }
            try { await _fileTransferClient.DisconnectAsync(); } catch { /* Ignore disconnect errors during viewer teardown */ }
        }

        private static bool IsDisconnectFailure(string reason) =>
            reason.Contains("perdida", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("No se pudo", StringComparison.OrdinalIgnoreCase);

        private readonly record struct PlaceholderInfo(string Title, string Subtitle, string StatusMsg, string StatusColor);

        private static PlaceholderInfo DeterminePlaceholderInfo(string reason)
        {
            if (reason.Contains("finaliz", StringComparison.OrdinalIgnoreCase))
            {
                return new PlaceholderInfo(
                    "Transmisión Finalizada",
                    "El anfitrión ha finalizado la transmisión.\nPuedes iniciar tu propia transmisión o conectarte a otro equipo.",
                    "El anfitrión ha finalizado la transmisión",
                    StatusWarningAmberHex);
            }
            if (IsDisconnectFailure(reason))
            {
                return new PlaceholderInfo("Conexión Perdida", reason, reason, "#EF4444");
            }
            return new PlaceholderInfo(
                "Todo listo para compartir",
                "Comparte una pantalla o una aplicación con otros equipos.\nTú eliges qué mostrar y quién puede controlarlo.",
                "Sin conexión activa",
                "#34D399");
        }

        private void UpdatePlaceholderOnDisconnect(string reason)
        {
            SetViewerControlVisibility("PlaceholderPanel", true);
            SetViewerControlVisibility("PlaceholderActionsPanel", true);

            var info = DeterminePlaceholderInfo(reason);

            var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
            if (placeholderTitle != null) placeholderTitle.Text = info.Title;

            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
            if (placeholderSubtitle != null) placeholderSubtitle.Text = info.Subtitle;

            UpdateStatus(info.StatusMsg, info.StatusColor);
        }

        private async Task HandleViewerDisconnectedAsync(string reason)
        {
            _isViewing = false;
            UpdateStreamStateBadge();
            ClosePopoutWindow();
            SetFullscreen(false);
            StopDeviceSearch();
            _currentConnectedDeviceIp = null;
            _currentConnectedDeviceName = null;
            UpdateDevicesViewingState();
            UpdateSourceUI();
            OnSettingChanged();

            await DisconnectViewerServicesAsync();
            await ResetWirelessReceiverSessionAsync();

            if (!_isHosting)
            {
                _micCaptureService?.StopCapture();
            }

            _audioPlayerService?.Stop();
            _micAudioPlayerService?.Stop();
            _remoteControlEnabled = false;

            if (_isGameMode)
            {
                _isGameMode = false;
                UpdateGameModeUI();
            }

            ResetMetrics();

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Source = null;
            }

            _viewerWriteableBitmap?.Dispose();
            _viewerWriteableBitmap = null;

            UpdatePlaceholderOnDisconnect(reason);
            UpdateSecureDesktopBanner(false);
            HideViewerControls();
        }

        private void HideViewerButtons()
        {
            SetViewerControlVisibility("FullscreenButton", false);
            SetViewerControlVisibility("StretchModeButton", false);
            SetViewerControlVisibility("PopoutWindowButton", false);
            SetViewerControlVisibility("DisconnectButton", false);
            SetViewerControlVisibility("AudioControlPanel", false);
            SetViewerControlVisibility("MicControlPanel", false);
            SetViewerControlVisibility("ViewerVoiceChatPanel", false);
            SetViewerControlVisibility("SendFileButton", false);

            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsVisible = false;
                remoteControlBtn.IsChecked = false;
            }

            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsVisible = false;
                gameModeBtn.IsChecked = false;
            }
        }

        private void ResetViewerMonitorLabels()
        {
            _currentRemoteMonitorLabel = "Pantalla";
            _remoteMonitorCatalog = null;
            _remoteSourceMenu?.Close();

            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (switchMonitorBtn != null)
            {
                switchMonitorBtn.IsVisible = false;
            }
            var switchMonitorText = this.FindControl<TextBlock>("SwitchMonitorButtonText");
            if (switchMonitorText != null)
            {
                switchMonitorText.Text = "Pantalla";
            }
        }

        private void HideViewerControls()
        {
            HideViewerButtons();
            ResetViewerMonitorLabels();
        }

        #endregion
    }
}
