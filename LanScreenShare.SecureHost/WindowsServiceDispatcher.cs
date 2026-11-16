using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using LanScreenShare.Core.Security;

namespace LanScreenShare.SecureHost;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsServiceDispatcher
{
    public const string ServiceName = "LanScreenShareSecureHost";

    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceAcceptSessionChange = 0x00000080;

    private const uint ServiceControlStop = 1;
    private const uint ServiceControlShutdown = 5;
    private const uint ServiceControlSessionChange = 14;
    private const uint NoError = 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainProc(uint argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceCtrlHandlerEx(uint dwControl, uint dwEventType, IntPtr lpEventData, IntPtr lpContext);

    private static IntPtr _statusHandle;
    private static CancellationTokenSource? _serviceCts;
    private static SessionAgentSupervisor? _supervisor;
    private static ServiceMainProc? _serviceMainDelegate;
    private static ServiceCtrlHandlerEx? _ctrlHandlerDelegate;
    private static readonly string _pipeNameDefault = SecureHostCliOptions.TryParseArgs(["--service"], out var def)
        ? def.PipeName
        : "LanScreenShare.SecureAgent";
    private static string _activePipeName = _pipeNameDefault;

    public static void Run(string pipeName)
    {
        _activePipeName = pipeName;
        _serviceMainDelegate = OnServiceMain;
        IntPtr serviceNamePtr = Marshal.StringToHGlobalUni(ServiceName);
        try
        {
            var table = new ServiceTableEntry[]
            {
                new() { lpServiceName = serviceNamePtr, lpServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMainDelegate) },
                default
            };
            StartServiceCtrlDispatcherW(table);
        }
        finally
        {
            Marshal.FreeHGlobal(serviceNamePtr);
            GC.KeepAlive(_serviceMainDelegate);
        }
    }

    private static void OnServiceMain(uint argc, IntPtr argv)
    {
        _ctrlHandlerDelegate = OnServiceControl;
        IntPtr serviceNamePtr = Marshal.StringToHGlobalUni(ServiceName);
        try
        {
            _statusHandle = RegisterServiceCtrlHandlerExW(
                serviceNamePtr,
                Marshal.GetFunctionPointerForDelegate(_ctrlHandlerDelegate),
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(serviceNamePtr);
        }

        if (_statusHandle == IntPtr.Zero)
            return;

        ReportStatus(ServiceStartPending, waitHintMs: 3000);
        SecureDesktopPolicySanitizer.RestoreIfDisabled();

        _serviceCts = new CancellationTokenSource();
        var launcher = new WindowsSystemSessionLauncher();
        using var supervisor = new SessionAgentSupervisor(launcher, _activePipeName, Environment.ProcessId);
        _supervisor = supervisor;

        ReportStatus(ServiceRunning, waitHintMs: 0);
        supervisor.RunAsync(_serviceCts.Token).GetAwaiter().GetResult();

        _supervisor = null;
        ReportStatus(ServiceStopped, waitHintMs: 0);
        GC.KeepAlive(_ctrlHandlerDelegate);
    }

    private static uint OnServiceControl(uint dwControl, uint dwEventType, IntPtr lpEventData, IntPtr lpContext)
    {
        switch (dwControl)
        {
            case ServiceControlStop:
            case ServiceControlShutdown:
                ReportStatus(ServiceStopPending, waitHintMs: 3000);
                try { _serviceCts?.Cancel(); } catch { /* Best effort */ }
                return NoError;

            case ServiceControlSessionChange:
                _supervisor?.NotifySessionChanged();
                return NoError;

            default:
                return NoError;
        }
    }

    private static void ReportStatus(uint currentState, uint waitHintMs)
    {
        if (_statusHandle == IntPtr.Zero)
            return;

        var status = new ServiceStatus
        {
            dwServiceType = ServiceWin32OwnProcess,
            dwCurrentState = currentState,
            dwControlsAccepted = currentState == ServiceRunning
                ? ServiceAcceptStop | ServiceAcceptShutdown | ServiceAcceptSessionChange
                : 0,
            dwWaitHint = waitHintMs
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public IntPtr lpServiceName;
        public IntPtr lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] lpServiceStartTable);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial IntPtr RegisterServiceCtrlHandlerExW(
        IntPtr lpServiceName,
        IntPtr lpHandlerProc,
        IntPtr lpContext);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(IntPtr hServiceStatus, ref ServiceStatus lpServiceStatus);
}
