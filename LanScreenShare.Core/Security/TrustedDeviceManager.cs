using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;

namespace LanScreenShare.Core.Security
{
    public enum TrustedDeviceStatus
    {
        Authorized = 0,
        Connected = 1,
        Revoked = 2
    }

    public sealed record TrustedDeviceViewItem(
        PairedDevice Device,
        TrustedDeviceStatus Status,
        string ShortFingerprint,
        string StatusLabel,
        string? ActiveSessionId,
        bool IsActiveController)
    {
        public string DisplayName => Device.DisplayName;
        public string OriginalName => Device.DeviceName;
        public string PairedAtDisplay => FormatLocalTimestamp(Device.PairedAt);
        public string LastSeenAtDisplay => FormatLocalTimestamp(Device.LastSeenAt);

        private static string FormatLocalTimestamp(DateTime utcOrLocal)
        {
            if (utcOrLocal == default) return "—";
            return utcOrLocal.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Coordina las operaciones de administración de dispositivos autorizados, permisos granulares,
    /// expulsión en caliente y rotación de identidad mTLS.
    /// </summary>
    public sealed class TrustedDeviceManager
    {
        private readonly IPairedDeviceStore _deviceStore;
        private readonly ISessionAuthority _sessionAuthority;
        private readonly IIdentityKeyStorage _identityKeyStorage;
        private readonly Func<IInputSimulatorService?> _getInputSimulator;

        public TrustedDeviceManager(
            IPairedDeviceStore deviceStore,
            ISessionAuthority sessionAuthority,
            IIdentityKeyStorage identityKeyStorage,
            Func<IInputSimulatorService?>? getInputSimulator = null)
        {
            _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
            _sessionAuthority = sessionAuthority ?? throw new ArgumentNullException(nameof(sessionAuthority));
            _identityKeyStorage = identityKeyStorage ?? throw new ArgumentNullException(nameof(identityKeyStorage));
            _getInputSimulator = getInputSimulator ?? (() => null);
        }

        public static string FormatShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return "Desconocida";

            const string shaPrefix = "SHA256:";
            string body = fingerprint.StartsWith(shaPrefix, StringComparison.OrdinalIgnoreCase)
                ? fingerprint[shaPrefix.Length..]
                : fingerprint;

            if (body.Length <= 16)
                return fingerprint;

            return $"{body[..8]}…{body[^8..]}";
        }

        public async Task<int> GetTrustedDeviceCountAsync()
        {
            var devices = await _deviceStore.GetAllPairedDevicesAsync();
            return devices.Count;
        }

        public async Task<IReadOnlyList<TrustedDeviceViewItem>> GetTrustedDevicesAsync()
        {
            var devices = await _deviceStore.GetAllPairedDevicesAsync();
            var activeSessions = _sessionAuthority.GetActiveSessions();
            string? activeControllerSessionId = _sessionAuthority.ActiveControllerSessionId;

            var sessionByFingerprint = activeSessions
                .GroupBy(s => s.PeerFingerprint.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            return devices
                .Select(device => BuildViewItem(device, sessionByFingerprint, activeControllerSessionId))
                .OrderByDescending(item => item.Status == TrustedDeviceStatus.Connected)
                .ThenBy(item => item.Status == TrustedDeviceStatus.Revoked)
                .ThenByDescending(item => item.Device.LastSeenAt)
                .ToList();
        }

        private static TrustedDeviceViewItem BuildViewItem(
            PairedDevice device,
            Dictionary<string, ActiveSession> sessionByFingerprint,
            string? activeControllerSessionId)
        {
            sessionByFingerprint.TryGetValue(device.PublicKeyFingerprint, out var activeSession);
            var status = ResolveStatus(device, activeSession);
            string statusLabel = status switch
            {
                TrustedDeviceStatus.Connected => "● Conectado ahora",
                TrustedDeviceStatus.Revoked => "Bloqueado / Revocado",
                _ => "Autorizado"
            };

            bool isController = activeSession != null
                && !string.IsNullOrEmpty(activeControllerSessionId)
                && string.Equals(activeSession.SessionId.Value, activeControllerSessionId, StringComparison.Ordinal);

            return new TrustedDeviceViewItem(
                device,
                status,
                FormatShortFingerprint(device.PublicKeyFingerprint),
                statusLabel,
                activeSession?.SessionId.Value,
                isController);
        }

        private static TrustedDeviceStatus ResolveStatus(PairedDevice device, ActiveSession? activeSession)
        {
            if (device.IsRevoked) return TrustedDeviceStatus.Revoked;
            return activeSession != null ? TrustedDeviceStatus.Connected : TrustedDeviceStatus.Authorized;
        }

        public async Task RevokeDeviceAsync(DeviceFingerprint fingerprint)
        {
            await _deviceStore.RevokeDeviceAsync(fingerprint);
            TerminateDeviceSessionsAndReleaseInputs(fingerprint);
        }

        public Task UnrevokeDeviceAsync(DeviceFingerprint fingerprint) =>
            _deviceStore.UnrevokeDeviceAsync(fingerprint);

        public async Task RemoveDeviceAsync(DeviceFingerprint fingerprint)
        {
            await _deviceStore.RemoveDeviceAsync(fingerprint);
            TerminateDeviceSessionsAndReleaseInputs(fingerprint);
        }

        public async Task UpdateDeviceSettingsAsync(
            DeviceFingerprint fingerprint,
            string? alias,
            DevicePermissions permissions)
        {
            ArgumentNullException.ThrowIfNull(permissions);
            await _deviceStore.UpdateDeviceSettingsAsync(fingerprint, alias, permissions);
            _sessionAuthority.UpdateDevicePermissions(fingerprint, permissions);

            if (!permissions.AllowRemoteControl)
            {
                _getInputSimulator()?.ReleaseInjectedInputs();
            }
        }

        public void RevokeRemoteControlForDevice(DeviceFingerprint fingerprint)
        {
            if (fingerprint.IsEmpty) return;

            var activeSessions = _sessionAuthority.GetActiveSessions();
            foreach (var session in activeSessions)
            {
                if (string.Equals(session.PeerFingerprint.Value, fingerprint.Value, StringComparison.OrdinalIgnoreCase))
                {
                    _sessionAuthority.RevokeControl(session.SessionId);
                }
            }

            _getInputSimulator()?.ReleaseInjectedInputs();
        }

        public async Task RevokeAllDevicesAsync()
        {
            _sessionAuthority.TerminateAllSessions();
            _getInputSimulator()?.ReleaseInjectedInputs();
            await _deviceStore.ClearAllDevicesAsync();
        }

        public async Task<X509Certificate2> RegenerateIdentityAsync()
        {
            _sessionAuthority.TerminateAllSessions();
            _getInputSimulator()?.ReleaseInjectedInputs();
            await _identityKeyStorage.ResetIdentityAsync();
            return await _identityKeyStorage.GetOrCreateIdentityCertificateAsync();
        }

        private void TerminateDeviceSessionsAndReleaseInputs(DeviceFingerprint fingerprint)
        {
            _sessionAuthority.TerminateSessionsForDevice(fingerprint);
            _getInputSimulator()?.ReleaseInjectedInputs();
        }
    }
}
