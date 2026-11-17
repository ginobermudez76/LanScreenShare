using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LanScreenShare.SecureHost;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSystemSessionLauncher : ISessionAgentLauncher
{
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;

    public uint GetActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    public ISpawnedAgentProcess? LaunchSessionAgent(uint sessionId, int parentPid, string pipeName)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        if (!TryCreateSessionPrimaryToken(sessionId, out IntPtr hPrimaryToken))
            return null;

        try
        {
            return SpawnProcessWithToken(hPrimaryToken, exePath, parentPid, pipeName);
        }
        finally
        {
            CloseHandle(hPrimaryToken);
        }
    }

    private static bool TryCreateSessionPrimaryToken(uint sessionId, out IntPtr hPrimaryToken)
    {
        hPrimaryToken = IntPtr.Zero;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAllAccess, out IntPtr hSystemToken))
            return false;

        try
        {
            if (!DuplicateTokenEx(hSystemToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr dupToken))
                return false;

            byte[] sessionBytes = BitConverter.GetBytes(sessionId);
            if (!SetTokenInformation(dupToken, TokenSessionId, sessionBytes, (uint)sessionBytes.Length))
            {
                CloseHandle(dupToken);
                return false;
            }

            hPrimaryToken = dupToken;
            return true;
        }
        finally
        {
            CloseHandle(hSystemToken);
        }
    }

    private static SpawnedProcessWrapper? SpawnProcessWithToken(
        IntPtr hPrimaryToken,
        string exePath,
        int parentPid,
        string pipeName)
    {
        IntPtr envBlock = IntPtr.Zero;
        IntPtr lpDesktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        try
        {
            bool hasEnv = CreateEnvironmentBlock(out envBlock, hPrimaryToken, false);
            uint creationFlags = CreateNoWindow | (hasEnv ? CreateUnicodeEnvironment : 0);
            var startupInfo = new StartupInfoW
            {
                cb = Marshal.SizeOf<StartupInfoW>(),
                lpDesktop = lpDesktop,
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwHide
            };

            string commandLine = $"\"{exePath}\" --session-agent --parent-pid {parentPid} --pipe \"{pipeName}\"";
            string workDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            if (!CreateProcessAsUserW(
                hPrimaryToken,
                exePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                hasEnv ? envBlock : IntPtr.Zero,
                workDir,
                ref startupInfo,
                out ProcessInformation procInfo))
            {
                return null;
            }

            if (procInfo.hThread != IntPtr.Zero) CloseHandle(procInfo.hThread);
            if (procInfo.hProcess != IntPtr.Zero) CloseHandle(procInfo.hProcess);
            return TryWrapProcess(procInfo.dwProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(lpDesktop);
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
        }
    }

    private static SpawnedProcessWrapper? TryWrapProcess(uint processId)
    {
        try
        {
            return new SpawnedProcessWrapper(Process.GetProcessById((int)processId));
        }
        catch
        {
            return null;
        }
    }

    private sealed class SpawnedProcessWrapper(Process process) : ISpawnedAgentProcess
    {
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch { return true; }
            }
        }

        public void Terminate()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignorar si ya salió
            }
        }

        public void Dispose() => process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(
        IntPtr existingTokenHandle,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr newTokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        [In] byte[] tokenInformation,
        uint tokenInformationLength);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment,
        IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUserW(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo,
        out ProcessInformation lpProcessInformation);
}
