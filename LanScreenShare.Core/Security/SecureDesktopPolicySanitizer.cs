using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LanScreenShare.Core.Security;

public enum SecureDesktopPolicyCheckResult
{
    NotApplicablePlatform,
    AlreadySecureOrDefault,
    RestoredToDefault,
    InsufficientPermissionsOrError
}

public interface IPromptOnSecureDesktopStore
{
    object? ReadValue();
    void WriteEnabled();
}

/// <summary>
/// Verifica y restaura la política HKLM\...\Policies\System\PromptOnSecureDesktop = 1
/// para reparar equipos en los que versiones anteriores deshabilitaron el Escritorio Seguro.
/// </summary>
public static class SecureDesktopPolicySanitizer
{
    public const string PolicySubKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string PolicyValueName = "PromptOnSecureDesktop";

    public static bool ShouldRestorePromptOnSecureDesktop(object? rawValue)
    {
        return rawValue switch
        {
            int intVal => intVal == 0,
            long longVal => longVal == 0L,
            uint uintVal => uintVal == 0u,
            string strVal when int.TryParse(strVal.Trim(), out int parsed) => parsed == 0,
            _ => false
        };
    }

    public static SecureDesktopPolicyCheckResult RestoreIfDisabled(IPromptOnSecureDesktopStore? store = null)
    {
        if (store == null && !OperatingSystem.IsWindows())
            return SecureDesktopPolicyCheckResult.NotApplicablePlatform;

        IPromptOnSecureDesktopStore effectiveStore = store ?? CreateDefaultWindowsStore();
        try
        {
            object? currentValue = effectiveStore.ReadValue();
            if (!ShouldRestorePromptOnSecureDesktop(currentValue))
                return SecureDesktopPolicyCheckResult.AlreadySecureOrDefault;

            effectiveStore.WriteEnabled();
            return SecureDesktopPolicyCheckResult.RestoredToDefault;
        }
        catch
        {
            return SecureDesktopPolicyCheckResult.InsufficientPermissionsOrError;
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsRegistryPromptOnSecureDesktopStore CreateDefaultWindowsStore() =>
        new();

    [SupportedOSPlatform("windows")]
    private sealed class WindowsRegistryPromptOnSecureDesktopStore : IPromptOnSecureDesktopStore
    {
        public object? ReadValue()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicySubKeyPath, writable: false);
            return key?.GetValue(PolicyValueName);
        }

        public void WriteEnabled()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicySubKeyPath, writable: true);
            key?.SetValue(PolicyValueName, 1, RegistryValueKind.DWord);
        }
    }
}
