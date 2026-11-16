using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Network.Security;

public sealed class DisplaySignalingMessage
{
    public const int MaxPayloadBytes = 16 * 1024;

    public const string TypeCapabilitiesQuery = "CAPABILITIES_QUERY";
    public const string TypeCapabilities = "CAPABILITIES";
    public const string TypeProjectionInvite = "PROJECTION_INVITE";
    public const string TypeInviteAccepted = "INVITE_ACCEPTED";
    public const string TypeInviteRejected = "INVITE_REJECTED";
    public const string TypeError = "ERROR";

    public const string ErrorDisplayBusy = "ERROR_DISPLAY_BUSY";
    public const string ErrorUnauthorized = "ERROR_UNAUTHORIZED";
    public const string ErrorInvalidMode = "ERROR_INVALID_MODE";
    public const string ErrorRejected = "ERROR_REJECTED";

    public string Type { get; set; } = TypeCapabilitiesQuery;
    public string SessionId { get; set; } = string.Empty;
    public string InvitationToken { get; set; } = string.Empty;
    public VirtualDisplayMode? SelectedMode { get; set; }
    public DisplayCapabilitiesDto? Capabilities { get; set; }
    public int HostStreamPort { get; set; } = 5001;
    public int HostControlPort { get; set; } = 5003;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public static Task WriteAsync(Stream stream, DisplaySignalingMessage message, CancellationToken ct) =>
        StreamConnectionHelper.WriteJsonFrameAsync(stream, message, MaxPayloadBytes, ct);

    public static Task<DisplaySignalingMessage> ReadAsync(Stream stream, CancellationToken ct) =>
        StreamConnectionHelper.ReadJsonFrameAsync<DisplaySignalingMessage>(stream, MaxPayloadBytes, ct);
}
