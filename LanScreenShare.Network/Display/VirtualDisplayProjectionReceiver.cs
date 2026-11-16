using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Network.Display;

public sealed record VirtualDisplayProjectionReceiverOptions
{
    public SslServerAuthenticationOptions? SignalingServerSslOptions { get; init; }
    public Func<DisplayCapabilitiesDto>? CapabilitiesProvider { get; init; }
    public Func<DisplayInvitationAcceptanceContext, Task<bool>>? ApprovalHandler { get; init; }
    public required TcpStreamClient StreamClient { get; init; }
    public TcpControlClient? ControlClient { get; init; }
}

/// <summary>
/// Orquesta el flujo del Equipo B (Monitor Inalámbrico) para Entrega 2:
/// expone <see cref="DisplaySignalingServer"/> en 5005, aplica regla 1:1 (<c>ERROR_DISPLAY_BUSY</c>)
/// y conecta <see cref="TcpStreamClient"/> hacia el puerto 5001 del Equipo A presentando <see cref="ProjectionInvitationToken"/>.
/// </summary>
public sealed class VirtualDisplayProjectionReceiver : IAsyncDisposable
{
    private readonly VirtualDisplayProjectionReceiverOptions _options;
    private readonly DisplaySignalingServer _signalingServer;
    private int _isBusy;
    private bool _disposed;

    public bool IsListening => _signalingServer.IsRunning;
    public bool IsSessionBusy => Volatile.Read(ref _isBusy) == 1 || _options.StreamClient.IsConnected;
    public DisplayInvitationAcceptanceContext? LastAcceptedInvitation { get; private set; }

    public event EventHandler<DisplayInvitationAcceptanceContext>? InvitationAccepted;

    public VirtualDisplayProjectionReceiver(VirtualDisplayProjectionReceiverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _signalingServer = new DisplaySignalingServer(new DisplaySignalingServerOptions
        {
            SslServerOptions = options.SignalingServerSslOptions,
            CapabilitiesProvider = options.CapabilitiesProvider,
            IsBusyProvider = () => IsSessionBusy,
            InvitationHandler = HandleInvitationAsync
        });
    }

    public Task StartListeningAsync(int signalingPort = DiscoveryService.DefaultDisplaySignalingPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _signalingServer.StartAsync(signalingPort, ct);
    }

    private async Task<bool> HandleInvitationAsync(DisplayInvitationAcceptanceContext context)
    {
        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
            return false;

        bool approved = true;
        if (_options.ApprovalHandler != null)
        {
            try
            {
                approved = await _options.ApprovalHandler(context);
            }
            catch
            {
                approved = false;
            }
        }

        if (!approved)
        {
            Volatile.Write(ref _isBusy, 0);
            return false;
        }

        LastAcceptedInvitation = context;
        InvitationAccepted?.Invoke(this, context);
        return true;
    }

    public async Task ConnectToHostAsync(
        DisplayInvitationAcceptanceContext invitation,
        SslClientAuthenticationOptions? clientSslOptions,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(invitation);

        Volatile.Write(ref _isBusy, 1);
        try
        {
            string hostIp = string.IsNullOrWhiteSpace(invitation.SenderIpAddress)
                ? "127.0.0.1"
                : invitation.SenderIpAddress;

            await ConnectStreamWithRetryAsync(new StreamClientConnectionOptions(
                hostIp,
                invitation.HostStreamPort,
                clientSslOptions,
                invitation.InvitationToken), ct);

            await ConnectControlChannelIfAvailableAsync(hostIp, invitation, clientSslOptions, ct);
        }
        catch
        {
            Volatile.Write(ref _isBusy, 0);
            throw;
        }
    }

    private async Task ConnectStreamWithRetryAsync(StreamClientConnectionOptions options, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                await _options.StreamClient.ConnectAsync(options, ct);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(75, ct);
            }
        }
    }

    private async Task ConnectControlChannelIfAvailableAsync(
        string hostIp,
        DisplayInvitationAcceptanceContext invitation,
        SslClientAuthenticationOptions? clientSslOptions,
        CancellationToken ct)
    {
        if (_options.ControlClient == null || invitation.HostControlPort <= 0)
            return;

        var handshake = _options.StreamClient.ActiveSessionHandshake;
        await _options.ControlClient.ConnectAsync(new ControlClientConnectionOptions(
            hostIp,
            invitation.HostControlPort,
            clientSslOptions,
            handshake?.SessionId,
            handshake?.ControlToken), ct);
    }

    public Task RequestDisplayModeChangeAsync(VirtualDisplayMode requestedMode)
    {
        if (_options.ControlClient == null)
            throw new InvalidOperationException("No hay canal de control conectado para solicitar cambio de modo.");

        return _options.ControlClient.SendDisplayControlMessageAsync(
            DisplaySessionControlMessage.CreateModeChangeRequest(requestedMode));
    }

    public async Task DisconnectSessionAsync()
    {
        if (_options.ControlClient != null)
            await _options.ControlClient.DisconnectAsync();

        await _options.StreamClient.DisconnectAsync();
        LastAcceptedInvitation = null;
        Volatile.Write(ref _isBusy, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _signalingServer.Dispose();
        await DisconnectSessionAsync();
    }
}
