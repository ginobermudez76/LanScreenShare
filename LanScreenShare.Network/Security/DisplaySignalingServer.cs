using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Network.Security;

public sealed record DisplayInvitationAcceptanceContext
{
    public required string PeerFingerprint { get; init; }
    public required string PeerDeviceName { get; init; }
    public required string SessionId { get; init; }
    public required string InvitationToken { get; init; }
    public VirtualDisplayMode SelectedMode { get; init; }
    public int HostStreamPort { get; init; } = 5001;
    public int HostControlPort { get; init; } = 5003;
    public string SenderIpAddress { get; init; } = string.Empty;
}

public sealed record DisplaySignalingServerOptions
{
    public SslServerAuthenticationOptions? SslServerOptions { get; init; }
    public Func<DisplayCapabilitiesDto>? CapabilitiesProvider { get; init; }
    public Func<bool>? IsBusyProvider { get; init; }
    public Func<DisplayInvitationAcceptanceContext, Task<bool>>? InvitationHandler { get; init; }
}

/// <summary>
/// Endpoint TLS de señalización en el Equipo B (Receptor de Pantalla Inalámbrica, puerto 5005 por defecto).
/// Expone <see cref="DisplayCapabilitiesDto"/> autenticado y recibe <see cref="ProjectionInvitationToken"/> del Equipo A.
/// </summary>
public sealed class DisplaySignalingServer : IDisposable
{
    private readonly DisplaySignalingServerOptions _options;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private SslServerAuthenticationOptions? _sslServerOptions;
    private bool _disposed;

    public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;

    public DisplaySignalingServer(DisplaySignalingServerOptions? options = null)
    {
        _options = options ?? new DisplaySignalingServerOptions();
        _sslServerOptions = _options.SslServerOptions;
    }

    public void UpdateSslServerOptions(SslServerAuthenticationOptions? sslServerOptions) =>
        _sslServerOptions = sslServerOptions;

    public Task StartAsync(int port = DiscoveryService.DefaultDisplaySignalingPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return Task.CompletedTask;

        (_cts, _listener) = StreamConnectionHelper.StartAcceptLoop(port, AcceptLoopAsync, ct);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DisplaySignalingServer] Error aceptando conexión: {ex.Message}");
            }
        }
    }

    private sealed record AuthenticatedPeer(string Fingerprint, string DeviceName, string IpAddress);

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        System.IO.Stream? stream = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            var negotiated = await NegotiateStreamAsync(client, linkedCts.Token);
            if (negotiated == null)
                return;

            stream = negotiated.Value.Stream;
            await ProcessSignalingMessagesAsync(stream, negotiated.Value.Peer, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout o cierre normal de señalización
        }
        catch (System.IO.EndOfStreamException)
        {
            // El par cerró el canal tras completar la negociación
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DisplaySignalingServer] Error atendiendo señalización: {ex.Message}");
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* Ignore stream disposal error */ }
            try { client.Close(); client.Dispose(); } catch { /* Ignore client disposal error */ }
        }
    }

    private async Task<(System.IO.Stream Stream, AuthenticatedPeer Peer)?> NegotiateStreamAsync(
        TcpClient client,
        CancellationToken ct)
    {
        string senderIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? string.Empty;
        System.IO.Stream rawStream = client.GetStream();

        if (_sslServerOptions == null)
        {
            return (rawStream, new AuthenticatedPeer(string.Empty, "RemoteSender", senderIp));
        }

        var sslStream = new SslStream(rawStream, false);
        await sslStream.AuthenticateAsServerAsync(_sslServerOptions, ct);

        if (sslStream.RemoteCertificate == null)
        {
            await sslStream.DisposeAsync();
            return null;
        }

        using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);
        string deviceName = peerCert.GetNameInfo(X509NameType.SimpleName, false) ?? "RemoteSender";
        return (sslStream, new AuthenticatedPeer(fingerprint, deviceName, senderIp));
    }

    private async Task ProcessSignalingMessagesAsync(
        System.IO.Stream stream,
        AuthenticatedPeer peer,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await DisplaySignalingMessage.ReadAsync(stream, ct);
            bool continueLoop = await DispatchMessageAsync(stream, peer, message, ct);
            if (!continueLoop)
                break;
        }
    }

    private async Task<bool> DispatchMessageAsync(
        System.IO.Stream stream,
        AuthenticatedPeer peer,
        DisplaySignalingMessage message,
        CancellationToken ct)
    {
        if (string.Equals(message.Type, DisplaySignalingMessage.TypeCapabilitiesQuery, StringComparison.Ordinal))
        {
            await SendCapabilitiesAsync(stream, ct);
            return true;
        }

        if (string.Equals(message.Type, DisplaySignalingMessage.TypeProjectionInvite, StringComparison.Ordinal))
        {
            await HandleProjectionInviteAsync(stream, peer, message, ct);
            return false;
        }

        await DisplaySignalingMessage.WriteAsync(stream, new DisplaySignalingMessage
        {
            Type = DisplaySignalingMessage.TypeError,
            ErrorCode = DisplaySignalingMessage.ErrorRejected,
            ErrorMessage = $"Tipo de mensaje desconocido: {message.Type}"
        }, ct);
        return false;
    }

    private async Task SendCapabilitiesAsync(System.IO.Stream stream, CancellationToken ct)
    {
        var caps = _options.CapabilitiesProvider?.Invoke() ?? new DisplayCapabilitiesDto();
        bool isBusy = caps.IsBusy || (_options.IsBusyProvider?.Invoke() ?? false);

        await DisplaySignalingMessage.WriteAsync(stream, new DisplaySignalingMessage
        {
            Type = DisplaySignalingMessage.TypeCapabilities,
            Capabilities = caps with { IsBusy = isBusy }
        }, ct);
    }

    private async Task HandleProjectionInviteAsync(
        System.IO.Stream stream,
        AuthenticatedPeer peer,
        DisplaySignalingMessage message,
        CancellationToken ct)
    {
        string? validationError = ValidateInvitePayload(message);
        if (validationError != null)
        {
            await WriteRejectionAsync(stream, DisplaySignalingMessage.ErrorInvalidMode, validationError, ct);
            return;
        }

        if (_options.IsBusyProvider?.Invoke() == true)
        {
            await WriteRejectionAsync(
                stream,
                DisplaySignalingMessage.ErrorDisplayBusy,
                "El receptor ya se encuentra en una sesión de pantalla extendida activa.",
                ct);
            return;
        }

        var context = new DisplayInvitationAcceptanceContext
        {
            PeerFingerprint = peer.Fingerprint,
            PeerDeviceName = peer.DeviceName,
            SessionId = message.SessionId,
            InvitationToken = message.InvitationToken,
            SelectedMode = message.SelectedMode!.Value,
            HostStreamPort = message.HostStreamPort,
            HostControlPort = message.HostControlPort,
            SenderIpAddress = peer.IpAddress
        };

        bool accepted = _options.InvitationHandler == null || await _options.InvitationHandler(context);
        if (!accepted)
        {
            await WriteRejectionAsync(
                stream,
                DisplaySignalingMessage.ErrorRejected,
                "Invitación rechazada por el receptor.",
                ct);
            return;
        }

        await DisplaySignalingMessage.WriteAsync(stream, new DisplaySignalingMessage
        {
            Type = DisplaySignalingMessage.TypeInviteAccepted,
            SessionId = message.SessionId
        }, ct);
    }

    private static string? ValidateInvitePayload(DisplaySignalingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId) || string.IsNullOrWhiteSpace(message.InvitationToken))
            return "Falta SessionId o InvitationToken en la invitación.";

        if (message.SelectedMode is not { } mode || !VirtualDisplayModeValidator.IsSupported(in mode))
            return "El modo de pantalla solicitado excede los límites seguros.";

        if (message.HostStreamPort <= 0 || message.HostStreamPort > 65535)
            return "Puerto de stream inválido.";

        return null;
    }

    private static Task WriteRejectionAsync(
        System.IO.Stream stream,
        string errorCode,
        string errorMessage,
        CancellationToken ct) =>
        DisplaySignalingMessage.WriteAsync(stream, new DisplaySignalingMessage
        {
            Type = DisplaySignalingMessage.TypeInviteRejected,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        }, ct);

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* Ignore listener stop error */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
