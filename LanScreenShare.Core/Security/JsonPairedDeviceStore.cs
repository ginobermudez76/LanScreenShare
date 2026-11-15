using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security;

/// <summary>
/// Almacén persistente en JSON para dispositivos emparejados y de confianza, con escrituras atómicas
/// para tolerancia ante apagones o fallas repentinas. Cumple con la especificación ADR-001.
/// </summary>
public class JsonPairedDeviceStore : IPairedDeviceStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonPairedDeviceStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _filePath = customFilePath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "LanScreenShare");
            _filePath = Path.Combine(appFolder, "trusted_devices.json");
        }
    }

    private static bool MatchesFingerprint(PairedDevice device, DeviceFingerprint fingerprint) =>
        string.Equals(device.PublicKeyFingerprint, fingerprint.Value, StringComparison.OrdinalIgnoreCase);

    public Task<bool> IsDevicePairedAsync(DeviceFingerprint fingerprint) =>
        QueryDevicesAsync(devices => devices.Any(d => MatchesFingerprint(d, fingerprint)));

    public Task<bool> IsDeviceRevokedAsync(DeviceFingerprint fingerprint) =>
        QueryDevicesAsync(devices =>
        {
            var device = devices.FirstOrDefault(d => MatchesFingerprint(d, fingerprint));
            return device != null && device.IsRevoked;
        });

    public Task<PairedDevice?> GetPairedDeviceAsync(DeviceFingerprint fingerprint) =>
        QueryDevicesAsync(devices => devices.FirstOrDefault(d => MatchesFingerprint(d, fingerprint)));

    public Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync() =>
        QueryDevicesAsync<IReadOnlyList<PairedDevice>>(devices => devices.ToList());

    public Task AddPairedDeviceAsync(PairedDevice device) =>
        MutateDevicesAsync(devices =>
        {
            UpsertDevice(devices, device);
            return true;
        });

    public Task RevokeDeviceAsync(DeviceFingerprint fingerprint) =>
        MutateDevicesAsync(devices => UpdateDeviceEntry(devices, fingerprint, d => d with { IsRevoked = true }));

    public Task UnrevokeDeviceAsync(DeviceFingerprint fingerprint) =>
        MutateDevicesAsync(devices => UpdateDeviceEntry(devices, fingerprint, d => d with { IsRevoked = false }));

    public Task UpdateLastSeenAsync(DeviceFingerprint fingerprint, DateTime? seenAt = null) =>
        MutateDevicesAsync(devices => UpdateDeviceEntry(devices, fingerprint, d => d with { LastSeenAt = seenAt ?? DateTime.UtcNow }));

    public Task UpdateDeviceSettingsAsync(DeviceFingerprint fingerprint, string? alias, DevicePermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        string? normalizedAlias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        return MutateDevicesAsync(devices =>
            UpdateDeviceEntry(devices, fingerprint, d => d with { Alias = normalizedAlias, Permissions = permissions }));
    }

    private static void UpsertDevice(List<PairedDevice> devices, PairedDevice device)
    {
        int idx = FindDeviceIndex(devices, (DeviceFingerprint)device.PublicKeyFingerprint);
        if (idx < 0)
        {
            devices.Add(device);
            return;
        }
        devices[idx] = device;
    }

    private static bool UpdateDeviceEntry(
        List<PairedDevice> devices,
        DeviceFingerprint fingerprint,
        Func<PairedDevice, PairedDevice> transform)
    {
        int idx = FindDeviceIndex(devices, fingerprint);
        if (idx < 0) return false;
        devices[idx] = transform(devices[idx]);
        return true;
    }

    private static int FindDeviceIndex(List<PairedDevice> devices, DeviceFingerprint fingerprint) =>
        devices.FindIndex(d => MatchesFingerprint(d, fingerprint));

    public Task RemoveDeviceAsync(DeviceFingerprint fingerprint) =>
        MutateDevicesAsync(devices =>
            devices.RemoveAll(d => MatchesFingerprint(d, fingerprint)) > 0);

    public Task ClearAllDevicesAsync() =>
        MutateDevicesAsync(devices =>
        {
            bool hadAny = devices.Count > 0;
            devices.Clear();
            return hadAny;
        });

    private async Task<T> QueryDevicesAsync<T>(Func<List<PairedDevice>, T> query)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            return query(devices);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task MutateDevicesAsync(Func<List<PairedDevice>, bool> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            var devices = await LoadDevicesInternalAsync();
            if (mutate(devices))
            {
                await SaveDevicesInternalAsync(devices);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<PairedDevice>> LoadDevicesInternalAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new List<PairedDevice>();
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var result = await JsonSerializer.DeserializeAsync<List<PairedDevice>>(stream, JsonOptions);
            return result ?? new List<PairedDevice>();
        }
        catch (JsonException)
        {
            // Respaldar archivo corrupto para evitar sobreescritura ciega
            string corruptBackup = _filePath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Copy(_filePath, corruptBackup, overwrite: true);
            }
            catch
            {
                // Ignorar error de respaldo best-effort ante corrupción de archivo
            }

            return new List<PairedDevice>();
        }
    }

    private async Task SaveDevicesInternalAsync(List<PairedDevice> devices)
    {
        string directory = Path.GetDirectoryName(_filePath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, devices, JsonOptions);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Ignorar error de limpieza de archivo temporal */ }
            }
        }
    }
}

