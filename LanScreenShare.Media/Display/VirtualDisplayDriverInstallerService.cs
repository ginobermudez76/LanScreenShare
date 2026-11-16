using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Media.Display
{
    public enum VddInstallSource
    {
        LocalInfPackage = 0,
        WingetFallback = 1
    }

    public sealed record VddInstallPlan
    {
        public VddInstallSource Source { get; init; }
        public required string Executable { get; init; }
        public required string Arguments { get; init; }
        public string? PackagePath { get; init; }
        public bool RequiresElevation { get; init; } = true;
    }

    public sealed record VddInstallResult
    {
        public bool Success { get; init; }
        public bool WasCancelledByUser { get; init; }
        public VddInstallSource Source { get; init; }
        public int ExitCode { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public readonly record struct ElevatedProcessOutcome(
        bool Started,
        int ExitCode = 0,
        bool WasCancelledByUser = false,
        string? ErrorMessage = null);

    public interface IElevatedProcessRunner
    {
        ElevatedProcessOutcome RunElevated(VddInstallPlan plan);
    }

    internal sealed class WindowsElevatedProcessRunner : IElevatedProcessRunner
    {
        private const int ErrorCancelledByUser = 1223;

        public ElevatedProcessOutcome RunElevated(VddInstallPlan plan)
        {
            var startInfo = CreateStartInfo(plan);

            try
            {
                using var process = Process.Start(startInfo);
                if (process == null)
                    return new ElevatedProcessOutcome(false, -1, false, "No se pudo iniciar el proceso de instalación.");

                process.WaitForExit();
                return new ElevatedProcessOutcome(true, process.ExitCode);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
            {
                return new ElevatedProcessOutcome(false, ErrorCancelledByUser, true, "La elevación UAC fue cancelada por el usuario.");
            }
            catch (Exception ex)
            {
                return new ElevatedProcessOutcome(false, -1, false, ex.Message);
            }
        }

        internal static ProcessStartInfo CreateStartInfo(VddInstallPlan plan) => new()
        {
            FileName = plan.Executable,
            Arguments = plan.Arguments,
            UseShellExecute = true,
            Verb = plan.RequiresElevation ? "runas" : string.Empty,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    /// <summary>
    /// Asistente efímero de instalación de controlador IDDCx para Equipo A manteniendo `asInvoker` en la app principal.
    /// Ejecuta `pnputil /add-driver "Drivers\VDD\MttVDD.inf" /install` con elevación UAC o `winget` como respaldo.
    /// </summary>
    public sealed class VirtualDisplayDriverInstallerService
    {
        public const string WingetPackageId = "VirtualDrivers.Virtual-Display-Driver";
        private const int ErrorSuccessRebootRequired = 3010;

        private readonly IElevatedProcessRunner _processRunner;
        private readonly Func<string, bool> _fileExists;
        private readonly bool _isWindows;

        public VirtualDisplayDriverInstallerService()
            : this(new WindowsElevatedProcessRunner(), File.Exists, OperatingSystem.IsWindows())
        {
        }

        internal VirtualDisplayDriverInstallerService(
            IElevatedProcessRunner processRunner,
            Func<string, bool> fileExists,
            bool isWindows)
        {
            _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
            _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
            _isWindows = isWindows;
        }

        public static bool IsAnyVirtualDisplayDriverInstalled() =>
            CreateDefaultBackends().Any(b => b.IsDriverInstalled());

        public static IReadOnlyList<IVirtualDisplayBackend> CreateDefaultBackends() =>
        [
            new VirtualDriversVddBackend(),
            new ParsecVddBackend()
        ];

        public VddInstallPlan BuildInstallPlan(string? baseDirectory = null)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppContext.BaseDirectory
                : baseDirectory;

            string? localInf = ResolveLocalInfPath(root);
            if (!string.IsNullOrEmpty(localInf))
            {
                return new VddInstallPlan
                {
                    Source = VddInstallSource.LocalInfPackage,
                    Executable = "pnputil.exe",
                    Arguments = $"/add-driver \"{localInf}\" /install",
                    PackagePath = localInf,
                    RequiresElevation = true
                };
            }

            return new VddInstallPlan
            {
                Source = VddInstallSource.WingetFallback,
                Executable = "winget",
                Arguments = $"install --id {WingetPackageId} --exact --accept-package-agreements --accept-source-agreements",
                RequiresElevation = true
            };
        }

        private string? ResolveLocalInfPath(string baseDirectory)
        {
            var candidates = new List<string>
            {
                Path.Combine(baseDirectory, "Drivers", "VDD", "MttVDD.inf")
            };

            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(WindowsVddEnvironment.GetDefaultInstallDirectory(), "MttVDD.inf"));
            }

            return candidates.FirstOrDefault(_fileExists);
        }

        public Task<VddInstallResult> InstallDriverAsync(
            string? baseDirectory = null,
            CancellationToken ct = default)
        {
            if (!_isWindows)
            {
                return Task.FromResult(new VddInstallResult
                {
                    Success = false,
                    Source = VddInstallSource.LocalInfPackage,
                    ExitCode = -1,
                    Message = "La instalación de controladores virtuales IDDCx solo aplica en Windows 10/11."
                });
            }

            var plan = BuildInstallPlan(baseDirectory);
            return Task.Run(() => MapOutcomeToResult(plan, _processRunner.RunElevated(plan)), ct);
        }

        private static VddInstallResult MapOutcomeToResult(VddInstallPlan plan, ElevatedProcessOutcome outcome)
        {
            if (!outcome.Started)
            {
                return new VddInstallResult
                {
                    Success = false,
                    WasCancelledByUser = outcome.WasCancelledByUser,
                    Source = plan.Source,
                    ExitCode = outcome.ExitCode,
                    Message = outcome.ErrorMessage ?? "No se pudo ejecutar el instalador del controlador."
                };
            }

            if (outcome.ExitCode == 0 || outcome.ExitCode == ErrorSuccessRebootRequired)
            {
                string suffix = outcome.ExitCode == ErrorSuccessRebootRequired
                    ? " (puede requerir reinicio para completar la activación)."
                    : ".";
                return new VddInstallResult
                {
                    Success = true,
                    Source = plan.Source,
                    ExitCode = outcome.ExitCode,
                    Message = $"Controlador de pantalla virtual instalado correctamente{suffix}"
                };
            }

            return new VddInstallResult
            {
                Success = false,
                Source = plan.Source,
                ExitCode = outcome.ExitCode,
                Message = $"El instalador finalizó con código {outcome.ExitCode}."
            };
        }
    }
}
