using System;
using LanScreenShare.Media.SecureAgent;

namespace LanScreenShare.SecureHost;

public enum SecureHostRunMode
{
    Service,
    SessionAgent
}

public readonly record struct SecureHostLaunchOptions(
    SecureHostRunMode Mode,
    int ParentPid,
    string PipeName);

public static class SecureHostCliOptions
{
    public static bool TryParseArgs(string[] args, out SecureHostLaunchOptions options)
    {
        options = new SecureHostLaunchOptions(SecureHostRunMode.Service, 0, SecureAgentProtocol.PipeName);
        if (args == null || args.Length == 0)
            return false;

        SecureHostRunMode? mode = null;
        int parentPid = 0;
        string pipeName = SecureAgentProtocol.PipeName;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase))
            {
                mode = SecureHostRunMode.Service;
            }
            else if (string.Equals(arg, "--session-agent", StringComparison.OrdinalIgnoreCase))
            {
                mode = SecureHostRunMode.SessionAgent;
            }
            else if (string.Equals(arg, "--parent-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out parentPid) || parentPid <= 0)
                    return false;
            }
            else if (string.Equals(arg, "--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pipeName = args[++i];
                if (string.IsNullOrWhiteSpace(pipeName))
                    return false;
            }
            else
            {
                return false;
            }
        }

        if (!mode.HasValue)
            return false;

        options = new SecureHostLaunchOptions(mode.Value, parentPid, pipeName);
        return true;
    }
}
