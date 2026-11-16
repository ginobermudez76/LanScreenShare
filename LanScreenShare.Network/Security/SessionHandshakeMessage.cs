using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Network.Security;

/// <summary>
/// Mensaje de señalización e intercambio de tokens de sesión sobre el canal primario (5001).
/// Especificación: ADR-001 Sección 4 y Sección 11 (máximo 16 KB por trama JSON).
/// </summary>
public sealed class SessionHandshakeMessage
{
    public const int MaxSignalingPayloadBytes = 16 * 1024; // 16 KB

    public string Type { get; set; } = "SESSION_OK"; // "SESSION_OK", "PROVISIONAL_PAIRING", "PROJECTION_CLAIM", "ERROR"
    public string SessionId { get; set; } = string.Empty;
    public string AudioToken { get; set; } = string.Empty;
    public string ControlToken { get; set; } = string.Empty;
    public string FileTransferToken { get; set; } = string.Empty;
    public string ProjectionInvitationToken { get; set; } = string.Empty;
    public string HostFingerprint { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public bool AllowRemoteControl { get; set; } = true;
    public bool AllowSourceSwitch { get; set; } = true;
    public bool AllowFileTransfer { get; set; } = true;
    public bool AllowClipboardSync { get; set; } = true;

    public EffectiveSessionPermissions GetEffectivePermissions() => new()
    {
        AllowRemoteControl = AllowRemoteControl,
        AllowSourceSwitch = AllowSourceSwitch,
        AllowFileTransfer = AllowFileTransfer,
        AllowClipboardSync = AllowClipboardSync
    };

    public void ApplyEffectivePermissions(EffectiveSessionPermissions permissions)
    {
        AllowRemoteControl = permissions.AllowRemoteControl;
        AllowSourceSwitch = permissions.AllowSourceSwitch;
        AllowFileTransfer = permissions.AllowFileTransfer;
        AllowClipboardSync = permissions.AllowClipboardSync;
    }

    public static Task WriteAsync(Stream stream, SessionHandshakeMessage message, CancellationToken ct) =>
        StreamConnectionHelper.WriteJsonFrameAsync(stream, message, MaxSignalingPayloadBytes, ct);

    public static Task<SessionHandshakeMessage> ReadAsync(Stream stream, CancellationToken ct) =>
        StreamConnectionHelper.ReadJsonFrameAsync<SessionHandshakeMessage>(stream, MaxSignalingPayloadBytes, ct);
}

