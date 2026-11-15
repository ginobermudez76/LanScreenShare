using System;
using System.Text.Json.Serialization;

namespace LanScreenShare.Core.Security
{
    public enum DeviceAccessMode
    {
        Auto = 0,
        AskEveryTime = 1
    }

    public sealed record DevicePermissions(
        DeviceAccessMode AccessMode = DeviceAccessMode.Auto,
        bool AllowRemoteControl = true,
        bool AllowClipboardSync = true,
        bool AllowFileTransfer = true)
    {
        public static readonly DevicePermissions Default = new();
    }

    public sealed record PairedDevice
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public string PublicKeyFingerprint { get; init; } = string.Empty;
        public DateTime PairedAt { get; init; }
        public DateTime LastSeenAt { get; init; }
        public bool IsRevoked { get; init; }
        public string? Alias { get; init; }
        public DevicePermissions? Permissions { get; init; }

        [JsonIgnore]
        public DevicePermissions EffectivePermissions => Permissions ?? DevicePermissions.Default;

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? DeviceName : Alias;

        public static PairedDevice Create(
            string deviceId,
            string deviceName,
            string publicKeyFingerprint,
            bool isRevoked = false) =>
            new()
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                PublicKeyFingerprint = publicKeyFingerprint,
                PairedAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                IsRevoked = isRevoked
            };
    }
}
