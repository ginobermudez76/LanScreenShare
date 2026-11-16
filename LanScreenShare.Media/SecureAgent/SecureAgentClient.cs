using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Media.SecureAgent;

/// <summary>
/// Cliente IPC sobre Named Pipe hacia el agente de sesión SYSTEM (LanScreenShare.SecureHost).
/// Permite capturar y controlar el Escritorio Seguro (Winlogon) desde la UI sin privilegios.
/// </summary>
public sealed class SecureAgentClient : IDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _disposed;

    public SecureAgentClient(string pipeName = SecureAgentProtocol.PipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsConnected => _pipe is { IsConnected: true } && !_disposed;

    public event EventHandler<CapturedFrame>? FrameReceived;
    public event EventHandler<bool>? SecureDesktopChanged;
    public event EventHandler<AgentStatusSnapshot>? StatusReceived;
    public event EventHandler? Disconnected;

    public static bool IsAgentAvailable(string pipeName = SecureAgentProtocol.PipeName, int probeTimeoutMs = 120)
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            probe.Connect(probeTimeoutMs);
            return probe.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryConnectAsync(int timeoutMs = 400, CancellationToken ct = default)
    {
        if (_disposed)
            return false;
        if (IsConnected)
            return true;

        DisconnectPipe();
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(timeoutMs);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            if (!pipe.IsConnected)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            _pipe = pipe;
            _readerCts = new CancellationTokenSource();
            var readerToken = _readerCts.Token;
            _readerTask = Task.Run(() => RunReaderLoopAsync(pipe, readerToken), CancellationToken.None);
            return true;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private bool IsPipeActive(NamedPipeClientStream? pipe) =>
        !_disposed && pipe is { IsConnected: true } && ReferenceEquals(_pipe, pipe);

    public async Task<bool> SendAsync(SecureAgentMessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        NamedPipeClientStream? pipe = _pipe;
        if (!IsPipeActive(pipe))
            return false;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsPipeActive(pipe))
                return false;

            await SecureAgentProtocol.WritePacketAsync(pipe!, type, payload, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            DisconnectPipe();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunReaderLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                SecureAgentPacket? packet = await SecureAgentProtocol.ReadPacketAsync(pipe, ct).ConfigureAwait(false);
                if (packet == null)
                    break;

                DispatchIncomingPacket(packet.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Cierre limpio
        }
        catch (IOException)
        {
            // Pipe cerrado por el servidor
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SecureAgentClient] Error de lectura IPC: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_pipe, pipe))
            {
                DisconnectPipe();
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void DispatchIncomingPacket(in SecureAgentPacket packet)
    {
        switch (packet.Type)
        {
            case SecureAgentMessageType.Frame:
                CapturedFrame? frame = SecureAgentProtocol.DecodeFramePayload(packet.Payload);
                if (frame != null)
                    FrameReceived?.Invoke(this, frame);
                break;

            case SecureAgentMessageType.SecureDesktopState:
                bool isSecure = packet.Payload.Length > 0 && packet.Payload[0] != 0;
                SecureDesktopChanged?.Invoke(this, isSecure);
                break;

            case SecureAgentMessageType.StatusSnapshot:
                AgentStatusSnapshot? status = SecureAgentProtocol.DecodeJson<AgentStatusSnapshot>(packet.Payload);
                if (status != null)
                    StatusReceived?.Invoke(this, status);
                break;
        }
    }

    private void DisconnectPipe()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _readerCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* Best effort */ }
            cts.Dispose();
        }

        NamedPipeClientStream? pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe != null)
        {
            try { pipe.Dispose(); } catch { /* Best effort */ }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisconnectPipe();
        try { _readerTask?.Wait(250, CancellationToken.None); } catch { /* Best effort */ }
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Proxy de IScreenCaptureService que delega la captura al agente SYSTEM cuando está disponible
/// o degrada al motor en proceso si no hay servicio instalado.
/// </summary>
public sealed class AgentBackedScreenCaptureService : IScreenCaptureService
{
    private readonly SecureAgentClient _client;
    private readonly CaptureSource _source;
    private readonly string _engine;
    private readonly Func<IScreenCaptureService> _fallbackFactory;
    private IScreenCaptureService? _activeFallback;
    private CaptureProfile _currentProfile = new();
    private bool _isCapturing;
    private bool _disposed;

    public AgentBackedScreenCaptureService(
        SecureAgentClient client,
        CaptureSource source,
        string engine,
        Func<IScreenCaptureService> fallbackFactory)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _engine = engine;
        _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));

        _client.FrameReceived += OnAgentFrameReceived;
        _client.SecureDesktopChanged += OnAgentSecureDesktopChanged;
        _client.StatusReceived += OnAgentStatusReceived;
        _client.Disconnected += OnAgentDisconnected;
    }

    public event EventHandler<CapturedFrame>? FrameCaptured;
    public event EventHandler<bool>? SecureDesktopChanged;

    public bool IsCapturing => _isCapturing || (_activeFallback?.IsCapturing ?? false);
    public string? ActiveEncoderDescription { get; private set; }
    public bool IsHardwareAccelerated { get; private set; }

    public int TargetFps
    {
        get => _currentProfile.TargetFps;
        set => UpdateSettings(_currentProfile with { TargetFps = Math.Clamp(value, 1, 120) });
    }

    public int Quality
    {
        get => _currentProfile.Quality;
        set => UpdateSettings(_currentProfile with { Quality = Math.Clamp(value, 10, 100) });
    }

    public int BitrateMbps
    {
        get => _currentProfile.BitrateMbps;
        set => UpdateSettings(_currentProfile with { BitrateMbps = Math.Clamp(value, 1, 50) });
    }

    public double ResolutionScale
    {
        get => _currentProfile.ResolutionScale;
        set => UpdateSettings(_currentProfile with { ResolutionScale = Math.Clamp(value, 0.25, 1.0) });
    }

    public int TargetWidth
    {
        get => _currentProfile.TargetWidth;
        set => UpdateSettings(_currentProfile with { TargetWidth = Math.Max(0, value) });
    }

    public int TargetHeight
    {
        get => _currentProfile.TargetHeight;
        set => UpdateSettings(_currentProfile with { TargetHeight = Math.Max(0, value) });
    }

    public string Codec
    {
        get => _currentProfile.Codec;
        set => UpdateSettings(_currentProfile with { Codec = string.Equals(value, "h264", StringComparison.OrdinalIgnoreCase) ? "h264" : "jpeg" });
    }

    public void StartCapture(CaptureProfile profile)
    {
        if (_disposed)
            return;

        _currentProfile = profile;
        if (_client.IsConnected || _client.TryConnectAsync(300).GetAwaiter().GetResult())
        {
            var request = new AgentStartCaptureRequest(CaptureSourceDto.FromModel(_source), _engine, profile);
            if (_client.SendAsync(SecureAgentMessageType.StartCapture, SecureAgentProtocol.EncodeJson(request)).GetAwaiter().GetResult())
            {
                _isCapturing = true;
                return;
            }
        }

        ActivateFallbackCapture(profile);
    }

    public void StopCapture()
    {
        _isCapturing = false;
        if (_client.IsConnected)
        {
            _ = _client.SendAsync(SecureAgentMessageType.StopCapture, ReadOnlyMemory<byte>.Empty);
        }

        StopAndDisposeFallback();
    }

    public void UpdateSettings(CaptureProfile profile)
    {
        _currentProfile = profile;
        if (_activeFallback != null)
        {
            _activeFallback.UpdateSettings(profile);
            return;
        }

        if (_isCapturing && _client.IsConnected)
        {
            _ = _client.SendAsync(SecureAgentMessageType.UpdateSettings, SecureAgentProtocol.EncodeJson(profile));
        }
    }

    public void RequestKeyframe()
    {
        if (_activeFallback != null)
        {
            _activeFallback.RequestKeyframe();
            return;
        }

        if (_client.IsConnected)
        {
            _ = _client.SendAsync(SecureAgentMessageType.RequestKeyframe, ReadOnlyMemory<byte>.Empty);
        }
    }

    private void ActivateFallbackCapture(CaptureProfile profile)
    {
        StopAndDisposeFallback();
        var fallback = _fallbackFactory();
        fallback.FrameCaptured += OnFallbackFrameCaptured;
        fallback.SecureDesktopChanged += OnFallbackSecureDesktopChanged;
        _activeFallback = fallback;
        fallback.StartCapture(profile);
        ActiveEncoderDescription = fallback.ActiveEncoderDescription;
        IsHardwareAccelerated = fallback.IsHardwareAccelerated;
    }

    private void StopAndDisposeFallback()
    {
        IScreenCaptureService? fallback = Interlocked.Exchange(ref _activeFallback, null);
        if (fallback != null)
        {
            fallback.FrameCaptured -= OnFallbackFrameCaptured;
            fallback.SecureDesktopChanged -= OnFallbackSecureDesktopChanged;
            fallback.StopCapture();
            fallback.Dispose();
        }
    }

    private void OnAgentFrameReceived(object? sender, CapturedFrame frame)
    {
        if (_isCapturing)
            FrameCaptured?.Invoke(this, frame);
    }

    private void OnAgentSecureDesktopChanged(object? sender, bool isSecure)
    {
        if (_isCapturing)
            SecureDesktopChanged?.Invoke(this, isSecure);
    }

    private void OnAgentStatusReceived(object? sender, AgentStatusSnapshot status)
    {
        ActiveEncoderDescription = status.ActiveEncoderDescription;
        IsHardwareAccelerated = status.IsHardwareAccelerated;
    }

    private void OnAgentDisconnected(object? sender, EventArgs e)
    {
        if (!_isCapturing || _disposed)
            return;
        if (_activeFallback != null)
            return;

        _isCapturing = false;
        ActivateFallbackCapture(_currentProfile);
    }

    private void OnFallbackFrameCaptured(object? sender, CapturedFrame frame) => FrameCaptured?.Invoke(this, frame);
    private void OnFallbackSecureDesktopChanged(object? sender, bool isSecure) => SecureDesktopChanged?.Invoke(this, isSecure);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _client.FrameReceived -= OnAgentFrameReceived;
        _client.SecureDesktopChanged -= OnAgentSecureDesktopChanged;
        _client.StatusReceived -= OnAgentStatusReceived;
        _client.Disconnected -= OnAgentDisconnected;
        StopCapture();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Proxy de IInputSimulatorService que reenvía eventos al agente SYSTEM cuando está conectado,
/// o utiliza el simulador local como respaldo.
/// </summary>
public sealed class AgentBackedInputSimulatorService : IInputSimulatorService
{
    private readonly SecureAgentClient _client;
    private readonly IInputSimulatorService _fallbackSimulator;
    private bool _disposed;

    public AgentBackedInputSimulatorService(SecureAgentClient client, IInputSimulatorService fallbackSimulator)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _fallbackSimulator = fallbackSimulator ?? throw new ArgumentNullException(nameof(fallbackSimulator));
    }

    public void SetTargetSource(CaptureSource source)
    {
        if (_disposed)
            return;

        _fallbackSimulator.SetTargetSource(source);
        if (_client.IsConnected)
        {
            byte[] payload = SecureAgentProtocol.EncodeJson(CaptureSourceDto.FromModel(source));
            _ = _client.SendAsync(SecureAgentMessageType.SetInputTarget, payload);
        }
    }

    public void SimulateInput(RemoteInputMessage input)
    {
        if (_disposed)
            return;

        if (_client.IsConnected)
        {
            byte[] payload = SecureAgentProtocol.EncodeInputPayload(input);
            _ = _client.SendAsync(SecureAgentMessageType.SimulateInput, payload);
            return;
        }

        _fallbackSimulator.SimulateInput(input);
    }

    public void ReleaseInjectedInputs()
    {
        if (_disposed)
            return;

        if (_client.IsConnected)
        {
            _ = _client.SendAsync(SecureAgentMessageType.ReleaseInputs, ReadOnlyMemory<byte>.Empty);
        }

        _fallbackSimulator.ReleaseInjectedInputs();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseInjectedInputs();
        _fallbackSimulator.Dispose();
        GC.SuppressFinalize(this);
    }
}
