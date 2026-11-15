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

    public sealed record PairedDevice(
        string DeviceId,
        string DeviceName,
        string PublicKeyFingerprint,
        DateTime PairedAt,
        DateTime LastSeenAt,
        bool IsRevoked = false,
        string? Alias = null,
        DevicePermissions? Permissions = null)
    {
        [JsonIgnore]
        public DevicePermissions EffectivePermissions => Permissions ?? DevicePermissions.Default;

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? DeviceName : Alias;
    }
}
