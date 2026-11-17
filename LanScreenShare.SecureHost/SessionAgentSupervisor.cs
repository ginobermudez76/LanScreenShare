using System;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.SecureHost;

public interface ISpawnedAgentProcess : IDisposable
{
    bool HasExited { get; }
    void Terminate();
}

public interface ISessionAgentLauncher
{
    uint GetActiveConsoleSessionId();
    ISpawnedAgentProcess? LaunchSessionAgent(uint sessionId, int parentPid, string pipeName);
}

/// <summary>
/// Supervisa la sesión gráfica de consola activa y mantiene exactamente un agente SYSTEM
/// ejecutándose dentro de ella para captura e inyección en Escritorio Seguro.
/// </summary>
public sealed class SessionAgentSupervisor : IDisposable
{
    public const uint InvalidSessionId = 0xFFFFFFFF;

    private readonly ISessionAgentLauncher _launcher;
    private readonly string _pipeName;
    private readonly int _parentPid;
    private readonly SemaphoreSlim _wakeSignal = new(0);
    private ISpawnedAgentProcess? _activeAgent;
    private uint _activeSessionId = InvalidSessionId;

    public SessionAgentSupervisor(ISessionAgentLauncher launcher, string pipeName, int parentPid)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _pipeName = pipeName;
        _parentPid = parentPid;
    }

    public uint ActiveSessionId => _activeSessionId;

    public void NotifySessionChanged() => _wakeSignal.Release();

    public void ReconcileOnce()
    {
        uint consoleSession = _launcher.GetActiveConsoleSessionId();
        if (consoleSession == InvalidSessionId)
        {
            TerminateCurrentAgent();
            return;
        }

        bool sessionChanged = consoleSession != _activeSessionId;
        bool agentDown = _activeAgent == null || _activeAgent.HasExited;
        if (!sessionChanged && !agentDown)
            return;

        TerminateCurrentAgent();
        ISpawnedAgentProcess? launched = _launcher.LaunchSessionAgent(consoleSession, _parentPid, _pipeName);
        if (launched != null)
        {
            _activeAgent = launched;
            _activeSessionId = consoleSession;
        }
    }

    public async Task RunAsync(CancellationToken ct, int pollIntervalMs = 1500)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReconcileOnce();
                try
                {
                    await _wakeSignal.WaitAsync(pollIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            TerminateCurrentAgent();
        }
    }

    public void TerminateCurrentAgent()
    {
        ISpawnedAgentProcess? agent = Interlocked.Exchange(ref _activeAgent, null);
        _activeSessionId = InvalidSessionId;
        if (agent == null)
            return;

        try
        {
            if (!agent.HasExited)
                agent.Terminate();
        }
        catch
        {
            // Mejor esfuerzo al detener agente previo
        }
        finally
        {
            agent.Dispose();
        }
    }

    public void Dispose()
    {
        TerminateCurrentAgent();
        _wakeSignal.Dispose();
    }
}
