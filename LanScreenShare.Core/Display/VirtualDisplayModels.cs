using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Display
{
    public enum VirtualDisplayState
    {
        Inactive = 0,
        DriverCheck = 1,
        Negotiating = 2,
        CreatingDisplay = 3,
        ConfiguringTopology = 4,
        ConnectingStream = 5,
        Streaming = 6,
        Reconnecting = 7,
        Suspended = 8,
        SuspendRecovery = 9,
        Destroying = 10,
        Error = 11
    }

    public readonly record struct VirtualDisplayMode(int Width, int Height, int RefreshRateHz)
    {
        public static VirtualDisplayMode Default => new(1920, 1080, 60);
    }

    public static class VirtualDisplayModeValidator
    {
        public const int MinWidth = 640;
        public const int MaxWidth = 3840;
        public const int MinHeight = 480;
        public const int MaxHeight = 2160;
        public const int MinRefreshRateHz = 30;
        public const int MaxRefreshRateHz = 120;

        public static bool IsSupported(in VirtualDisplayMode mode)
        {
            if (mode.Width < MinWidth || mode.Width > MaxWidth)
                return false;
            if (mode.Height < MinHeight || mode.Height > MaxHeight)
                return false;
            return mode.RefreshRateHz >= MinRefreshRateHz && mode.RefreshRateHz <= MaxRefreshRateHz;
        }

        public static VirtualDisplayMode? SelectBestSupportedMode(
            IReadOnlyList<VirtualDisplayMode> advertisedModes,
            VirtualDisplayMode? preferred = null)
        {
            ArgumentNullException.ThrowIfNull(advertisedModes);
            var valid = advertisedModes.Where(m => IsSupported(in m)).ToList();
            if (valid.Count == 0)
                return null;

            if (preferred is { } target && valid.Contains(target))
                return target;

            return valid
                .OrderByDescending(m => (long)m.Width * m.Height)
                .ThenByDescending(m => m.RefreshRateHz)
                .First();
        }
    }

    public sealed record VirtualDisplayBackendCapabilities
    {
        public required string BackendId { get; init; }
        public Version MinimumIddCxVersion { get; init; } = new(1, 4);
        public Version MinimumWindowsBuild { get; init; } = new(10, 0, 19041);
        public bool HasNativeKernelWatchdog { get; init; }
        public TimeSpan NativeWatchdogTimeout { get; init; } = TimeSpan.Zero;
        public bool RequiresAdminForHotplug { get; init; }
        public bool SupportsDynamicModeChange { get; init; } = true;
    }

    public sealed record VirtualDisplayInstance
    {
        public required string BackendId { get; init; }
        public required string MonitorId { get; init; }
        public string? DeviceName { get; init; }
        public VirtualDisplayMode Mode { get; init; } = VirtualDisplayMode.Default;
        public int MonitorIndex { get; init; }
        public uint AdapterLuidLow { get; init; }
        public int AdapterLuidHigh { get; init; }
    }

    public sealed record DisplayPathSnapshot
    {
        public uint AdapterLuidLow { get; init; }
        public int AdapterLuidHigh { get; init; }
        public uint SourceId { get; init; }
        public uint TargetId { get; init; }
        public int PositionX { get; init; }
        public int PositionY { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public uint RefreshRateNumerator { get; init; }
        public uint RefreshRateDenominator { get; init; }
        public int Rotation { get; init; }
        public int Scaling { get; init; }
        public int PixelFormat { get; init; }
        public string DeviceName { get; init; } = string.Empty;
    }

    public sealed record DisplayTopologySnapshot
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
        public string? BackendId { get; init; }
        public string? VirtualMonitorId { get; init; }
        public string? VirtualDeviceName { get; init; }
        public IReadOnlyList<DisplayPathSnapshot> Paths { get; init; } = Array.Empty<DisplayPathSnapshot>();

        public static string GetDefaultBackupPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanScreenShare",
            "topology_backup.json");

        public async Task SaveToFileAsync(string filePath, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, SerializerOptions);
            await File.WriteAllTextAsync(filePath, json, ct);
        }

        public static async Task<DisplayTopologySnapshot?> TryLoadFromFileAsync(string filePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                string json = await File.ReadAllTextAsync(filePath, ct);
                return JsonSerializer.Deserialize<DisplayTopologySnapshot>(json, SerializerOptions);
            }
            catch
            {
                return null;
            }
        }

        public static void DeleteFileIfExists(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Best-effort cleanup of snapshot backup
            }
        }
    }

    public sealed record VirtualDisplaySessionInfo
    {
        public required string BackendId { get; init; }
        public required string MonitorId { get; init; }
        public string? DeviceName { get; init; }
        public VirtualDisplayMode ActiveMode { get; init; }
        public required string SnapshotPath { get; init; }
        public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    }

    public enum VirtualDisplayErrorCode
    {
        None = 0,
        InvalidMode,
        DisplayBusy,
        DriverNotInstalled,
        CreateFailed,
        TopologyFailed,
        NoSession,
        ModeUpdateFailed
    }

    public sealed record VirtualDisplayOperationResult
    {
        public bool Success { get; init; }
        public VirtualDisplayErrorCode ErrorCode { get; init; }
        public string? Message { get; init; }
        public VirtualDisplaySessionInfo? Session { get; init; }

        public static VirtualDisplayOperationResult Ok(VirtualDisplaySessionInfo? session = null, string? message = null) =>
            new() { Success = true, ErrorCode = VirtualDisplayErrorCode.None, Session = session, Message = message };

        public static VirtualDisplayOperationResult Fail(VirtualDisplayErrorCode errorCode, string message) =>
            new() { Success = false, ErrorCode = errorCode, Message = message };
    }

    public sealed record VirtualDisplayStateChangedEventArgs
    {
        public VirtualDisplayState PreviousState { get; init; }
        public VirtualDisplayState CurrentState { get; init; }
        public string? Reason { get; init; }
    }

    public sealed record VirtualDisplayWatchdogLaunchOptions
    {
        public int ParentPid { get; init; }
        public required string SnapshotPath { get; init; }
        public required string MonitorId { get; init; }
        public required string BackendId { get; init; }
    }

    public sealed record VirtualDisplayCoordinatorOptions
    {
        public string SnapshotFilePath { get; init; } = DisplayTopologySnapshot.GetDefaultBackupPath();
        public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(200);
        public bool AutoEnterStreamingState { get; init; } = true;
    }

    public interface IVirtualDisplayBackend
    {
        VirtualDisplayBackendCapabilities Capabilities { get; }
        bool IsDriverInstalled();
        Task<VirtualDisplayInstance?> CreateVirtualMonitorAsync(VirtualDisplayMode mode, CancellationToken ct = default);
        Task<bool> UpdateModeAsync(string monitorId, VirtualDisplayMode mode, CancellationToken ct = default);
        Task<bool> DestroyVirtualMonitorAsync(string monitorId, CancellationToken ct = default);
        Task<bool> SendHeartbeatAsync(string monitorId, CancellationToken ct = default);
    }

    public interface IDisplayTopologyManager
    {
        DisplayTopologySnapshot CaptureSnapshot();
        bool ApplyExtendTopology(string? preferredDeviceName, VirtualDisplayMode mode);
        bool RestoreSnapshot(DisplayTopologySnapshot snapshot);
    }

    public interface IWatchdogProcessLauncher
    {
        IDisposable? StartWatchdog(VirtualDisplayWatchdogLaunchOptions options);
    }

    public interface IVirtualDisplayService : IAsyncDisposable
    {
        VirtualDisplayState State { get; }
        VirtualDisplaySessionInfo? CurrentSession { get; }
        event EventHandler<VirtualDisplayStateChangedEventArgs>? StateChanged;

        Task<VirtualDisplayOperationResult> StartExtendedDisplayAsync(VirtualDisplayMode requestedMode, CancellationToken ct = default);
        Task<VirtualDisplayOperationResult> NotifyStreamConnectedAsync(CancellationToken ct = default);
        Task<VirtualDisplayOperationResult> ChangeDisplayModeAsync(VirtualDisplayMode newMode, CancellationToken ct = default);
        Task HandleNetworkDisconnectedAsync(CancellationToken ct = default);
        Task HandleNetworkReconnectedAsync(CancellationToken ct = default);
        Task HandleSystemSuspendAsync(CancellationToken ct = default);
        Task HandleSystemResumeAsync(CancellationToken ct = default);
        Task StopExtendedDisplayAsync(CancellationToken ct = default);
        Task RecoverOrphanedSessionOnStartupAsync(CancellationToken ct = default);
    }
}
