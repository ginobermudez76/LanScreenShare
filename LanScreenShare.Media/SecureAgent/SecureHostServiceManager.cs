using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Media.SecureAgent;

public enum SecureHostServiceStatus
{
    UnsupportedPlatform,
    NotInstalled,
    Stopped,
    Running
}

public readonly record struct ElevatedProcessCommand(string FileName, string Arguments);

public interface IServiceControlBackend
{
    SecureHostServiceStatus QueryStatus();
    bool TryStartService();
    bool TryStopService();
    bool TryRunElevatedCommand(in ElevatedProcessCommand command);
    bool IsAgentPipeReady();
}

/// <summary>
/// Administra bajo demanda el ciclo de vida del servicio SYSTEM LanScreenShareSecureHost
/// desde la UI (sin UAC en modo instalado gracias a la ACL del servicio, o con UAC puntual en portátil).
/// </summary>
public sealed class SecureHostServiceManager
{
    public const string DefaultServiceName = "LanScreenShareSecureHost";

    private readonly IServiceControlBackend _backend;
    private readonly string _serviceName;

    public SecureHostServiceManager(
        IServiceControlBackend? backend = null,
        string serviceName = DefaultServiceName)
    {
        _serviceName = serviceName;
        _backend = backend ?? CreateDefaultBackend(serviceName);
    }

    public bool RegisteredTemporarily { get; private set; }

    public SecureHostServiceStatus GetStatus() => _backend.QueryStatus();

    public async Task<bool> EnsureServiceRunningAsync(string? secureHostExePath, CancellationToken ct = default)
    {
        SecureHostServiceStatus status = _backend.QueryStatus();
        if (status == SecureHostServiceStatus.UnsupportedPlatform)
            return false;

        if (status == SecureHostServiceStatus.Running && _backend.IsAgentPipeReady())
            return true;

        FileInfo? exeFile = string.IsNullOrWhiteSpace(secureHostExePath) ? null : new FileInfo(secureHostExePath);
        bool started = status == SecureHostServiceStatus.NotInstalled
            ? TryInstallAndStartPortable(exeFile)
            : TryStartExistingService();

        if (!started)
            return false;

        return await WaitForAgentPipeAsync(TimeSpan.FromMilliseconds(4500), ct).ConfigureAwait(false);
    }

    public async Task<bool> StopServiceAsync(bool removeIfTemporary = true, CancellationToken ct = default)
    {
        SecureHostServiceStatus status = _backend.QueryStatus();
        if (status == SecureHostServiceStatus.UnsupportedPlatform || status == SecureHostServiceStatus.NotInstalled)
        {
            RegisteredTemporarily = false;
            return true;
        }

        if (status == SecureHostServiceStatus.Running && !_backend.TryStopService())
        {
            _backend.TryRunElevatedCommand(new ElevatedProcessCommand(GetScExePath(), $"stop {_serviceName}"));
        }

        await WaitUntilServiceStoppedAsync(TimeSpan.FromMilliseconds(3500), ct).ConfigureAwait(false);

        if (RegisteredTemporarily && removeIfTemporary)
        {
            _backend.TryRunElevatedCommand(new ElevatedProcessCommand(GetScExePath(), $"delete {_serviceName}"));
            RegisteredTemporarily = false;
        }

        return _backend.QueryStatus() != SecureHostServiceStatus.Running;
    }

    private bool TryStartExistingService()
    {
        if (_backend.TryStartService())
            return true;

        return _backend.TryRunElevatedCommand(new ElevatedProcessCommand(GetScExePath(), $"start {_serviceName}"));
    }

    private bool TryInstallAndStartPortable(FileInfo? exeFile)
    {
        if (exeFile == null || !exeFile.Exists)
            return false;

        string scPath = GetScExePath();
        string cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string binPathArg = $"\\\"{exeFile.FullName}\\\" --service";
        string script = $"/c \"\"{scPath}\" create {_serviceName} binPath= \"{binPathArg}\" start= demand DisplayName= \"LanScreenShare Secure Desktop Service\" && \"{scPath}\" start {_serviceName}\"";

        if (!_backend.TryRunElevatedCommand(new ElevatedProcessCommand(cmdPath, script)))
            return false;

        RegisteredTemporarily = true;
        return true;
    }

    private async Task<bool> WaitForAgentPipeAsync(TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_backend.IsAgentPipeReady())
                return true;

            await Task.Delay(120, ct).ConfigureAwait(false);
        }

        return _backend.IsAgentPipeReady();
    }

    private async Task WaitUntilServiceStoppedAsync(TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_backend.QueryStatus() != SecureHostServiceStatus.Running)
                return;

            await Task.Delay(120, ct).ConfigureAwait(false);
        }
    }

    private static string GetScExePath() => Path.Combine(Environment.SystemDirectory, "sc.exe");

    private static IServiceControlBackend CreateDefaultBackend(string serviceName) =>
        OperatingSystem.IsWindows() ? new WindowsScServiceBackend(serviceName) : new UnsupportedServiceBackend();

    internal static SecureHostServiceStatus ParseScQueryOutput(int exitCode, string output)
    {
        if (exitCode == 1060)
            return SecureHostServiceStatus.NotInstalled;

        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            return SecureHostServiceStatus.Running;

        if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) || exitCode == 0)
            return SecureHostServiceStatus.Stopped;

        return SecureHostServiceStatus.NotInstalled;
    }

    private sealed class UnsupportedServiceBackend : IServiceControlBackend
    {
        public SecureHostServiceStatus QueryStatus() => SecureHostServiceStatus.UnsupportedPlatform;
        public bool TryStartService() => false;
        public bool TryStopService() => false;
        public bool TryRunElevatedCommand(in ElevatedProcessCommand command) => false;
        public bool IsAgentPipeReady() => false;
    }

    private sealed class WindowsScServiceBackend(string serviceName) : IServiceControlBackend
    {
        public SecureHostServiceStatus QueryStatus()
        {
            var (exitCode, output) = RunScCommand($"query {serviceName}");
            return ParseScQueryOutput(exitCode, output);
        }

        public bool TryStartService() => RunScCommand($"start {serviceName}").ExitCode == 0;

        public bool TryStopService() => RunScCommand($"stop {serviceName}").ExitCode == 0;

        public bool TryRunElevatedCommand(in ElevatedProcessCommand command)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = command.Arguments,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (proc == null)
                    return false;

                proc.WaitForExit(10000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public bool IsAgentPipeReady() => SecureAgentClient.IsAgentAvailable();

        private static (int ExitCode, string Output) RunScCommand(string arguments)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = GetScExePath(),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc == null)
                    return (-1, string.Empty);

                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(8000);
                return (proc.ExitCode, stdout);
            }
            catch
            {
                return (-1, string.Empty);
            }
        }
    }
}
