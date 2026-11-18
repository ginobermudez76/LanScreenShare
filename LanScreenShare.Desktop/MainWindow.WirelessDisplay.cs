using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Display;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Media;
using LanScreenShare.Media.Display;
using LanScreenShare.Network;
using LanScreenShare.Network.Display;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Desktop
{
    public enum VirtualDisplayPowerEvent
    {
        None = 0,
        Suspend = 1,
        Resume = 2
    }

    public partial class MainWindow
    {
        private const uint WmPowerBroadcast = 0x0218;
        private const uint WmDisplayChange = 0x007E;
        private const int PbtApmSuspend = 0x0004;
        private const int PbtApmResumeSuspended = 0x0007;
        private const int PbtApmResumeAutomatic = 0x0012;
        private const string WirelessIndigoHex = "#6366F1";

        private VirtualDisplayCoordinator? _virtualDisplayService;
        private IDisplayTopologyManager? _topologyManager;
        private readonly ProjectionInvitationStore _projectionInvitationStore = new();
        private VirtualDisplayProjectionHost? _projectionHost;
        private VirtualDisplayProjectionReceiver? _projectionReceiver;
        private Win32Properties.CustomWndProcHookCallback? _powerWndProcHook;
        private CancellationTokenSource? _displayChangeGuardCts;
        private nint _cachedWindowHwnd;

        private bool _isWirelessDisplayModeActive;
        private bool _isExtendingDisplay;
        private string? _extendedTargetIp;

        internal static VirtualDisplayPowerEvent ParsePowerBroadcast(uint msg, nint wParam)
        {
            if (msg != WmPowerBroadcast)
                return VirtualDisplayPowerEvent.None;

            int code = unchecked((int)wParam.ToInt64());
            if (code == PbtApmSuspend)
                return VirtualDisplayPowerEvent.Suspend;

            if (code is PbtApmResumeAutomatic or PbtApmResumeSuspended)
                return VirtualDisplayPowerEvent.Resume;

            return VirtualDisplayPowerEvent.None;
        }

        private void InitializeVirtualDisplaySubsystem()
        {
            _topologyManager = new WindowsDisplayTopologyManager();
            _virtualDisplayService = new VirtualDisplayCoordinator(
                VirtualDisplayDriverInstallerService.CreateDefaultBackends(),
                _topologyManager,
                new ProcessWatchdogLauncher(),
                new VirtualDisplayCoordinatorOptions { AutoEnterStreamingState = false });
            _virtualDisplayService.StateChanged += VirtualDisplayService_StateChanged;

            WireButtonClick("WirelessDisplayModeButton", async (_, _) => await ToggleWirelessDisplayModeAsync());
            WireButtonClick("CenterWirelessDisplayButton", async (_, _) => await ToggleWirelessDisplayModeAsync());

            if (OperatingSystem.IsWindows())
            {
                _cachedWindowHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                _powerWndProcHook = (nint hwnd, uint msg, nint wParam, nint _, ref bool _) =>
                {
                    if (hwnd != IntPtr.Zero)
                    {
                        _cachedWindowHwnd = hwnd;
                    }
                    DispatchPowerBroadcastMessage(msg, wParam);
                    return IntPtr.Zero;
                };
                Win32Properties.AddWndProcHookCallback(this, _powerWndProcHook);
            }

            _ = RecoverOrphanedVirtualDisplayOnStartupAsync();
        }

        private void DispatchPowerBroadcastMessage(uint msg, nint wParam)
        {
            if (msg == WmDisplayChange)
            {
                if (_isExtendingDisplay)
                {
                    ScheduleExtendedTopologyGuardCheck();
                }
                return;
            }

            var powerEvent = ParsePowerBroadcast(msg, wParam);
            if (powerEvent == VirtualDisplayPowerEvent.Suspend)
            {
                _ = _virtualDisplayService?.HandleSystemSuspendAsync(CancellationToken.None);
            }
            else if (powerEvent == VirtualDisplayPowerEvent.Resume)
            {
                _ = HandleResumeAndKeyframeAsync();
            }
        }

        private void ScheduleExtendedTopologyGuardCheck()
        {
            _displayChangeGuardCts?.Cancel();
            _displayChangeGuardCts?.Dispose();
            var cts = new CancellationTokenSource();
            _displayChangeGuardCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300, token);
                    if (token.IsCancellationRequested)
                        return;

                    GuardExtendedTopologyStep(
                        _isExtendingDisplay,
                        _projectionHost,
                        _topologyManager);
                }
                catch (OperationCanceledException)
                {
                    // Reemplazado por un evento WM_DISPLAYCHANGE más reciente
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VirtualDisplay] Advertencia validando topología tras WM_DISPLAYCHANGE: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        internal static void GuardExtendedTopologyStep(
            bool isExtendingDisplay,
            VirtualDisplayProjectionHost? host,
            IDisplayTopologyManager? topologyManager)
        {
            if (!isExtendingDisplay || host is not { IsProjecting: true })
                return;

            if (topologyManager == null)
                return;

            if (topologyManager is WindowsDisplayTopologyManager winTopo)
            {
                winTopo.EnsureExtendedLayout(host.ActiveDisplaySession?.DeviceName);
            }
            else
            {
                topologyManager.ApplyExtendTopology(
                    host.ActiveDisplaySession?.DeviceName,
                    host.ActiveDisplaySession?.ActiveMode ?? VirtualDisplayMode.Default);
            }

            host.RefreshCaptureSourceFromTopology();
        }

        private async Task HandleResumeAndKeyframeAsync()
        {
            if (_virtualDisplayService == null)
                return;

            await _virtualDisplayService.HandleSystemResumeAsync(CancellationToken.None);
            _screenCaptureService?.RequestKeyframe();
        }

        private async Task RecoverOrphanedVirtualDisplayOnStartupAsync()
        {
            if (_virtualDisplayService == null)
                return;

            try
            {
                await _virtualDisplayService.RecoverOrphanedSessionOnStartupAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VirtualDisplay] Advertencia en limpieza de inicio: {ex.Message}");
            }
        }

        private void VirtualDisplayService_StateChanged(object? sender, VirtualDisplayStateChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isExtendingDisplay && e.CurrentState is VirtualDisplayState.Inactive or VirtualDisplayState.Error)
                {
                    _isExtendingDisplay = false;
                    _extendedTargetIp = null;
                    UpdateDevicesViewingState();
                    UpdateStreamStateBadge();
                    SetElementVisibility("DisconnectButton", _isViewing);
                }

                if (!string.IsNullOrWhiteSpace(e.Reason))
                {
                    string color = e.CurrentState == VirtualDisplayState.Error ? "#EF4444" : WirelessIndigoHex;
                    UpdateStatus(e.Reason, color);
                }
            });
        }

        private async Task ToggleWirelessDisplayModeAsync()
        {
            if (_isWirelessDisplayModeActive)
            {
                await DisableWirelessDisplayModeAsync();
                return;
            }

            await EnableWirelessDisplayModeAsync();
        }

        private async Task EnableWirelessDisplayModeAsync()
        {
            if (_isHosting) StopHosting();
            if (_isExtendingDisplay) await StopExtendedProjectionAsync();
            if (_isViewing) await HandleViewerDisconnectedAsync("Cambiando a Modo Pantalla Inalámbrica...");

            if (OperatingSystem.IsWindows())
            {
                _cachedWindowHwnd = TryGetPlatformHandle()?.Handle ?? _cachedWindowHwnd;
            }

            _discoveryService.IsWirelessDisplayBusyProvider = () => _projectionReceiver?.IsSessionBusy ?? _isViewing;
            _projectionReceiver = new VirtualDisplayProjectionReceiver(new VirtualDisplayProjectionReceiverOptions
            {
                SignalingServerSslOptions = _sslServerOptions,
                CapabilitiesProvider = BuildLocalDisplayCapabilities,
                ApprovalHandler = HandleIncomingProjectionInviteAsync,
                StreamClient = _streamClient,
                ControlClient = _controlClient
            });
            _projectionReceiver.InvitationAccepted += OnProjectionInvitationAccepted;

            await _projectionReceiver.StartListeningAsync(DiscoveryService.DefaultDisplaySignalingPort, CancellationToken.None);
            _discoveryService.StartWirelessDisplayBroadcast(DiscoveryService.DefaultDisplaySignalingPort);

            _isWirelessDisplayModeActive = true;
            UpdateWirelessModeButtonsUi(true);
            UpdateStreamStateBadge();
            UpdateStatus("Modo Pantalla Inalámbrica activo · Esperando invitación mTLS", "#8B5CF6");
        }

        private async Task DisableWirelessDisplayModeAsync()
        {
            _isWirelessDisplayModeActive = false;
            _discoveryService.StopWirelessDisplayBroadcast();

            if (_projectionReceiver != null)
            {
                _projectionReceiver.InvitationAccepted -= OnProjectionInvitationAccepted;
                await _projectionReceiver.DisposeAsync();
                _projectionReceiver = null;
            }

            if (_isViewing)
            {
                await HandleViewerDisconnectedAsync("Modo Pantalla Inalámbrica desactivado.");
            }

            UpdateWirelessModeButtonsUi(false);
            UpdateStreamStateBadge();
            UpdateStatus("Modo Pantalla Inalámbrica desactivado", "#34D399");
        }

        private DisplayCapabilitiesDto BuildLocalDisplayCapabilities()
        {
            var (nativeMode, dpiScale) = ResolveLocalPhysicalDisplayMode();
            return BuildDisplayCapabilities(nativeMode, dpiScale, IsLocalDisplaySessionBusy());
        }

        private bool IsLocalDisplaySessionBusy()
        {
            if (_isViewing || _isHosting)
                return true;

            return _isExtendingDisplay;
        }

        internal static DisplayCapabilitiesDto BuildDisplayCapabilities(
            VirtualDisplayMode? detectedNativeMode,
            double dpiScale,
            bool isBusy)
        {
            var normalizedNative = NormalizeDisplayMode(detectedNativeMode);
            var referenceMode = normalizedNative ?? VirtualDisplayMode.Default;
            var modes = BuildSupportedModesList(normalizedNative, referenceMode);

            return new DisplayCapabilitiesDto
            {
                DeviceName = Environment.MachineName,
                SupportedModes = modes,
                NativeMode = normalizedNative ?? modes[0],
                DpiScaleFactor = dpiScale > 0 ? dpiScale : 1.0,
                SupportsAuxiliaryInput = false,
                IsBusy = isBusy
            };
        }

        private static List<VirtualDisplayMode> BuildSupportedModesList(
            VirtualDisplayMode? normalizedNative,
            VirtualDisplayMode referenceMode)
        {
            var modes = new List<VirtualDisplayMode>();
            if (normalizedNative is { } native)
            {
                AddModeIfSupported(modes, native);
            }

            AddStandardCandidateModes(modes, referenceMode.Width, referenceMode.Height, referenceMode.RefreshRateHz);
            if (modes.Count == 0)
            {
                modes.Add(VirtualDisplayMode.Default);
            }

            return modes;
        }

        private static void AddStandardCandidateModes(List<VirtualDisplayMode> modes, int maxW, int maxH, int hz)
        {
            (int Width, int Height)[] standardCandidates =
            [
                (3840, 2160),
                (2560, 1440),
                (1920, 1080),
                (1600, 900),
                (1366, 768),
                (1280, 720)
            ];

            foreach (var (w, h) in standardCandidates)
            {
                TryAddCandidateResolution(modes, w, h, maxW, maxH, hz);
            }
        }

        private static void TryAddCandidateResolution(
            List<VirtualDisplayMode> modes,
            int width,
            int height,
            int maxW,
            int maxH,
            int hz)
        {
            if (width > maxW || height > maxH)
                return;

            AddModeIfSupported(modes, new VirtualDisplayMode(width, height, hz));
            if (hz != 60)
            {
                AddModeIfSupported(modes, new VirtualDisplayMode(width, height, 60));
            }
        }

        private static void AddModeIfSupported(List<VirtualDisplayMode> modes, VirtualDisplayMode candidate)
        {
            if (VirtualDisplayModeValidator.IsSupported(in candidate) && !modes.Contains(candidate))
            {
                modes.Add(candidate);
            }
        }

        private static VirtualDisplayMode? NormalizeDisplayMode(VirtualDisplayMode? rawMode)
        {
            if (rawMode is not { } raw)
                return null;

            if (raw.Width <= 0 || raw.Height <= 0)
                return null;

            var (width, height) = ScaleDownToMaxBounds(raw.Width, raw.Height);
            width = Math.Clamp(width & ~1, VirtualDisplayModeValidator.MinWidth, VirtualDisplayModeValidator.MaxWidth);
            height = Math.Clamp(height & ~1, VirtualDisplayModeValidator.MinHeight, VirtualDisplayModeValidator.MaxHeight);
            int targetHz = raw.RefreshRateHz > 0 ? raw.RefreshRateHz : 60;
            int hz = Math.Clamp(targetHz, VirtualDisplayModeValidator.MinRefreshRateHz, 60);

            var normalized = new VirtualDisplayMode(width, height, hz);
            return VirtualDisplayModeValidator.IsSupported(in normalized) ? normalized : null;
        }

        private static (int Width, int Height) ScaleDownToMaxBounds(int width, int height)
        {
            if (width <= VirtualDisplayModeValidator.MaxWidth && height <= VirtualDisplayModeValidator.MaxHeight)
                return (width, height);

            double ratio = Math.Min(
                (double)VirtualDisplayModeValidator.MaxWidth / width,
                (double)VirtualDisplayModeValidator.MaxHeight / height);
            return ((int)Math.Floor(width * ratio), (int)Math.Floor(height * ratio));
        }

        private (VirtualDisplayMode? Mode, double DpiScale) ResolveLocalPhysicalDisplayMode()
        {
            if (WindowsDisplayTopologyManager.TryGetWindowMonitorPhysicalMode(_cachedWindowHwnd, out var winMode, out double winScale))
            {
                return (winMode, winScale);
            }

            try
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    return ReadAvaloniaScreenMode();
                }

                return Dispatcher.UIThread.Invoke(ReadAvaloniaScreenMode);
            }
            catch
            {
                return (null, 1.0);
            }
        }

        private (VirtualDisplayMode? Mode, double DpiScale) ReadAvaloniaScreenMode()
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null)
                return (null, 1.0);

            return (new VirtualDisplayMode(screen.Bounds.Width, screen.Bounds.Height, 60), screen.Scaling);
        }

        private async Task<bool> HandleIncomingProjectionInviteAsync(DisplayInvitationAcceptanceContext context)
        {
            if (string.IsNullOrWhiteSpace(context.PeerFingerprint))
                return false;

            if (await _pairedDeviceStore.IsDevicePairedAsync(context.PeerFingerprint))
                return true;

            bool approved = await RequestPairingApprovalAsync(
                context.PeerDeviceName,
                context.PeerFingerprint[..Math.Min(16, context.PeerFingerprint.Length)]);

            if (approved)
            {
                await _pairedDeviceStore.AddPairedDeviceAsync(
                    PairedDevice.Create(Guid.NewGuid().ToString("D"), context.PeerDeviceName, context.PeerFingerprint));
            }

            return approved;
        }

        private void OnProjectionInvitationAccepted(object? sender, DisplayInvitationAcceptanceContext context)
        {
            Dispatcher.UIThread.Post(async () => await ConnectReceiverToProjectionHostAsync(context));
        }

        private async Task ConnectReceiverToProjectionHostAsync(DisplayInvitationAcceptanceContext context)
        {
            if (_projectionReceiver == null)
                return;

            StopDeviceSearch();
            _currentConnectedDeviceName = context.PeerDeviceName;
            _currentConnectedDeviceIp = context.SenderIpAddress;
            _remoteControlEnabled = false;
            _viewerPermissions = EffectiveSessionPermissions.AllAllowed with
            {
                AllowRemoteControl = false,
                AllowSourceSwitch = false
            };

            UpdateStatus($"Conectando monitor inalámbrico a {context.PeerDeviceName}...", StatusWarningAmberHex);
            SetConnectingPlaceholder(context.PeerDeviceName, context.SenderIpAddress);

            try
            {
                await _projectionReceiver.ConnectToHostAsync(context, _sslClientOptions, CancellationToken.None);
                if (!_streamClient.IsConnected)
                {
                    await HandleViewerDisconnectedAsync("El anfitrión ha finalizado la transmisión.");
                    return;
                }

                _isViewing = true;
                UpdateStreamStateBadge();
                ShowViewerControls(new DeviceInfo { Name = context.PeerDeviceName, IPAddress = context.SenderIpAddress });
                SetFullscreen(true);
            }
            catch (Exception ex)
            {
                await HandleViewerDisconnectedAsync($"Error conectando pantalla inalámbrica: {ex.Message}");
            }
        }

        private async void ExtendToDevice_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DeviceInfo device)
                return;

            if (device.IsExtending)
            {
                await StopExtendedProjectionAsync();
                return;
            }

            await ExtendToDeviceAsync(device);
        }

        private async Task ExtendToDeviceAsync(DeviceInfo device)
        {
            if (!OperatingSystem.IsWindows())
            {
                UpdateStatus("Extender pantalla solo está disponible en Windows 10/11.", StatusWarningAmberHex);
                return;
            }

            if (!await EnsureVirtualDisplayDriverReadyAsync())
                return;

            await PrepareExclusiveSenderSessionAsync();
            await LaunchProjectionToDeviceAsync(device);
        }

        private async Task<bool> EnsureVirtualDisplayDriverReadyAsync()
        {
            if (VirtualDisplayDriverInstallerService.IsAnyVirtualDisplayDriverInstalled())
                return true;

            var wizard = new VddDriverInstallerWindow();
            bool? installed = await wizard.ShowDialog<bool?>(this);
            if (installed == true && VirtualDisplayDriverInstallerService.IsAnyVirtualDisplayDriverInstalled())
                return true;

            UpdateStatus("Se requiere el controlador IDDCx para extender pantalla.", StatusWarningAmberHex);
            return false;
        }

        private async Task PrepareExclusiveSenderSessionAsync()
        {
            if (_isWirelessDisplayModeActive) await DisableWirelessDisplayModeAsync();
            if (_isHosting) StopHosting();
            if (_isViewing) await HandleViewerDisconnectedAsync("Iniciando pantalla inalámbrica extendida...");
            if (_isExtendingDisplay) await StopExtendedProjectionAsync();
        }

        private async Task LaunchProjectionToDeviceAsync(DeviceInfo device)
        {
            if (_virtualDisplayService == null)
                return;

            _projectionHost = new VirtualDisplayProjectionHost(new VirtualDisplayProjectionHostDependencies
            {
                DisplayService = _virtualDisplayService,
                InvitationStore = _projectionInvitationStore,
                StreamServer = _streamServer,
                ControlServer = _controlServer,
                TopologyManager = _topologyManager,
                CaptureFactory = source => CreateCaptureService(source, GetSelectedEngine())
            });

            UpdateStatus($"Negociando monitor inalámbrico con {device.Name}...", StatusWarningAmberHex);
            var profile = BuildCurrentCaptureProfile();
            var result = await _projectionHost.StartProjectionAsync(new DisplayNegotiationRequest
            {
                TargetIp = device.IPAddress,
                TargetPort = device.DisplaySignalingPort > 0 ? device.DisplaySignalingPort : DiscoveryService.DefaultDisplaySignalingPort,
                SslOptions = _sslClientOptions,
                InvitationStore = _projectionInvitationStore
            }, profile, CancellationToken.None);

            if (!result.Success)
            {
                await _projectionHost.DisposeAsync();
                _projectionHost = null;
                UpdateStatus($"No se pudo extender a {device.Name}: {result.ErrorMessage}", "#EF4444");
                return;
            }

            MarkExtendedSessionActive(device);
        }

        private void MarkExtendedSessionActive(DeviceInfo device)
        {
            _isExtendingDisplay = true;
            _extendedTargetIp = device.IPAddress;
            UpdateDevicesViewingState();
            UpdateStreamStateBadge();
            SetElementVisibility("DisconnectButton", true);
            UpdateStatus($"Escritorio extendido hacia {device.Name} ({device.IPAddress})", WirelessIndigoHex);
        }

        private async Task StopExtendedProjectionAsync()
        {
            _isExtendingDisplay = false;
            _extendedTargetIp = null;

            if (_projectionHost != null)
            {
                await _projectionHost.DisposeAsync();
                _projectionHost = null;
            }

            UpdateDevicesViewingState();
            UpdateStreamStateBadge();
            SetElementVisibility("DisconnectButton", _isViewing);
            UpdateStatus("Monitor inalámbrico desconectado y topología restaurada.", "#34D399");
        }

        private async Task ResetWirelessReceiverSessionAsync()
        {
            if (_projectionReceiver != null)
            {
                await _projectionReceiver.DisconnectSessionAsync();
            }
        }

        private void UpdateWirelessModeButtonsUi(bool active)
        {
            SetWirelessControlText("WirelessDisplayModeButtonText", active ? "Monitor inalámbrico activo" : "Modo Pantalla Inalámbrica");
            SetWirelessControlText("CenterWirelessDisplayButtonText", active ? "Desactivar Monitor Inalámbrico" : "Modo Pantalla Inalámbrica");

            var topBtn = this.FindControl<Button>("WirelessDisplayModeButton");
            if (topBtn != null)
                topBtn.Background = new SolidColorBrush(Color.Parse(active ? "#4F46E5" : "#1A2230"));

            UpdateWirelessPlaceholderUi(active);
        }

        private void SetWirelessControlText(string controlName, string text)
        {
            var label = this.FindControl<TextBlock>(controlName);
            if (label != null)
                label.Text = text;
        }

        private void UpdateWirelessPlaceholderUi(bool active)
        {
            if (active)
            {
                SetWirelessControlText("PlaceholderTitleText", "Modo Pantalla Inalámbrica activo");
                SetWirelessControlText("PlaceholderSubtitleText", "Este equipo es visible como 'Monitor inalámbrico'.\nOtro equipo Windows puede pulsar 'Extender pantalla' para proyectar aquí.");
                return;
            }

            if (!_isHosting && !_isViewing)
            {
                SetPlaceholderIdleText();
            }
        }

        private void DisposeVirtualDisplaySubsystemSync()
        {
            _displayChangeGuardCts?.Cancel();
            _displayChangeGuardCts?.Dispose();
            _displayChangeGuardCts = null;

            if (_powerWndProcHook != null && OperatingSystem.IsWindows())
            {
                Win32Properties.RemoveWndProcHookCallback(this, _powerWndProcHook);
                _powerWndProcHook = null;
            }

            _discoveryService.StopWirelessDisplayBroadcast();

            if (_projectionHost != null)
            {
                try { _projectionHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best-effort teardown on close */ }
                _projectionHost = null;
            }

            if (_projectionReceiver != null)
            {
                try { _projectionReceiver.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best-effort teardown on close */ }
                _projectionReceiver = null;
            }

            if (_virtualDisplayService != null)
            {
                _virtualDisplayService.StateChanged -= VirtualDisplayService_StateChanged;
                try { _virtualDisplayService.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* Best-effort teardown on close */ }
                _virtualDisplayService = null;
            }
        }
    }
}
