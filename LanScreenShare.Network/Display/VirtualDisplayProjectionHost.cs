using System;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Network.Display;

public sealed record VirtualDisplayProjectionHostDependencies
{
    public required IVirtualDisplayService DisplayService { get; init; }
    public required IProjectionInvitationStore InvitationStore { get; init; }
    public required TcpStreamServer StreamServer { get; init; }
    public required Func<CaptureSource, IScreenCaptureService> CaptureFactory { get; init; }
    public TcpControlServer? ControlServer { get; init; }
    public IDisplayTopologyManager? TopologyManager { get; init; }
    public IDisplaySignalingClient? SignalingClient { get; init; }
}

public sealed record ProjectionStartResult
{
    public bool Success { get; init; }
    public VirtualDisplaySessionInfo? DisplaySession { get; init; }
    public ProjectionInvitationToken? InvitationToken { get; init; }
    public CaptureSource? CaptureSource { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;

    public static ProjectionStartResult Fail(string errorCode, string errorMessage) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

/// <summary>
/// Orquesta el flujo del Equipo A (Emisor) para Entrega 2:
/// negociación TLS con <see cref="DisplayCapabilitiesDto"/>, emisión de <see cref="ProjectionInvitationToken"/>,
/// creación del monitor virtual y captura DXGI/aislada sobre <see cref="TcpStreamServer"/> (5001).
/// </summary>
public sealed class VirtualDisplayProjectionHost : IAsyncDisposable
{
    private readonly VirtualDisplayProjectionHostDependencies _deps;
    private readonly IDisplaySignalingClient _signalingClient;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private IScreenCaptureService? _captureService;
    private CaptureProfile _baseProfile = new();
    private bool _isProjecting;
    private bool _disposed;

    public bool IsProjecting => _isProjecting;
    public CaptureSource? ActiveCaptureSource { get; private set; }
    public VirtualDisplaySessionInfo? ActiveDisplaySession => _deps.DisplayService.CurrentSession;

    public VirtualDisplayProjectionHost(VirtualDisplayProjectionHostDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _deps = dependencies;
        _signalingClient = dependencies.SignalingClient ?? new DisplaySignalingClient();
    }

    public async Task<ProjectionStartResult> StartProjectionAsync(
        DisplayNegotiationRequest negotiationRequest,
        CaptureProfile? captureProfile = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(negotiationRequest);

        await _stateLock.WaitAsync(ct);
        try
        {
            if (_isProjecting)
                return ProjectionStartResult.Fail(DisplaySignalingMessage.ErrorDisplayBusy, "Ya existe una proyección de pantalla inalámbrica activa.");

            var negotiation = await _signalingClient.NegotiateAndInviteAsync(negotiationRequest, ct);
            if (!negotiation.Accepted || negotiation.InvitationToken == null)
            {
                return ProjectionStartResult.Fail(negotiation.ErrorCode, negotiation.ErrorMessage);
            }

            return await ActivateVirtualDisplayAndCaptureAsync(negotiation.InvitationToken, captureProfile ?? new CaptureProfile(), ct);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<ProjectionStartResult> ActivateVirtualDisplayAndCaptureAsync(
        ProjectionInvitationToken invitation,
        CaptureProfile profile,
        CancellationToken ct)
    {
        await ConfigureServersAsync(invitation, ct);

        var displayResult = await _deps.DisplayService.StartExtendedDisplayAsync(invitation.NegotiatedMode, ct);
        if (!displayResult.Success || displayResult.Session == null)
        {
            await CleanupProjectionResourcesAsync(CancellationToken.None);
            return ProjectionStartResult.Fail(
                displayResult.ErrorCode.ToString(),
                displayResult.Message ?? "No se pudo inicializar el monitor virtual.");
        }

        _baseProfile = profile;
        StartIsolatedCapture(displayResult.Session, profile);
        _deps.DisplayService.StateChanged += OnDisplayServiceStateChanged;
        _isProjecting = true;

        return new ProjectionStartResult
        {
            Success = true,
            DisplaySession = displayResult.Session,
            InvitationToken = invitation,
            CaptureSource = ActiveCaptureSource
        };
    }

    private async Task ConfigureServersAsync(ProjectionInvitationToken invitation, CancellationToken ct)
    {
        _deps.StreamServer.ProjectionInvitationStore = _deps.InvitationStore;
        _deps.StreamServer.RequireProjectionInvitation = true;
        _deps.StreamServer.ClientConnected += OnStreamClientConnected;
        _deps.StreamServer.ClientDisconnected += OnStreamClientDisconnected;
        await _deps.StreamServer.StartAsync(invitation.HostStreamPort, ct);

        if (_deps.ControlServer != null)
        {
            _deps.ControlServer.AllowDisplayControlMessages = true;
            _deps.ControlServer.DisplayControlReceived += OnControlMessageReceived;
            await _deps.ControlServer.StartAsync(invitation.HostControlPort, ct);
        }
    }

    private void StartIsolatedCapture(VirtualDisplaySessionInfo session, CaptureProfile profile)
    {
        StopAndDisposeCapture();

        var topology = _deps.TopologyManager?.CaptureSnapshot();
        var captureSource = VirtualDisplayCaptureSourceFactory.CreateCaptureSource(session, topology);
        ActiveCaptureSource = captureSource;

        var effectiveProfile = profile with
        {
            TargetFps = Math.Clamp(session.ActiveMode.RefreshRateHz, 30, 120),
            ResolutionScale = 1.0,
            TargetWidth = session.ActiveMode.Width,
            TargetHeight = session.ActiveMode.Height
        };

        var capture = _deps.CaptureFactory(captureSource);
        capture.FrameCaptured += OnFrameCaptured;
        _captureService = capture;
        capture.StartCapture(effectiveProfile);
    }

    private void OnFrameCaptured(object? sender, CapturedFrame frame)
    {
        if (_isProjecting && _deps.StreamServer.IsRunning)
        {
            _ = _deps.StreamServer.BroadcastFrameAsync(frame);
        }
    }

    private void OnStreamClientConnected(object? sender, string clientId)
    {
        _captureService?.RequestKeyframe();
        if (_deps.DisplayService.State == VirtualDisplayState.Reconnecting)
        {
            _ = _deps.DisplayService.HandleNetworkReconnectedAsync();
            return;
        }

        _ = _deps.DisplayService.NotifyStreamConnectedAsync();
    }

    private void OnStreamClientDisconnected(object? sender, string clientId)
    {
        if (_isProjecting && _deps.StreamServer.ConnectedClientsCount == 0)
        {
            _ = _deps.DisplayService.HandleNetworkDisconnectedAsync();
        }
    }

    private void OnDisplayServiceStateChanged(object? sender, VirtualDisplayStateChangedEventArgs e)
    {
        if (_isProjecting && e.CurrentState is VirtualDisplayState.Inactive or VirtualDisplayState.Error)
        {
            _ = StopProjectionAsync();
        }
    }

    private void OnControlMessageReceived(object? sender, DisplayControlReceivedEventArgs e)
    {
        if (e.Message.Kind == DisplayControlMessageKind.ModeChangeRequest)
        {
            _ = HandleModeChangeRequestAsync(e.Message.ToDisplayMode());
        }
    }

    private async Task HandleModeChangeRequestAsync(VirtualDisplayMode requestedMode)
    {
        await _stateLock.WaitAsync();
        try
        {
            await ApplyModeChangeLockedAsync(requestedMode);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task ApplyModeChangeLockedAsync(VirtualDisplayMode requestedMode)
    {
        if (!_isProjecting || _deps.ControlServer == null)
            return;

        if (!VirtualDisplayModeValidator.IsSupported(in requestedMode))
        {
            await _deps.ControlServer.BroadcastDisplayControlMessageAsync(
                DisplaySessionControlMessage.CreateModeChangeRejected("Modo solicitado fuera de los límites seguros."));
            return;
        }

        var changeResult = await _deps.DisplayService.ChangeDisplayModeAsync(requestedMode);
        if (!changeResult.Success || changeResult.Session == null)
        {
            await _deps.ControlServer.BroadcastDisplayControlMessageAsync(
                DisplaySessionControlMessage.CreateModeChangeRejected(changeResult.Message ?? "Cambio de modo rechazado."));
            return;
        }

        StartIsolatedCapture(changeResult.Session, _baseProfile);
        _captureService?.RequestKeyframe();

        await _deps.ControlServer.BroadcastDisplayControlMessageAsync(
            DisplaySessionControlMessage.CreateModeChangeAccepted(requestedMode));
        await _deps.ControlServer.BroadcastDisplayControlMessageAsync(
            DisplaySessionControlMessage.CreateModeChanged(changeResult.Session.ActiveMode));
    }

    public async Task StopProjectionAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            await CleanupProjectionResourcesAsync(ct);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task CleanupProjectionResourcesAsync(CancellationToken ct)
    {
        _isProjecting = false;
        _deps.DisplayService.StateChanged -= OnDisplayServiceStateChanged;

        StopAndDisposeCapture();
        ActiveCaptureSource = null;

        _deps.StreamServer.ClientConnected -= OnStreamClientConnected;
        _deps.StreamServer.ClientDisconnected -= OnStreamClientDisconnected;
        _deps.StreamServer.RequireProjectionInvitation = false;
        _deps.StreamServer.Stop();

        if (_deps.ControlServer != null)
        {
            _deps.ControlServer.DisplayControlReceived -= OnControlMessageReceived;
            _deps.ControlServer.AllowDisplayControlMessages = false;
            _deps.ControlServer.Stop();
        }

        await _deps.DisplayService.StopExtendedDisplayAsync(ct);
    }

    private void StopAndDisposeCapture()
    {
        if (_captureService == null)
            return;

        _captureService.FrameCaptured -= OnFrameCaptured;
        try { _captureService.StopCapture(); } catch { /* Ignore capture stop error */ }
        try { _captureService.Dispose(); } catch { /* Ignore capture dispose error */ }
        _captureService = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopProjectionAsync();
        _stateLock.Dispose();
    }
}
