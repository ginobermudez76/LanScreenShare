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

        private sealed record SessionSetupContext(
            IVirtualDisplayBackend Backend,
            VirtualDisplayInstance Instance,
            DisplayTopologySnapshot BaseSnapshot);

        private async Task<T> RunLockedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            await _lifecycleLock.WaitAsync(ct);
            try
            {
                return await operation(ct);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task RunLockedAsync(Func<CancellationToken, Task> operation, CancellationToken ct) =>
            await RunLockedAsync<bool>(async token =>
            {
                await operation(token);
                return true;
            }, ct);

        public Task<VirtualDisplayOperationResult> StartExtendedDisplayAsync(
            VirtualDisplayMode requestedMode,
            CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in requestedMode))
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.InvalidMode, "Modo de pantalla fuera de los límites permitidos."));

            return RunLockedAsync(token =>
            {
                if (IsSessionActive())
                    return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.DisplayBusy, "Ya existe un monitor virtual extendido activo."));

                return StartExtendedDisplayCoreAsync(requestedMode, token);
            }, ct);
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
                return VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.DriverNotInstalled, "No se encontró un controlador de pantalla virtual instalado.");
            }

            _activeBackend = backend;
            var baseSnapshot = _topologyManager.CaptureSnapshot();

            TransitionState(VirtualDisplayState.CreatingDisplay, $"Creando monitor virtual con {backend.Capabilities.BackendId}");
            var instance = await TryCreateVirtualMonitorAsync(backend, requestedMode, ct);
            if (instance == null)
                return await AbortStartAsync(VirtualDisplayErrorCode.CreateFailed, "No se pudo inicializar el monitor virtual.", baseSnapshot, ct);

            var setup = new SessionSetupContext(backend, instance, baseSnapshot);
            bool configured = await PersistAndConfigureDisplayAsync(setup, ct);
            if (!configured)
                return VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.TopologyFailed, "No se pudo aplicar la topología extendida en Windows.");

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

        private async Task<bool> PersistAndConfigureDisplayAsync(SessionSetupContext setup, CancellationToken ct)
        {
            _activeInstance = setup.Instance;
            _savedSnapshot = setup.BaseSnapshot with
            {
                BackendId = setup.Backend.Capabilities.BackendId,
                VirtualMonitorId = setup.Instance.MonitorId,
                VirtualDeviceName = setup.Instance.DeviceName
            };
            await _savedSnapshot.SaveToFileAsync(_options.SnapshotFilePath, ct);

            _watchdogHandle = _watchdogLauncher?.StartWatchdog(new VirtualDisplayWatchdogLaunchOptions
            {
                ParentPid = Environment.ProcessId,
                SnapshotPath = _options.SnapshotFilePath,
                MonitorId = setup.Instance.MonitorId,
                BackendId = setup.Backend.Capabilities.BackendId
            });

            TransitionState(VirtualDisplayState.ConfiguringTopology, "Aplicando topología Extendida (CCD)");
            if (_topologyManager.ApplyExtendTopology(setup.Instance.DeviceName, setup.Instance.Mode))
                return true;

            await TeardownActiveResourcesAsync(_savedSnapshot, ct);
            TransitionState(VirtualDisplayState.Inactive, "No se pudo aplicar la topología extendida en Windows.");
            return false;
        }

        private static async Task<VirtualDisplayInstance?> TryCreateVirtualMonitorAsync(
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
            VirtualDisplayErrorCode errorCode,
            string message,
            DisplayTopologySnapshot rollbackSnapshot,
            CancellationToken ct)
        {
            await TeardownActiveResourcesAsync(rollbackSnapshot, ct);
            TransitionState(VirtualDisplayState.Inactive, message);
            return VirtualDisplayOperationResult.Fail(errorCode, message);
        }

        public Task<VirtualDisplayOperationResult> NotifyStreamConnectedAsync(CancellationToken ct = default) =>
            RunLockedAsync(_ =>
            {
                if (CurrentSession == null)
                    return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.NoSession, "No existe una sesión de pantalla extendida."));

                ResetCts(ref _reconnectCts);
                TransitionState(VirtualDisplayState.Streaming, "Receptor conectado al monitor virtual");
                return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
            }, ct);

        public Task<VirtualDisplayOperationResult> ChangeDisplayModeAsync(
            VirtualDisplayMode newMode,
            CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in newMode))
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.InvalidMode, "Modo solicitado fuera de rango."));

            return RunLockedAsync(token => ChangeDisplayModeLockedAsync(newMode, token), ct);
        }

        private async Task<VirtualDisplayOperationResult> ChangeDisplayModeLockedAsync(
            VirtualDisplayMode newMode,
            CancellationToken ct)
        {
            if (!HasActiveSessionContext())
                return VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.NoSession, "No hay monitor virtual activo.");

            var backend = _activeBackend!;
            var instance = _activeInstance!;
            if (backend.Capabilities.SupportsDynamicModeChange && !await backend.UpdateModeAsync(instance.MonitorId, newMode, ct))
                return VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.ModeUpdateFailed, "El controlador rechazó el cambio de modo.");

            if (!_topologyManager.ApplyExtendTopology(instance.DeviceName, newMode))
                return VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.TopologyFailed, "No se pudo aplicar el nuevo modo en CCD.");

            _activeInstance = instance with { Mode = newMode };
            CurrentSession = CurrentSession! with { ActiveMode = newMode };
            return VirtualDisplayOperationResult.Ok(CurrentSession);
        }

        private bool HasActiveSessionContext()
        {
            if (_activeBackend == null || _activeInstance == null)
                return false;

            return CurrentSession != null;
        }

        public Task HandleNetworkDisconnectedAsync(CancellationToken ct = default) =>
            RunLockedAsync(_ =>
            {
                if (State == VirtualDisplayState.Streaming)
                {
                    TransitionState(VirtualDisplayState.Reconnecting, "Pérdida temporal de red; conservando monitor virtual");
                    StartReconnectTimer();
                }
                return Task.CompletedTask;
            }, ct);

        public Task HandleNetworkReconnectedAsync(CancellationToken ct = default) =>
            RunLockedAsync(_ =>
            {
                if (State == VirtualDisplayState.Reconnecting)
                {
                    ResetCts(ref _reconnectCts);
                    TransitionState(VirtualDisplayState.Streaming, "Conectividad recuperada");
                }
                return Task.CompletedTask;
            }, ct);

        public Task HandleSystemSuspendAsync(CancellationToken ct = default) =>
            RunLockedAsync(_ =>
            {
                bool canSuspend = State == VirtualDisplayState.Streaming || State == VirtualDisplayState.Reconnecting;
                if (canSuspend)
                {
                    ResetCts(ref _reconnectCts);
                    TransitionState(VirtualDisplayState.Suspended, "Sistema en suspensión");
                }
                return Task.CompletedTask;
            }, ct);

        public Task HandleSystemResumeAsync(CancellationToken ct = default) =>
            RunLockedAsync(async token =>
            {
                if (State != VirtualDisplayState.Suspended || _activeInstance == null)
                    return;

                TransitionState(VirtualDisplayState.SuspendRecovery, "Reanudando pantalla virtual tras suspensión");
                if (_topologyManager.ApplyExtendTopology(_activeInstance.DeviceName, _activeInstance.Mode))
                {
                    TransitionState(VirtualDisplayState.Streaming, "Monitor virtual reanudado");
                    return;
                }

                await TeardownActiveResourcesAsync(_savedSnapshot, token);
                TransitionState(VirtualDisplayState.Inactive, "Fallo irrecuperable tras suspensión");
            }, ct);

        public Task StopExtendedDisplayAsync(CancellationToken ct = default) =>
            RunLockedAsync(async token =>
            {
                if (State == VirtualDisplayState.Inactive && _activeInstance == null)
                    return;

                TransitionState(VirtualDisplayState.Destroying, "Desmontando monitor virtual y restaurando topología");
                await TeardownActiveResourcesAsync(_savedSnapshot, token);
                TransitionState(VirtualDisplayState.Inactive, "Pantalla extendida finalizada");
            }, ct);

        public Task RecoverOrphanedSessionOnStartupAsync(CancellationToken ct = default) =>
            RunLockedAsync(async token =>
            {
                var orphan = await DisplayTopologySnapshot.TryLoadFromFileAsync(_options.SnapshotFilePath, token);
                if (orphan == null)
                    return;

                await DestroyOrphanBackendMonitorAsync(orphan, token);
                _topologyManager.RestoreSnapshot(orphan);
                DisplayTopologySnapshot.DeleteFileIfExists(_options.SnapshotFilePath);
            }, ct);

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
            ResetCts(ref _reconnectCts);
            ResetCts(ref _heartbeatCts);

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
            var interval = _options.HeartbeatInterval > TimeSpan.Zero
                ? _options.HeartbeatInterval
                : TimeSpan.FromMilliseconds(200);

            var token = ReplaceCts(ref _heartbeatCts);
            _ = Task.Run(() => RunHeartbeatAsync(backend, monitorId, interval, token), CancellationToken.None);
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

        private void StartReconnectTimer()
        {
            var timeout = _options.ReconnectTimeout > TimeSpan.Zero
                ? _options.ReconnectTimeout
                : TimeSpan.FromSeconds(15);

            var token = ReplaceCts(ref _reconnectCts);
            _ = Task.Run(() => RunReconnectExpiryAsync(timeout, token), CancellationToken.None);
        }

        private async Task RunReconnectExpiryAsync(TimeSpan timeout, CancellationToken token)
        {
            try
            {
                await Task.Delay(timeout, token);
                await RunLockedAsync(async innerToken =>
                {
                    if (State != VirtualDisplayState.Reconnecting)
                        return;

                    TransitionState(VirtualDisplayState.Destroying, "Timeout de reconexión agotado");
                    await TeardownActiveResourcesAsync(_savedSnapshot, innerToken);
                    TransitionState(VirtualDisplayState.Inactive, "Monitor virtual destruido por timeout");
                }, token);
            }
            catch (OperationCanceledException)
            {
                // Cancelled when reconnected or stopped
            }
        }

        private static CancellationToken ReplaceCts(ref CancellationTokenSource? field)
        {
            ResetCts(ref field);
            var cts = new CancellationTokenSource();
            field = cts;
            return cts.Token;
        }

        private static void ResetCts(ref CancellationTokenSource? field)
        {
            field?.Cancel();
            field?.Dispose();
            field = null;
        }

        private void TransitionState(VirtualDisplayState nextState, string? reason)
        {
            var previous = State;
            State = nextState;
            StateChanged?.Invoke(this, new VirtualDisplayStateChangedEventArgs
            {
                PreviousState = previous,
                CurrentState = nextState,
                Reason = reason
            });
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
