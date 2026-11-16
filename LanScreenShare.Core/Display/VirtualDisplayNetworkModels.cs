using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Display
{
    public sealed record DisplayCapabilitiesDto
    {
        public IReadOnlyList<VirtualDisplayMode> SupportedModes { get; init; } = new[] { VirtualDisplayMode.Default };
        public double DpiScaleFactor { get; init; } = 1.0;
        public int OrientationDegrees { get; init; } = 0;
        public string DeviceName { get; init; } = string.Empty;
        public bool SupportsAuxiliaryInput { get; init; } = true;
        public bool IsBusy { get; init; }
    }

    public sealed record ProjectionInvitationRequest
    {
        public required string TargetFingerprint { get; init; }
        public VirtualDisplayMode NegotiatedMode { get; init; } = VirtualDisplayMode.Default;
        public int HostStreamPort { get; init; } = 5001;
        public int HostControlPort { get; init; } = 5003;
        public TimeSpan? Ttl { get; init; }
    }

    public sealed record ProjectionInvitationToken
    {
        public const int TokenByteLength = 32; // 256 bits
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(15);

        public required string TokenId { get; init; }
        public required string SessionId { get; init; }
        public required string TargetFingerprint { get; init; }
        public VirtualDisplayMode NegotiatedMode { get; init; } = VirtualDisplayMode.Default;
        public int HostStreamPort { get; init; } = 5001;
        public int HostControlPort { get; init; } = 5003;
        public DateTime ExpiresAtUtc { get; init; }

        public bool IsExpired(DateTime utcNow) => utcNow > ExpiresAtUtc;
    }

    public readonly record struct ProjectionInvitationConsumptionResult(
        bool Accepted,
        ProjectionInvitationToken? Token = null,
        string? FailureReason = null);

    public interface IProjectionInvitationStore
    {
        ProjectionInvitationToken IssueInvitation(ProjectionInvitationRequest request);
        ProjectionInvitationConsumptionResult ConsumeInvitation(string tokenId, string presenterFingerprint);
        void PruneExpired();
    }

    public sealed class ProjectionInvitationStore : IProjectionInvitationStore
    {
        private readonly ConcurrentDictionary<string, ProjectionInvitationToken> _invitations = new(StringComparer.Ordinal);
        private readonly TimeProvider _timeProvider;

        public ProjectionInvitationStore(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public ProjectionInvitationToken IssueInvitation(ProjectionInvitationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.TargetFingerprint))
                throw new ArgumentException("Target fingerprint cannot be empty.", nameof(request));

            if (!VirtualDisplayModeValidator.IsSupported(request.NegotiatedMode))
                throw new ArgumentOutOfRangeException(nameof(request), "Negotiated display mode is outside allowed limits.");

            byte[] randomBytes = new byte[ProjectionInvitationToken.TokenByteLength];
            RandomNumberGenerator.Fill(randomBytes);
            string tokenId = Convert.ToHexString(randomBytes).ToLowerInvariant();

            var ttl = request.Ttl ?? ProjectionInvitationToken.DefaultTtl;
            if (ttl <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(request), "Invitation TTL must be positive.");

            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var invitation = new ProjectionInvitationToken
            {
                TokenId = tokenId,
                SessionId = Guid.NewGuid().ToString("D"),
                TargetFingerprint = request.TargetFingerprint.Trim(),
                NegotiatedMode = request.NegotiatedMode,
                HostStreamPort = request.HostStreamPort,
                HostControlPort = request.HostControlPort,
                ExpiresAtUtc = nowUtc.Add(ttl)
            };

            _invitations[tokenId] = invitation;
            return invitation;
        }

        public ProjectionInvitationConsumptionResult ConsumeInvitation(string tokenId, string presenterFingerprint)
        {
            if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(presenterFingerprint))
                return new ProjectionInvitationConsumptionResult(false, null, "Token de invitación o certificado faltante.");

            string normalizedToken = tokenId.Trim().ToLowerInvariant();
            if (!_invitations.TryRemove(normalizedToken, out var invitation))
                return new ProjectionInvitationConsumptionResult(false, null, "Token de invitación desconocido o ya consumido.");

            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (invitation.IsExpired(nowUtc))
                return new ProjectionInvitationConsumptionResult(false, null, "Token de invitación expirado.");

            if (!string.Equals(invitation.TargetFingerprint, presenterFingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
                return new ProjectionInvitationConsumptionResult(false, null, "El certificado del receptor no coincide con la invitación.");

            return new ProjectionInvitationConsumptionResult(true, invitation);
        }

        public void PruneExpired()
        {
            DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var kvp in _invitations)
            {
                if (kvp.Value.IsExpired(nowUtc))
                {
                    _invitations.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public enum DisplayControlMessageKind : uint
    {
        ModeChangeRequest = 1,
        ModeChangeAccepted = 2,
        ModeChangeRejected = 3,
        ModeChanged = 4
    }

    public sealed record DisplaySessionControlMessage
    {
        public DisplayControlMessageKind Kind { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int RefreshRateHz { get; init; }
        public string? Reason { get; init; }

        public VirtualDisplayMode ToDisplayMode() => new(Width, Height, RefreshRateHz);

        public static DisplaySessionControlMessage CreateModeChangeRequest(VirtualDisplayMode mode) => new()
        {
            Kind = DisplayControlMessageKind.ModeChangeRequest,
            Width = mode.Width,
            Height = mode.Height,
            RefreshRateHz = mode.RefreshRateHz
        };

        public static DisplaySessionControlMessage CreateModeChangeAccepted(VirtualDisplayMode mode) => new()
        {
            Kind = DisplayControlMessageKind.ModeChangeAccepted,
            Width = mode.Width,
            Height = mode.Height,
            RefreshRateHz = mode.RefreshRateHz
        };

        public static DisplaySessionControlMessage CreateModeChangeRejected(string reason) => new()
        {
            Kind = DisplayControlMessageKind.ModeChangeRejected,
            Reason = reason
        };

        public static DisplaySessionControlMessage CreateModeChanged(VirtualDisplayMode effectiveMode) => new()
        {
            Kind = DisplayControlMessageKind.ModeChanged,
            Width = effectiveMode.Width,
            Height = effectiveMode.Height,
            RefreshRateHz = effectiveMode.RefreshRateHz
        };
    }

    public static class VirtualDisplayCaptureSourceFactory
    {
        public static CaptureSource CreateCaptureSource(
            VirtualDisplaySessionInfo session,
            DisplayTopologySnapshot? activeTopology = null)
        {
            ArgumentNullException.ThrowIfNull(session);

            var path = ResolveMatchingPath(session, activeTopology);
            int width = path?.Width > 0 ? path.Width : session.ActiveMode.Width;
            int height = path?.Height > 0 ? path.Height : session.ActiveMode.Height;

            return new CaptureSource
            {
                Type = CaptureSourceType.Screen,
                Id = $"vdd:{session.MonitorId}",
                Name = "Pantalla inalámbrica",
                Subtitle = $"{width} × {height} @ {session.ActiveMode.RefreshRateHz} Hz",
                DeviceName = path?.DeviceName ?? session.DeviceName,
                X = path?.PositionX ?? 0,
                Y = path?.PositionY ?? 0,
                Width = width,
                Height = height,
                IsPrimary = false
            };
        }

        private static DisplayPathSnapshot? ResolveMatchingPath(
            VirtualDisplaySessionInfo session,
            DisplayTopologySnapshot? activeTopology)
        {
            if (activeTopology == null || string.IsNullOrWhiteSpace(session.DeviceName))
                return null;

            return activeTopology.Paths.FirstOrDefault(p =>
                string.Equals(p.DeviceName, session.DeviceName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
