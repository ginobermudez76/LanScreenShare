using System;
using System.Text.Json.Serialization;

namespace LanScreenShare.Core.Security
{
    public enum DeviceAccessMode
    {
        Auto = 0,
        AskEveryTime = 1
    }

    public sealed record DevicePermissions
    {
        public DeviceAccessMode AccessMode { get; init; } = DeviceAccessMode.Auto;
        public bool AllowRemoteControl { get; init; } = true;
        public bool AllowSourceSwitch { get; init; } = true;
        public bool AllowClipboardSync { get; init; } = true;
        public bool AllowFileTransfer { get; init; } = true;

        public static readonly DevicePermissions Default = new();
    }

    public readonly record struct EffectiveSessionPermissions()
    {
        public bool AllowRemoteControl { get; init; } = true;
        public bool AllowSourceSwitch { get; init; } = true;
        public bool AllowFileTransfer { get; init; } = true;
        public bool AllowClipboardSync { get; init; } = true;

        public static EffectiveSessionPermissions AllAllowed => new();

        public EffectiveSessionPermissions IntersectWith(EffectiveSessionPermissions hostGlobals) => new()
        {
            AllowRemoteControl = AllowRemoteControl && hostGlobals.AllowRemoteControl,
            AllowSourceSwitch = AllowSourceSwitch && hostGlobals.AllowSourceSwitch,
            AllowFileTransfer = AllowFileTransfer && hostGlobals.AllowFileTransfer,
            AllowClipboardSync = AllowClipboardSync && hostGlobals.AllowClipboardSync
        };

        public int ToBitmask() =>
            (AllowRemoteControl ? 1 : 0)
            | (AllowSourceSwitch ? 2 : 0)
            | (AllowFileTransfer ? 4 : 0)
            | (AllowClipboardSync ? 8 : 0);

        public static EffectiveSessionPermissions FromBitmask(int bitmask) => new()
        {
            AllowRemoteControl = (bitmask & 1) != 0,
            AllowSourceSwitch = (bitmask & 2) != 0,
            AllowFileTransfer = (bitmask & 4) != 0,
            AllowClipboardSync = (bitmask & 8) != 0
        };
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
