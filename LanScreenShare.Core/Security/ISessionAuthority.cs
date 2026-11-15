using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public sealed record SessionPermissionsChangedEventArgs(
        string SessionId,
        string PeerFingerprint,
        EffectiveSessionPermissions Permissions);

    public interface ISessionAuthority
    {
        string? ActiveControllerSessionId { get; }
        event EventHandler<string>? SessionTerminated;
        event EventHandler<SessionPermissionsChangedEventArgs>? SessionPermissionsChanged;

        ActiveSession CreateSession(DeviceFingerprint peerFingerprint, DeviceName peerName, TimeSpan? ttl = null);
        Task<(bool Success, ActiveSession? Session, string? FailureReason)> TryCreateSessionAsync(DeviceFingerprint peerFingerprint, DeviceName peerName = default, TimeSpan? ttl = null);
        ActiveSession? GetSession(SessionId sessionId);
        IReadOnlyList<ActiveSession> GetActiveSessions();

        ChannelAttachmentToken IssueChannelToken(SessionId sessionId, ChannelType channel, TimeSpan? validFor = null);
        AttachmentStatus TryAttachChannel(ChannelAttachmentRequest request);
        AttachmentStatus TryAttachChannel(string tokenId, SessionId sessionId, DeviceFingerprint peerFingerprint, ChannelType channel);
        bool TryAttachChannel(ChannelAttachmentRequest request, out string? failureReason);

        RemoteControlState RequestRemoteControl(SessionId sessionId);
        bool TryAcquireControl(SessionId sessionId, out string? failureReason);
        void ReleaseRemoteControl(SessionId sessionId);
        void RevokeControl(SessionId sessionId);
        string? GetActiveControllerSessionId();

        void CloseSession(SessionId sessionId);
        void TerminateSession(SessionId sessionId);
        void TerminateSessionsForDevice(DeviceFingerprint fingerprint);
        void TerminateAllSessions();
        void UpdateDevicePermissions(DeviceFingerprint fingerprint, DevicePermissions permissions);
        void PruneExpired();

        bool IsPairingBlocked(string ipAddress);
        void RecordPairingAttempt(string ipAddress, bool success);
    }
}
