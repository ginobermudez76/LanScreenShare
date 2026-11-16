using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Display
{
    public sealed class VirtualDisplayCoordinator : IVirtualDisplayService
    {
        private readonly IReadOnlyList<IVirtualDisplayBackend> _backends;
        private readonly IDisplayTopologyManager _topologyManager;
        private readonly IWatchdogProcessLauncher? _watchdogLauncher;
        private readonly VirtualDisplayCoordinatorOptions _options;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

        private IVirtualDisplayBackend? _activeBackend;
        private VirtualDisplayInstance? _activeInstance;
        private DisplayTopologySnapshot? _savedSnapshot;
        private IDisposable? _watchdogHandle;
        private CancellationTokenSource? _heartbeatCts;
        private CancellationTokenSource? _reconnectCts;
        private bool _disposed;

        public VirtualDisplayState State { get; private set; } = VirtualDisplayState.Inactive;
        public VirtualDisplaySessionInfo? CurrentSession { get; private set; }
        public event EventHandler<VirtualDisplayStateChangedEventArgs>? StateChanged;

        public VirtualDisplayCoordinator(
            IReadOnlyList<IVirtualDisplayBackend> backends,
            IDisplayTopologyManager topologyManager,
            IWatchdogProcessLauncher? watchdogLauncher = null,
            VirtualDisplayCoordinatorOptions? options = null)
        {
            _backends = backends ?? throw new ArgumentNullException(nameof(backends));
            _topologyManager = topologyManager ?? throw new ArgumentNullException(nameof(topologyManager));
            _watchdogLauncher = watchdogLauncher;
            _options = options ?? new VirtualDisplayCoordinatorOptions();
        }

        public async Task<VirtualDisplayOperationResult> StartExtendedDisplayAsync(
            VirtualDisplayMode requestedMode,
            CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in requestedMode))
                return VirtualDisplayOperationResult.Fail("ERROR_INVALID_MODE", "Modo de pantalla fuera de los límites permitidos.");

            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (IsSessionActive())
                    return VirtualDisplayOperationResult.Fail("ERROR_DISPLAY_BUSY", "Ya existe un monitor virtual extendido activo.");

                return await StartExtendedDisplayCoreAsync(requestedMode, ct);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private bool IsSessionActive() =>
            State != VirtualDisplayState.Inactive && State != VirtualDisplayState.Error;

        private async Task<VirtualDisplayOperationResult> StartExtendedDisplayCoreAsync(
            VirtualDisplayMode requestedMode,
            CancellationToken ct)
        {
            TransitionState(VirtualDisplayState.DriverCheck, "Verificando controlador virtual IDDCx");
            var backend = SelectAvailableBackend();
            if (backend == null)
            {
                TransitionState(VirtualDisplayState.Error, "No se encontró ningún controlador IDDCx instalado");
                return VirtualDisplayOperationResult.Fail("ERROR_DRIVER_NOT_INSTALLED", "No se encontró un controlador de pantalla virtual instalado.");
            }

            _activeBackend = backend;
            var baseSnapshot = _topologyManager.CaptureSnapshot();

            TransitionState(VirtualDisplayState.CreatingDisplay, $"Creando monitor virtual con {backend.Capabilities.BackendId}");
            var instance = await TryCreateVirtualMonitorAsync(backend, requestedMode, ct);
            if (instance == null)
                return await AbortStartAsync("ERROR_CREATE_FAILED", "No se pudo inicializar el monitor virtual.", baseSnapshot, ct);

            _activeInstance = instance;
            _savedSnapshot = baseSnapshot with
            {
                BackendId = backend.Capabilities.BackendId,
                VirtualMonitorId = instance.MonitorId,
                VirtualDeviceName = instance.DeviceName
            };
            await _savedSnapshot.SaveToFileAsync(_options.SnapshotFilePath, ct);

            _watchdogHandle = _watchdogLauncher?.StartWatchdog(new VirtualDisplayWatchdogLaunchOptions
            {
                ParentPid = Environment.ProcessId,
                SnapshotPath = _options.SnapshotFilePath,
                MonitorId = instance.MonitorId,
                BackendId = backend.Capabilities.BackendId
            });

            TransitionState(VirtualDisplayState.ConfiguringTopology, "Aplicando topología Extendida (CCD)");
            if (!_topologyManager.ApplyExtendTopology(instance.DeviceName, requestedMode))
                return await AbortStartAsync("ERROR_TOPOLOGY_FAILED", "No se pudo aplicar la topología extendida en Windows.", _savedSnapshot, ct);

            CurrentSession = new VirtualDisplaySessionInfo
            {
                BackendId = backend.Capabilities.BackendId,
                MonitorId = instance.MonitorId,
                DeviceName = instance.DeviceName,
                ActiveMode = instance.Mode,
                SnapshotPath = _options.SnapshotFilePath
            };

            StartHeartbeatLoop(backend, instance.MonitorId);
            TransitionState(VirtualDisplayState.ConnectingStream, "Esperando conexión del receptor");

            if (_options.AutoEnterStreamingState)
            {
                TransitionState(VirtualDisplayState.Streaming, "Pantalla extendida transmitiendo");
            }

            return VirtualDisplayOperationResult.Ok(CurrentSession);
        }

        private async Task<VirtualDisplayInstance?> TryCreateVirtualMonitorAsync(
            IVirtualDisplayBackend backend,
            VirtualDisplayMode mode,
            CancellationToken ct)
        {
            try
            {
                return await backend.CreateVirtualMonitorAsync(mode, ct);
            }
            catch
            {
                return null;
            }
        }

        private async Task<VirtualDisplayOperationResult> AbortStartAsync(
            string errorCode,
            string message,
            DisplayTopologySnapshot rollbackSnapshot,
            CancellationToken ct)
        {
            await TeardownActiveResourcesAsync(rollbackSnapshot, ct);
            TransitionState(VirtualDisplayState.Inactive, message);
            return VirtualDisplayOperationResult.Fail(errorCode, message);
        }

        public async Task<VirtualDisplayOperationResult> NotifyStreamConnectedAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (CurrentSession == null)
                    return VirtualDisplayOperationResult.Fail("ERROR_NO_SESSION", "No existe una sesión de pantalla extendida.");

                CancelReconnectTimer();
                TransitionState(VirtualDisplayState.Streaming, "Receptor conectado al monitor virtual");
                return VirtualDisplayOperationResult.Ok(CurrentSession);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task<VirtualDisplayOperationResult> ChangeDisplayModeAsync(
            VirtualDisplayMode newMode,
            CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in newMode))
                return VirtualDisplayOperationResult.Fail("ERROR_INVALID_MODE", "Modo solicitado fuera de rango.");

            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (_activeBackend == null || _activeInstance == null || CurrentSession == null)
                    return VirtualDisplayOperationResult.Fail("ERROR_NO_SESSION", "No hay monitor virtual activo.");

                if (_activeBackend.Capabilities.SupportsDynamicModeChange)
                {
                    bool backendUpdated = await _activeBackend.UpdateModeAsync(_activeInstance.MonitorId, newMode, ct);
                    if (!backendUpdated)
                        return VirtualDisplayOperationResult.Fail("ERROR_MODE_UPDATE_FAILED", "El controlador rechazó el cambio de modo.");
                }

                if (!_topologyManager.ApplyExtendTopology(_activeInstance.DeviceName, newMode))
                    return VirtualDisplayOperationResult.Fail("ERROR_TOPOLOGY_FAILED", "No se pudo aplicar el nuevo modo en CCD.");

                _activeInstance = _activeInstance with { Mode = newMode };
                CurrentSession = CurrentSession with { ActiveMode = newMode };
                return VirtualDisplayOperationResult.Ok(CurrentSession);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task HandleNetworkDisconnectedAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (State != VirtualDisplayState.Streaming)
                    return;

                TransitionState(VirtualDisplayState.Reconnecting, "Pérdida temporal de red; conservando monitor virtual");
                StartReconnectTimer();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task HandleNetworkReconnectedAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (State != VirtualDisplayState.Reconnecting)
                    return;

                CancelReconnectTimer();
                TransitionState(VirtualDisplayState.Streaming, "Conectividad recuperada");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task HandleSystemSuspendAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (State != VirtualDisplayState.Streaming && State != VirtualDisplayState.Reconnecting)
                    return;

                CancelReconnectTimer();
                TransitionState(VirtualDisplayState.Suspended, "Sistema en suspensión");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task HandleSystemResumeAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (State != VirtualDisplayState.Suspended || _activeInstance == null)
                    return;

                TransitionState(VirtualDisplayState.SuspendRecovery, "Reanudando pantalla virtual tras suspensión");
                if (_topologyManager.ApplyExtendTopology(_activeInstance.DeviceName, _activeInstance.Mode))
                {
                    TransitionState(VirtualDisplayState.Streaming, "Monitor virtual reanudado");
                    return;
                }

                await TeardownActiveResourcesAsync(_savedSnapshot, ct);
                TransitionState(VirtualDisplayState.Inactive, "Fallo irrecuperable tras suspensión");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopExtendedDisplayAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                if (State == VirtualDisplayState.Inactive && _activeInstance == null)
                    return;

                TransitionState(VirtualDisplayState.Destroying, "Desmontando monitor virtual y restaurando topología");
                await TeardownActiveResourcesAsync(_savedSnapshot, ct);
                TransitionState(VirtualDisplayState.Inactive, "Pantalla extendida finalizada");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task RecoverOrphanedSessionOnStartupAsync(CancellationToken ct = default)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                var orphan = await DisplayTopologySnapshot.TryLoadFromFileAsync(_options.SnapshotFilePath, ct);
                if (orphan == null)
                    return;

                await DestroyOrphanBackendMonitorAsync(orphan, ct);
                _topologyManager.RestoreSnapshot(orphan);
                DisplayTopologySnapshot.DeleteFileIfExists(_options.SnapshotFilePath);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task DestroyOrphanBackendMonitorAsync(DisplayTopologySnapshot orphan, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(orphan.VirtualMonitorId))
                return;

            var matching = _backends.FirstOrDefault(b =>
                string.Equals(b.Capabilities.BackendId, orphan.BackendId, StringComparison.OrdinalIgnoreCase));

            if (matching != null)
            {
                await TryDestroyBackendMonitorAsync(matching, orphan.VirtualMonitorId, ct);
            }
        }

        private IVirtualDisplayBackend? SelectAvailableBackend() =>
            _backends.FirstOrDefault(b => b.IsDriverInstalled());

        private async Task TeardownActiveResourcesAsync(DisplayTopologySnapshot? snapshotToRestore, CancellationToken ct)
        {
            CancelReconnectTimer();
            StopHeartbeatLoop();

            if (_activeBackend != null && _activeInstance != null)
            {
                await TryDestroyBackendMonitorAsync(_activeBackend, _activeInstance.MonitorId, ct);
            }

            if (snapshotToRestore != null)
            {
                _topologyManager.RestoreSnapshot(snapshotToRestore);
            }

            DisplayTopologySnapshot.DeleteFileIfExists(_options.SnapshotFilePath);
            _watchdogHandle?.Dispose();
            _watchdogHandle = null;
            _activeBackend = null;
            _activeInstance = null;
            _savedSnapshot = null;
            CurrentSession = null;
        }

        private static async Task TryDestroyBackendMonitorAsync(
            IVirtualDisplayBackend backend,
            string monitorId,
            CancellationToken ct)
        {
            try
            {
                await backend.DestroyVirtualMonitorAsync(monitorId, ct);
            }
            catch
            {
                // Best-effort cleanup of virtual monitor
            }
        }

        private void StartHeartbeatLoop(IVirtualDisplayBackend backend, string monitorId)
        {
            StopHeartbeatLoop();
            var cts = new CancellationTokenSource();
            _heartbeatCts = cts;
            var interval = _options.HeartbeatInterval > TimeSpan.Zero
                ? _options.HeartbeatInterval
                : TimeSpan.FromMilliseconds(200);

            _ = Task.Run(() => RunHeartbeatAsync(backend, monitorId, interval, cts.Token), CancellationToken.None);
        }

        private static async Task RunHeartbeatAsync(
            IVirtualDisplayBackend backend,
            string monitorId,
            TimeSpan interval,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await backend.SendHeartbeatAsync(monitorId, token);
                    await Task.Delay(interval, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient heartbeat errors until stop
                }
            }
        }

        private void StopHeartbeatLoop()
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
        }

        private void StartReconnectTimer()
        {
            CancelReconnectTimer();
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            var timeout = _options.ReconnectTimeout > TimeSpan.Zero
                ? _options.ReconnectTimeout
                : TimeSpan.FromSeconds(15);

            _ = Task.Run(() => RunReconnectExpiryAsync(timeout, cts.Token), CancellationToken.None);
        }

        private async Task RunReconnectExpiryAsync(TimeSpan timeout, CancellationToken token)
        {
            try
            {
                await Task.Delay(timeout, token);
                await StopExtendedDisplayIfReconnectingAsync();
            }
            catch (OperationCanceledException)
            {
                // Cancelled when reconnected or stopped
            }
        }

        private async Task StopExtendedDisplayIfReconnectingAsync()
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                if (State != VirtualDisplayState.Reconnecting)
                    return;

                TransitionState(VirtualDisplayState.Destroying, "Timeout de reconexión agotado");
                await TeardownActiveResourcesAsync(_savedSnapshot, CancellationToken.None);
                TransitionState(VirtualDisplayState.Inactive, "Monitor virtual destruido por timeout");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private void CancelReconnectTimer()
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }

        private void TransitionState(VirtualDisplayState nextState, string? reason)
        {
            var previous = State;
            State = nextState;
            StateChanged?.Invoke(this, new VirtualDisplayStateChangedEventArgs(previous, nextState, reason));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            await StopExtendedDisplayAsync(CancellationToken.None);
            _lifecycleLock.Dispose();
        }
    }
}
