using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public interface IPairedDeviceStore
    {
        Task<bool> IsDevicePairedAsync(DeviceFingerprint fingerprint);
        Task<bool> IsDeviceRevokedAsync(DeviceFingerprint fingerprint);
        Task<PairedDevice?> GetPairedDeviceAsync(DeviceFingerprint fingerprint);
        Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync();
        Task AddPairedDeviceAsync(PairedDevice device);
        Task RevokeDeviceAsync(DeviceFingerprint fingerprint);
        Task UnrevokeDeviceAsync(DeviceFingerprint fingerprint);
        Task UpdateLastSeenAsync(DeviceFingerprint fingerprint, DateTime? seenAt = null);
        Task UpdateDeviceSettingsAsync(DeviceFingerprint fingerprint, string? alias, DevicePermissions permissions);
        Task RemoveDeviceAsync(DeviceFingerprint fingerprint);
        Task ClearAllDevicesAsync();
    }
}
