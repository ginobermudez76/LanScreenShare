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

        var state = new ParseState();
        int idx = 0;
        while (idx < args.Length)
        {
            if (!TryConsumeArg(args, ref idx, state))
                return false;
        }

        if (!state.Mode.HasValue)
            return false;

        options = new SecureHostLaunchOptions(state.Mode.Value, state.ParentPid, state.PipeName);
        return true;
    }

    private static bool TryConsumeArg(string[] args, ref int idx, ParseState state)
    {
        string arg = args[idx++];
        if (string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase))
        {
            state.Mode = SecureHostRunMode.Service;
            return true;
        }

        if (string.Equals(arg, "--session-agent", StringComparison.OrdinalIgnoreCase))
        {
            state.Mode = SecureHostRunMode.SessionAgent;
            return true;
        }

        return TryConsumeValueArg(args, arg, ref idx, state);
    }

    private static bool TryConsumeValueArg(string[] args, string flag, ref int idx, ParseState state)
    {
        if (idx >= args.Length)
            return false;

        if (string.Equals(flag, "--parent-pid", StringComparison.OrdinalIgnoreCase))
        {
            bool valid = int.TryParse(args[idx++], out int pid) && pid > 0;
            state.ParentPid = pid;
            return valid;
        }

        if (string.Equals(flag, "--pipe", StringComparison.OrdinalIgnoreCase))
        {
            string value = args[idx++];
            state.PipeName = value;
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private sealed class ParseState
    {
        public SecureHostRunMode? Mode { get; set; }
        public int ParentPid { get; set; }
        public string PipeName { get; set; } = SecureAgentProtocol.PipeName;
    }
}
