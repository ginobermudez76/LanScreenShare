using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Media.SecureAgent;

/// <summary>
/// Servidor Named Pipe ejecutado dentro de la sesión interactiva bajo privilegios SYSTEM.
/// Administra captura e input nativo (incluyendo Escritorio Seguro Winlogon) para LanScreenShare.Desktop.
/// </summary>
public sealed class SecureAgentPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<CaptureSource, string, IScreenCaptureService> _captureFactory;
    private readonly Func<IInputSimulatorService> _inputFactory;

    public SecureAgentPipeServer(
        Func<CaptureSource, string, IScreenCaptureService> captureFactory,
        Func<IInputSimulatorService> inputFactory,
        string pipeName = SecureAgentProtocol.PipeName)
    {
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SecureAgentProtocol.PipeName : pipeName;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = CreatePipeInstance();
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleClientSessionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecureAgentServer] Error en ciclo de cliente IPC: {ex.Message}");
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream CreatePipeInstance()
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsAclPipe(_pipeName);

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsAclPipe(string pipeName)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private async Task HandleClientSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outgoingChannel = Channel.CreateBounded<SecureAgentPacket>(new BoundedChannelOptions(12)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var sessionState = new ActiveSessionHandler(_captureFactory, _inputFactory, outgoingChannel.Writer);
        Task writerTask = RunOutgoingWriterAsync(pipe, outgoingChannel.Reader, sessionCts.Token);

        try
        {
            while (!sessionCts.IsCancellationRequested && pipe.IsConnected)
            {
                SecureAgentPacket? packet = await SecureAgentProtocol.ReadPacketAsync(pipe, sessionCts.Token).ConfigureAwait(false);
                if (packet == null)
                    break;

                sessionState.HandleIncomingPacket(packet.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Desconexión / cancelación ordenada
        }
        catch (IOException)
        {
            // El cliente cerró el pipe
        }
        finally
        {
            outgoingChannel.Writer.TryComplete();
            await sessionCts.CancelAsync().ConfigureAwait(false);
            try { await writerTask.ConfigureAwait(false); } catch { /* Best effort */ }
        }
    }

    private static async Task RunOutgoingWriterAsync(
        Stream pipe,
        ChannelReader<SecureAgentPacket> reader,
        CancellationToken ct)
    {
        await foreach (SecureAgentPacket packet in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await SecureAgentProtocol.WritePacketAsync(pipe, packet.Type, packet.Payload, ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ActiveSessionHandler : IDisposable
    {
        private readonly Func<CaptureSource, string, IScreenCaptureService> _captureFactory;
        private readonly IInputSimulatorService _inputSimulator;
        private readonly ChannelWriter<SecureAgentPacket> _outgoing;
        private IScreenCaptureService? _captureService;

        public ActiveSessionHandler(
            Func<CaptureSource, string, IScreenCaptureService> captureFactory,
            Func<IInputSimulatorService> inputFactory,
            ChannelWriter<SecureAgentPacket> outgoing)
        {
            _captureFactory = captureFactory;
            _inputSimulator = inputFactory();
            _outgoing = outgoing;
        }

        public void HandleIncomingPacket(in SecureAgentPacket packet)
        {
            switch (packet.Type)
            {
                case SecureAgentMessageType.StartCapture:
                    StartCaptureFromRequest(packet.Payload);
                    break;

                case SecureAgentMessageType.StopCapture:
                    StopActiveCapture();
                    break;

                case SecureAgentMessageType.UpdateSettings:
                    UpdateActiveCaptureSettings(packet.Payload);
                    break;

                case SecureAgentMessageType.RequestKeyframe:
                    _captureService?.RequestKeyframe();
                    break;

                case SecureAgentMessageType.SetInputTarget:
                    CaptureSourceDto? dto = SecureAgentProtocol.DecodeJson<CaptureSourceDto>(packet.Payload);
                    if (dto != null)
                        _inputSimulator.SetTargetSource(dto.ToModel());
                    break;

                case SecureAgentMessageType.SimulateInput:
                    RemoteInputMessage? input = SecureAgentProtocol.DecodeInputPayload(packet.Payload);
                    if (input != null)
                        _inputSimulator.SimulateInput(input);
                    break;

                case SecureAgentMessageType.ReleaseInputs:
                    _inputSimulator.ReleaseInjectedInputs();
                    break;

                case SecureAgentMessageType.Ping:
                    _outgoing.TryWrite(new SecureAgentPacket(SecureAgentMessageType.Pong, Array.Empty<byte>()));
                    break;
            }
        }

        private void StartCaptureFromRequest(ReadOnlySpan<byte> payload)
        {
            AgentStartCaptureRequest? request = SecureAgentProtocol.DecodeJson<AgentStartCaptureRequest>(payload);
            if (request == null)
                return;

            StopActiveCapture();

            CaptureSource source = request.Source.ToModel();
            _inputSimulator.SetTargetSource(source);

            IScreenCaptureService capture = _captureFactory(source, request.Engine);
            capture.FrameCaptured += OnCaptureFrame;
            capture.SecureDesktopChanged += OnCaptureSecureDesktopChanged;
            _captureService = capture;

            capture.StartCapture(request.Profile);
            PublishStatusSnapshot(capture);
        }

        private void UpdateActiveCaptureSettings(ReadOnlySpan<byte> payload)
        {
            if (_captureService == null)
                return;

            CaptureProfile? profile = SecureAgentProtocol.DecodeJson<CaptureProfile>(payload);
            if (profile.HasValue)
            {
                _captureService.UpdateSettings(profile.Value);
                PublishStatusSnapshot(_captureService);
            }
        }

        private void StopActiveCapture()
        {
            IScreenCaptureService? capture = Interlocked.Exchange(ref _captureService, null);
            if (capture != null)
            {
                capture.FrameCaptured -= OnCaptureFrame;
                capture.SecureDesktopChanged -= OnCaptureSecureDesktopChanged;
                capture.StopCapture();
                capture.Dispose();
            }
        }

        private void OnCaptureFrame(object? sender, CapturedFrame frame)
        {
            byte[] payload = SecureAgentProtocol.EncodeFramePayload(frame);
            _outgoing.TryWrite(new SecureAgentPacket(SecureAgentMessageType.Frame, payload));
        }

        private void OnCaptureSecureDesktopChanged(object? sender, bool isSecure)
        {
            byte[] payload = [isSecure ? (byte)1 : (byte)0];
            _outgoing.TryWrite(new SecureAgentPacket(SecureAgentMessageType.SecureDesktopState, payload));
        }

        private void PublishStatusSnapshot(IScreenCaptureService capture)
        {
            var status = new AgentStatusSnapshot(
                capture.IsCapturing,
                capture.ActiveEncoderDescription,
                capture.IsHardwareAccelerated);
            _outgoing.TryWrite(new SecureAgentPacket(
                SecureAgentMessageType.StatusSnapshot,
                SecureAgentProtocol.EncodeJson(status)));
        }

        public void Dispose()
        {
            StopActiveCapture();
            _inputSimulator.ReleaseInjectedInputs();
            _inputSimulator.Dispose();
        }
    }
}
