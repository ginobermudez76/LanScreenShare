using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public sealed class WasapiMicrophoneCaptureService : IAudioCaptureService
    {
        private WasapiCapture? _capture;
        private bool _disposed;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing => _capture != null;

        public void StartCapture(int processId) => StartCapture();

        private static WasapiCapture CreateDefaultCaptureInstance()
        {
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                                 ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (defaultDevice != null)
                {
                    return new WasapiCapture(defaultDevice);
                }
            }
            catch
            {
                // Fallback a constructor por defecto
            }

            return new WasapiCapture();
        }

        private void AttachCaptureHandlers(WasapiCapture captureInstance, WaveFormat waveFormat, short formatEncoding)
        {
            captureInstance.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                    return;

                byte[] buffer = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

                var chunk = new CapturedAudioChunk
                {
                    Data = buffer,
                    SampleRate = waveFormat.SampleRate,
                    Channels = waveFormat.Channels,
                    BitsPerSample = waveFormat.BitsPerSample,
                    AudioFormat = formatEncoding,
                    Source = AudioSourceType.Microphone,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                AudioCaptured?.Invoke(this, chunk);
            };

            captureInstance.RecordingStopped += (_, _) =>
            {
                try
                {
                    captureInstance.Dispose();
                }
                catch
                {
                    // Ignorar error al liberar instancia detenida
                }

                if (ReferenceEquals(_capture, captureInstance))
                {
                    _capture = null;
                }
            };
        }

        public void StartCapture()
        {
            if (IsCapturing)
                return;

            try
            {
                Task.Run(() =>
                {
                    var captureInstance = CreateDefaultCaptureInstance();
                    var waveFormat = captureInstance.WaveFormat;
                    short formatEncoding = (short)(waveFormat.BitsPerSample == 32 ? 3 : (short)waveFormat.Encoding);

                    AttachCaptureHandlers(captureInstance, waveFormat, formatEncoding);

                    _capture = captureInstance;
                    captureInstance.StartRecording();
                    Console.WriteLine($"[WASAPI Mic] Captura de micrófono iniciada ({waveFormat.SampleRate}Hz, {waveFormat.Channels} canales, {waveFormat.BitsPerSample} bits)");
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Mic] Error iniciando captura de micrófono: {ex.Message}");
                _capture?.Dispose();
                _capture = null;
            }
        }

        public void StopCapture()
        {
            if (_capture != null)
            {
                var captureInstance = _capture;
                _capture = null;

                try
                {
                    captureInstance.StopRecording();
                }
                catch
                {
                    // Ignore StopRecording errors if microphone was disconnected
                }

                try
                {
                    captureInstance.Dispose();
                }
                catch
                {
                    // Ignore disposal errors on microphone capture instance
                }

                Console.WriteLine("[WASAPI Mic] Captura de micrófono detenida.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StopCapture();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
