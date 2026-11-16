using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Network.Security;

public sealed record DisplayNegotiationRequest
{
    public required string TargetIp { get; init; }
    public int TargetPort { get; init; } = DiscoveryService.DefaultDisplaySignalingPort;
    public SslClientAuthenticationOptions? SslOptions { get; init; }
    public required IProjectionInvitationStore InvitationStore { get; init; }
    public VirtualDisplayMode? PreferredMode { get; init; }
    public int HostStreamPort { get; init; } = 5001;
    public int HostControlPort { get; init; } = 5003;
    public string? FallbackPeerFingerprint { get; init; }
}

public sealed record DisplayNegotiationResult
{
    public bool Accepted { get; init; }
    public DisplayCapabilitiesDto? Capabilities { get; init; }
    public ProjectionInvitationToken? InvitationToken { get; init; }
    public string? PeerFingerprint { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;

    public static DisplayNegotiationResult Fail(string errorCode, string errorMessage) => new()
    {
        Accepted = false,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage
    };
}

public interface IDisplaySignalingClient
{
    Task<DisplayNegotiationResult> NegotiateAndInviteAsync(DisplayNegotiationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Cliente TLS de señalización en el Equipo A (Emisor).
/// Se conecta al endpoint del Equipo B, autentica sus capacidades e invoca una proyección con token de un solo uso.
/// </summary>
public sealed class DisplaySignalingClient : IDisplaySignalingClient
{
    public async Task<DisplayNegotiationResult> NegotiateAndInviteAsync(
        DisplayNegotiationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TargetIp))
            return DisplayNegotiationResult.Fail(DisplaySignalingMessage.ErrorRejected, "La dirección IP del receptor está vacía.");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(request.TargetIp, request.TargetPort, linkedCts.Token);

        (Stream stream, string peerFingerprint) = await OpenSignalingStreamAsync(client, request, linkedCts.Token);
        await using (stream)
        {
            return await ExecuteNegotiationExchangeAsync(stream, peerFingerprint, request, linkedCts.Token);
        }
    }

    private static async Task<(Stream Stream, string PeerFingerprint)> OpenSignalingStreamAsync(
        TcpClient client,
        DisplayNegotiationRequest request,
        CancellationToken ct)
    {
        Stream rawStream = client.GetStream();
        if (request.SslOptions == null)
        {
            return (rawStream, request.FallbackPeerFingerprint ?? string.Empty);
        }

        var sslStream = new SslStream(rawStream, false);
        await sslStream.AuthenticateAsClientAsync(request.SslOptions, ct);

        if (sslStream.RemoteCertificate == null)
            throw new InvalidOperationException("El receptor de pantalla inalámbrica no presentó certificado TLS.");

        using var remoteCert = new X509Certificate2(sslStream.RemoteCertificate);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(remoteCert);
        return (sslStream, fingerprint);
    }

    private static async Task<DisplayNegotiationResult> ExecuteNegotiationExchangeAsync(
        Stream stream,
        string peerFingerprint,
        DisplayNegotiationRequest request,
        CancellationToken ct)
    {
        var capabilitiesResult = await QueryCapabilitiesAsync(stream, request.PreferredMode, ct);
        if (!capabilitiesResult.Accepted)
            return capabilitiesResult.Failure!;

        var selectedMode = capabilitiesResult.SelectedMode;
        if (string.IsNullOrWhiteSpace(peerFingerprint))
            return DisplayNegotiationResult.Fail(DisplaySignalingMessage.ErrorUnauthorized, "No se pudo verificar la identidad del receptor.");

        var invitation = request.InvitationStore.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = peerFingerprint,
            NegotiatedMode = selectedMode,
            HostStreamPort = request.HostStreamPort,
            HostControlPort = request.HostControlPort
        });

        return await SendInvitationAsync(
            stream,
            new InvitationSendContext(peerFingerprint, capabilitiesResult.Capabilities!, invitation),
            ct);
    }

    private readonly record struct CapabilitiesQueryOutcome(
        bool Accepted,
        DisplayCapabilitiesDto? Capabilities = null,
        VirtualDisplayMode SelectedMode = default,
        DisplayNegotiationResult? Failure = null);

    private static async Task<CapabilitiesQueryOutcome> QueryCapabilitiesAsync(
        Stream stream,
        VirtualDisplayMode? preferredMode,
        CancellationToken ct)
    {
        await DisplaySignalingMessage.WriteAsync(stream, new DisplaySignalingMessage
        {
            Type = DisplaySignalingMessage.TypeCapabilitiesQuery
        }, ct);

        var response = await DisplaySignalingMessage.ReadAsync(stream, ct);
        if (response.Type != DisplaySignalingMessage.TypeCapabilities || response.Capabilities == null)
        {
            return new CapabilitiesQueryOutcome(
                false,
                Failure: DisplayNegotiationResult.Fail(
                    string.IsNullOrEmpty(response.ErrorCode) ? DisplaySignalingMessage.ErrorRejected : response.ErrorCode,
                    string.IsNullOrEmpty(response.ErrorMessage) ? "Respuesta de capacidades inválida." : response.ErrorMessage));
        }

        if (response.Capabilities.IsBusy)
        {
            return new CapabilitiesQueryOutcome(
                false,
                Failure: DisplayNegotiationResult.Fail(DisplaySignalingMessage.ErrorDisplayBusy, "El receptor ya está ocupado en otra sesión."));
        }

        var selectedMode = VirtualDisplayModeValidator.SelectBestSupportedMode(response.Capabilities.SupportedModes, preferredMode);
        if (selectedMode == null)
        {
            return new CapabilitiesQueryOutcome(
                false,
                Failure: DisplayNegotiationResult.Fail(DisplaySignalingMessage.ErrorInvalidMode, "Ningún modo anunciado por el receptor cumple los límites seguros."));
        }

        return new CapabilitiesQueryOutcome(true, response.Capabilities, selectedMode.Value);
    }

    private sealed record InvitationSendContext(
        string PeerFingerprint,
        DisplayCapabilitiesDto Capabilities,
        ProjectionInvitationToken Invitation);

    private static async Task<DisplayNegotiationResult> SendInvitationAsync(
        Stream stream,
        InvitationSendContext ctx,
        CancellationToken ct)
    {
        await DisplaySignalingMessage.WriteAsync(stream, new DisplaySignalingMessage
        {
            Type = DisplaySignalingMessage.TypeProjectionInvite,
            SessionId = ctx.Invitation.SessionId,
            InvitationToken = ctx.Invitation.TokenId,
            SelectedMode = ctx.Invitation.NegotiatedMode,
            HostStreamPort = ctx.Invitation.HostStreamPort,
            HostControlPort = ctx.Invitation.HostControlPort
        }, ct);

        var reply = await DisplaySignalingMessage.ReadAsync(stream, ct);
        if (reply.Type == DisplaySignalingMessage.TypeInviteAccepted)
        {
            return new DisplayNegotiationResult
            {
                Accepted = true,
                Capabilities = ctx.Capabilities,
                InvitationToken = ctx.Invitation,
                PeerFingerprint = ctx.PeerFingerprint
            };
        }

        return DisplayNegotiationResult.Fail(
            string.IsNullOrEmpty(reply.ErrorCode) ? DisplaySignalingMessage.ErrorRejected : reply.ErrorCode,
            string.IsNullOrEmpty(reply.ErrorMessage) ? "Invitación rechazada." : reply.ErrorMessage);
    }
}
