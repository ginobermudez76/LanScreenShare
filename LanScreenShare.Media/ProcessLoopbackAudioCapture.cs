using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public sealed partial class ProcessLoopbackAudioCapture : IDisposable
    {
        private static readonly StrategyBasedComWrappers ComWrappersInstance = new();
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        public static Action<string>? OnLog { get; set; }

        private static void Log(string msg)
        {
            Console.WriteLine(msg);
            OnLog?.Invoke(msg);
        }
        private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing { get; private set; }

        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private IAudioClient? _audioClient;
        private IAudioCaptureClient? _captureClient;
        private WaitHandle? _bufferEvent;
        private bool _disposed;

        private sealed class NativeWaitHandle : WaitHandle
        {
            public NativeWaitHandle(IntPtr rawHandle)
            {
                SafeWaitHandle = new SafeWaitHandle(rawHandle, ownsHandle: true);
            }
        }

        private static (IntPtr pProp, IntPtr pParams) CreateActivationPropVariant(uint targetPid, bool includeProcess)
        {
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = targetPid,
                    ProcessLoopbackMode = includeProcess
                        ? ProcessLoopbackMode.IncludeTargetProcessTree
                        : ProcessLoopbackMode.ExcludeTargetProcessTree
                }
            };

            IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.StructureToPtr(activationParams, pParams, false);

            var propVariant = new PropVariant
            {
                vt = 65, // VT_BLOB
                blob = new NativeBlob
                {
                    cbSize = Marshal.SizeOf<AudioClientActivationParams>(),
                    pBlobData = pParams
                }
            };

            IntPtr pProp = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
            Marshal.StructureToPtr(propVariant, pProp, false);
            return (pProp, pParams);
        }

        private static IAudioClient? CreateAudioClientFromUnknown(IntPtr pUnk)
        {
            if (pUnk == IntPtr.Zero)
                return null;

            try
            {
                return ComWrappersInstance.GetOrCreateObjectForComInstance(pUnk, CreateObjectFlags.UniqueInstance) as IAudioClient;
            }
            finally
            {
                Marshal.Release(pUnk);
            }
        }

        private static async Task<IAudioClient?> ActivateProcessLoopbackClientAsync(uint targetPid, bool includeProcess, CancellationToken cancellationToken)
        {
            var handler = new AudioInterfaceCompletionHandler();
            var (pProp, pParams) = CreateActivationPropVariant(targetPid, includeProcess);

            IActivateAudioInterfaceAsyncOperation? asyncOp = null;
            try
            {
                int hr = ActivateAudioInterfaceAsync(
                    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    in IID_IAudioClient,
                    pProp,
                    handler,
                    out asyncOp);

                if (hr < 0)
                {
                    Log($"[ProcessAudio] ActivateAudioInterfaceAsync falló con HRESULT: 0x{hr:X8}");
                    return null;
                }

                using var timeoutCts = new CancellationTokenSource(2500);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    IntPtr pUnk = await handler.Task.WaitAsync(linkedCts.Token);
                    return CreateAudioClientFromUnknown(pUnk);
                }
                catch (Exception ex)
                {
                    Log($"[ProcessAudio] Timeout o error esperando activación COM (targetPid={targetPid}): {ex.Message}");
                    return null;
                }
            }
            finally
            {
                GC.KeepAlive(handler);
                ReleaseComInstance(asyncOp);
                Marshal.FreeHGlobal(pProp);
                Marshal.FreeHGlobal(pParams);
            }
        }

        private static WaveFormatExtensible CreateWaveFormatExtensible(int sampleRate) => new()
        {
            Format = new WaveFormatEx
            {
                wFormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
                nChannels = 2,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * 8),
                nBlockAlign = 8,
                wBitsPerSample = 32,
                cbSize = 22
            },
            wValidBitsPerSample = 32,
            dwChannelMask = 0x0003, // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
            SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71") // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        };

        private static int TryInitializeAtRate(IAudioClient audioClient, int sampleRate, long bufferDurationHns, IntPtr pFormat)
        {
            var wfExt = CreateWaveFormatExtensible(sampleRate);
            Marshal.StructureToPtr(wfExt, pFormat, false);

            uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK |
                               AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                               AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                               AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            int hr = audioClient.Initialize(0, streamFlags, bufferDurationHns, 0, pFormat, IntPtr.Zero);
            if (hr < 0)
            {
                streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
                hr = audioClient.Initialize(0, streamFlags, bufferDurationHns, 0, pFormat, IntPtr.Zero);
            }

            return hr;
        }

        private static int InitializeAudioClientFormat(IAudioClient audioClient, out WaveFormat waveFormat)
        {
            IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
            long bufferDurationHns = 10000000; // 1 segundo (en unidades de 100ns)
            try
            {
                int hr = TryInitializeAtRate(audioClient, 48000, bufferDurationHns, pFormat);
                if (hr >= 0)
                {
                    waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                    return hr;
                }

                Console.WriteLine($"[ProcessAudio] Initialize 48kHz falló con 0x{hr:X8}, reintentando 44.1kHz...");
                hr = TryInitializeAtRate(audioClient, 44100, bufferDurationHns, pFormat);
                waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                return hr;
            }
            finally
            {
                Marshal.FreeHGlobal(pFormat);
            }
        }

        public Task<bool> StartAsync(int processId, CancellationToken cancellationToken = default)
        {
            if (IsCapturing)
                return Task.FromResult(false);

            return Task.Run(async () =>
            {
                bool includeProcess = processId > 0;
                uint targetPid = includeProcess ? (uint)processId : (uint)Environment.ProcessId;

                try
                {
                    _audioClient = await ActivateProcessLoopbackClientAsync(targetPid, includeProcess, cancellationToken);
                    if (_audioClient == null)
                    {
                        Log("[ProcessAudio] El objeto activado no implementa IAudioClient");
                        return false;
                    }

                    if (!TryInitializeClientPipeline(_audioClient, out var waveFormat, out var captureClient))
                    {
                        Cleanup();
                        return false;
                    }

                    _captureClient = captureClient;
                    _cts = new CancellationTokenSource();
                    IsCapturing = true;
                    _captureTask = Task.Run(() => CaptureLoop(waveFormat, _cts.Token), _cts.Token);
                    string modeDesc = includeProcess ? $"PID {processId} (aislado)" : $"Escritorio completo (excluyendo PID {targetPid})";
                    Log($"[ProcessAudio] Captura iniciada: {modeDesc} ({waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, {waveFormat.BitsPerSample}bits)");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[ProcessAudio] Error general iniciando captura de proceso: {ex}");
                    Cleanup();
                    return false;
                }
            }, cancellationToken);
        }

        private bool TryInitializeClientPipeline(
            IAudioClient client,
            out WaveFormat waveFormat,
            out IAudioCaptureClient captureClient)
        {
            captureClient = null!;
            int hr = InitializeAudioClientFormat(client, out waveFormat);
            if (hr < 0)
            {
                Log($"[ProcessAudio] IAudioClient.Initialize falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            IntPtr hEvent = CreateEvent(IntPtr.Zero, bManualReset: false, bInitialState: false, IntPtr.Zero);
            if (hEvent == IntPtr.Zero)
            {
                Log("[ProcessAudio] CreateEventW falló al crear el evento de audio.");
                return false;
            }

            _bufferEvent = new NativeWaitHandle(hEvent);
            hr = client.SetEventHandle(hEvent);
            if (hr < 0)
            {
                Log($"[ProcessAudio] SetEventHandle falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            hr = client.GetService(in IID_IAudioCaptureClient, out IntPtr pCaptureClient);
            if (hr < 0 || pCaptureClient == IntPtr.Zero)
            {
                Log($"[ProcessAudio] GetService(IAudioCaptureClient) falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            try
            {
                captureClient = (IAudioCaptureClient)ComWrappersInstance.GetOrCreateObjectForComInstance(
                    pCaptureClient,
                    CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                Marshal.Release(pCaptureClient);
            }
            hr = client.Start();
            if (hr < 0)
            {
                Log($"[ProcessAudio] IAudioClient.Start falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            return true;
        }

        private static bool IsValidBuffer(int hrGet, IntPtr pData, uint numFrames) =>
            hrGet >= 0 && pData != IntPtr.Zero && numFrames > 0;

        private bool TryReadAndDispatchPacket(WaveFormat waveFormat, short formatEncoding)
        {
            int hrNext = _captureClient!.GetNextPacketSize(out uint packetSize);
            if (hrNext < 0 || packetSize == 0)
                return false;

            int hrGet = _captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
            if (!IsValidBuffer(hrGet, pData, numFrames))
                return false;

            int byteCount = (int)(numFrames * waveFormat.BlockAlign);
            byte[] buffer = new byte[byteCount];

            if ((flags & 2) == 0) // AUDCLNT_BUFFERFLAGS_SILENT
            {
                Marshal.Copy(pData, buffer, 0, byteCount);
            }

            _captureClient.ReleaseBuffer(numFrames);

            var chunk = new CapturedAudioChunk
            {
                Data = buffer,
                SampleRate = waveFormat.SampleRate,
                Channels = waveFormat.Channels,
                BitsPerSample = waveFormat.BitsPerSample,
                AudioFormat = formatEncoding,
                Source = AudioSourceType.Desktop,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            AudioCaptured?.Invoke(this, chunk);
            return true;
        }

        private void CaptureLoop(WaveFormat waveFormat, CancellationToken token)
        {
            if (_bufferEvent == null || _captureClient == null)
                return;

            short formatEncoding = (short)(waveFormat.BitsPerSample == 32 ? 3 : (short)waveFormat.Encoding);
            var waitHandles = new WaitHandle[] { _bufferEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                int index = WaitHandle.WaitAny(waitHandles, 20);
                if (index == 1)
                    break;

                DrainPackets(waveFormat, formatEncoding, token);
            }
        }

        private void DrainPackets(WaveFormat waveFormat, short formatEncoding, CancellationToken token)
        {
            while (!token.IsCancellationRequested && TryReadAndDispatchPacket(waveFormat, formatEncoding))
            {
                // Continue draining all available packets from the capture buffer
            }
        }

        public void Stop()
        {
            if (!IsCapturing)
                return;

            IsCapturing = false;
            _cts?.Cancel();

            try
            {
                _audioClient?.Stop();
            }
            catch
            {
                // Ignore errors stopping audio client during shutdown
            }

            try
            {
                _captureTask?.Wait(200, CancellationToken.None);
            }
            catch
            {
                // Ignore task cancellation or timeout on stop
            }

            Cleanup();
            Console.WriteLine("[ProcessAudio] Captura de audio de proceso detenida.");
        }

        private static void ReleaseComInstance(object? comObject)
        {
            if (comObject is ComObject managedCom)
            {
                try
                {
                    managedCom.FinalRelease();
                }
                catch
                {
                    // Ignore COM release errors during cleanup
                }
            }
        }

        private void Cleanup()
        {
            IsCapturing = false;
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;

            ReleaseComInstance(_audioClient);
            _audioClient = null;

            ReleaseComInstance(_captureClient);
            _captureClient = null;

            _bufferEvent?.Dispose();
            _bufferEvent = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }

        #region Native COM Definitions

        private enum AudioClientActivationType
        {
            Default = 0,
            ProcessLoopback = 1
        }

        private enum ProcessLoopbackMode
        {
            IncludeTargetProcessTree = 0,
            ExcludeTargetProcessTree = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParams
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParams ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatExtensible
        {
            public WaveFormatEx Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBlob
        {
            public int cbSize;
            public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public NativeBlob blob;
        }

        [GeneratedComInterface]
        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
        internal partial interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        [GeneratedComInterface]
        [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
        internal partial interface IAgileObject
        {
        }

        [GeneratedComInterface]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        internal partial interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig]
            int GetActivateResult(out int hrResult, out IntPtr ppUnk);
        }

        [GeneratedComInterface]
        [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        internal partial interface IAudioClient
        {
            [PreserveSig]
            int Initialize(
                int ShareMode,
                uint StreamFlags,
                long hnsBufferDuration,
                long hnsPeriodicity,
                IntPtr pFormat,
                IntPtr AudioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint pNumBufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long phnsLatency);

            [PreserveSig]
            int GetCurrentPadding(out uint pNumPaddingFrames);

            [PreserveSig]
            int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr ppDeviceFormat);

            [PreserveSig]
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService(in Guid riid, out IntPtr ppv);
        }

        [GeneratedComInterface]
        [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        internal partial interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(
                out IntPtr ppData,
                out uint pNumFramesToRead,
                out uint pdwFlags,
                out ulong pu64DevicePosition,
                out ulong pu64QPCPosition);

            [PreserveSig]
            int ReleaseBuffer(uint NumFramesRead);

            [PreserveSig]
            int GetNextPacketSize(out uint pNumFramesInNextPacket);
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
        private static partial IntPtr CreateEvent(
            IntPtr lpEventAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
            [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
            IntPtr lpName);

        [LibraryImport("mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int ActivateAudioInterfaceAsync(
            string deviceInterfacePath,
            in Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation? activationOperation);

        [GeneratedComClass]
        internal sealed partial class AudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            private readonly TaskCompletionSource<IntPtr> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<IntPtr> Task => _tcs.Task;

            private static bool IsActivationSuccessful(int hrMethod, int hrActivate, IntPtr unk) =>
                hrMethod >= 0 && hrActivate >= 0 && unk != IntPtr.Zero;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                try
                {
                    int hrMethod = activateOperation.GetActivateResult(out int hrActivate, out IntPtr pUnk);
                    if (IsActivationSuccessful(hrMethod, hrActivate, pUnk))
                    {
                        _tcs.TrySetResult(pUnk);
                    }
                    else
                    {
                        int failHr = hrMethod < 0 ? hrMethod : hrActivate;
                        _tcs.TrySetException(new COMException($"Error activando interfaz de audio: hrMethod=0x{hrMethod:X8}, hrActivate=0x{hrActivate:X8}", failHr));
                    }
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }

        #endregion
    }
}
