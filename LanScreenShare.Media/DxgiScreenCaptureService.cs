using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public sealed partial class DxgiScreenCaptureService : ScreenCaptureServiceBase
    {
        private volatile int _monitorIndex = 0;
        private volatile string? _deviceName;
        private volatile int _targetX = 0;
        private volatile int _targetY = 0;
        private volatile int _targetSourceWidth = 0;
        private volatile int _targetSourceHeight = 0;
        private readonly CursorSpriteRenderer _cursorRenderer = new(new Win32NativeCursorProvider());

        public event EventHandler<Exception>? CaptureFailed;
        public bool IncludeCursor { get; set; } = true;

        protected override int StopWaitTimeoutMs => 1000;
        protected override bool ClearSecureDesktopOnStop => false;
        protected override string LogTag => "DXGI";

        public DxgiScreenCaptureService()
            : base(defaultFps: 60, defaultQuality: 75, defaultCodec: "h264")
        {
        }

        protected override Task RunCaptureLoopAsync(CancellationToken token) =>
            RunDedicatedThreadLoopAsync("LssDxgiCapture", CaptureLoop, token);

        public int MonitorIndex
        {
            get => _monitorIndex;
            set => _monitorIndex = Math.Max(0, value);
        }

        public string? DeviceName
        {
            get => _deviceName;
            set => _deviceName = value;
        }

        public int TargetX
        {
            get => _targetX;
            set => _targetX = value;
        }

        public int TargetY
        {
            get => _targetY;
            set => _targetY = value;
        }

        public int TargetSourceWidth
        {
            get => _targetSourceWidth;
            set => _targetSourceWidth = Math.Max(0, value);
        }

        public int TargetSourceHeight
        {
            get => _targetSourceHeight;
            set => _targetSourceHeight = Math.Max(0, value);
        }

        private static IntPtr ConfigureCaptureThreadPriority()
        {
            IntPtr mmcssHandle = IntPtr.Zero;
            int mmcssTaskIndex = 0;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                try
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DXGI] No se pudo elevar prioridad del proceso: {ex.Message}");
                }

                mmcssHandle = AvSetMmThreadCharacteristics("DisplayPostProcessing", ref mmcssTaskIndex);
                if (mmcssHandle != IntPtr.Zero)
                {
                    Console.WriteLine("[DXGI] Hilo de captura registrado exitosamente en MMCSS (DisplayPostProcessing)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Advertencia configurando MMCSS/Prioridad de hilo: {ex.Message}");
            }
            return mmcssHandle;
        }

        private static void RevertMmcss(IntPtr mmcssHandle)
        {
            if (mmcssHandle == IntPtr.Zero) return;
            try
            {
                AvRevertMmThreadCharacteristics(mmcssHandle);
            }
            catch
            {
                // Ignore MMCSS revert failure on thread exit
            }
        }

        private (int Left, int Top, int Width, int Height) ResolveWinlogonFallbackBounds()
        {
            int width = _targetSourceWidth;
            int height = _targetSourceHeight;
            int left = _targetX;
            int top = _targetY;

            if (width <= 0 || height <= 0)
            {
                return WindowsScreenCaptureService.GetPrimaryScreenBounds();
            }

            return (left, top, width & ~1, height & ~1);
        }

        private void RunWinlogonGdiFallbackLoop(
            Stopwatch stopwatch,
            HostFpsTracker fpsTracker,
            CancellationToken token)
        {
            var bounds = ResolveWinlogonFallbackBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                token.WaitHandle.WaitOne(100);
                return;
            }

            OnSecureDesktopChanged(false);
            Console.WriteLine("[DXGI] Driver no permitió DuplicateOutput en Winlogon; usando respaldo GDI en Escritorio Seguro.");

            while (!token.IsCancellationRequested && WindowsDesktopManager.IsActiveDesktopSecure(out _))
            {
                if (!WindowsDesktopManager.TryAttachThreadToInputDesktopDetailed(out _, out _, out _))
                    break;

                stopwatch.Restart();
                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, _targetFps));
                CaptureAndDispatchWinlogonGdiFrame(bounds, fpsTracker);
                ApplyFramePacing(stopwatch, frameIntervalMs);
            }
        }

        private void CaptureAndDispatchWinlogonGdiFrame(
            (int Left, int Top, int Width, int Height) bounds,
            HostFpsTracker fpsTracker)
        {
            var procSw = Stopwatch.StartNew();
            using var dib = WindowsScreenCaptureService.CaptureScreenRegionToDib(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            if (dib == null)
                return;

            var options = new FrameEncodingOptions(_quality, ResolutionScale, bounds.Left, bounds.Top, _targetWidth, _targetHeight, ConsumeKeyframeRequest());
            var frame = EncodeFrameFromPointer(new RawFrameBuffer(dib.PBits, bounds.Width, bounds.Height, bounds.Width * 4), options);
            if (frame == null)
                return;

            procSw.Stop();
            frame.HostFrameTimeMs = (int)procSw.ElapsedMilliseconds;
            frame.HostFps = fpsTracker.RecordFrameAndGetFps();
            OnFrameCaptured(frame);
        }

        private void WaitForSecureDesktopExit(CancellationToken token)
        {
            Console.WriteLine("[DXGI] Escritorio seguro (UAC / Winlogon) detectado sin privilegios SYSTEM. Pausando captura...");
            OnSecureDesktopChanged(true);

            while (!token.IsCancellationRequested && WindowsDesktopManager.IsActiveDesktopSecure(out _))
            {
                Thread.Sleep(350);
            }

            if (!token.IsCancellationRequested)
            {
                Console.WriteLine("[DXGI] Retorno al escritorio normal detectado. Reanudando captura DXGI...");
                OnSecureDesktopChanged(false);
            }
        }

        private void CaptureLoop(CancellationToken token)
        {
            IntPtr mmcssHandle = ConfigureCaptureThreadPriority();
            var stopwatch = new Stopwatch();
            var fpsTracker = new HostFpsTracker();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    RunSingleDxgiIteration(stopwatch, fpsTracker, token);
                }
            }
            finally
            {
                RevertMmcss(mmcssHandle);
            }
        }

        private void RunSingleDxgiIteration(Stopwatch stopwatch, HostFpsTracker fpsTracker, CancellationToken token)
        {
            bool attached = WindowsDesktopManager.TryAttachThreadToInputDesktopDetailed(out _, out bool isSecure, out _);
            if (DesktopSwitchClassifier.ResolveDxgiDisposition(isSecure, attached) == DxgiDesktopDisposition.PauseUnauthorizedSecureDesktop)
            {
                WaitForSecureDesktopExit(token);
                return;
            }

            OnSecureDesktopChanged(false);
            try
            {
                using var session = CreateDxgiSession();
                RunSessionCaptureLoop(session, stopwatch, fpsTracker, token);
            }
            catch (Exception ex)
            {
                HandleCaptureLoopException(ex, stopwatch, fpsTracker, token);
            }
        }

        private void HandleCaptureLoopException(
            Exception ex,
            Stopwatch stopwatch,
            HostFpsTracker fpsTracker,
            CancellationToken token)
        {
            bool catchAttached = WindowsDesktopManager.TryAttachThreadToInputDesktopDetailed(out _, out bool catchSecure, out _);
            if (DesktopSwitchClassifier.ResolveDxgiDisposition(catchSecure, catchAttached) == DxgiDesktopDisposition.PauseUnauthorizedSecureDesktop)
            {
                WaitForSecureDesktopExit(token);
                return;
            }

            if (catchSecure && catchAttached)
            {
                RunWinlogonGdiFallbackLoop(stopwatch, fpsTracker, token);
                return;
            }

            Console.WriteLine($"[DXGI Capture] Error en bucle DXGI: {ex.Message}");
            CaptureFailed?.Invoke(this, ex);
            Thread.Sleep(500);
        }

        private bool ShouldEncodeFrame(
            FrameAcquireStatus status,
            bool hasCapturedAtLeastOneFrame,
            bool cursorMoved,
            long elapsedKeepAliveMs)
        {
            if (status == FrameAcquireStatus.NewFrame) return true;
            if (!hasCapturedAtLeastOneFrame) return false;
            if (cursorMoved || _forceKeyframeRequested) return true;
            return _codec == "h264" && elapsedKeepAliveMs >= 200;
        }

        private void RunSessionCaptureLoop(
            DxgiSessionResources session,
            Stopwatch stopwatch,
            HostFpsTracker fpsTracker,
            CancellationToken token)
        {
            bool hasCapturedAtLeastOneFrame = false;
            var cursorState = CursorTrackingState.Initial;
            var sessionBounds = new DxgiOutputRect
            {
                Left = session.Bounds.ScreenLeft,
                Top = session.Bounds.ScreenTop,
                Width = session.Bounds.ScreenWidth,
                Height = session.Bounds.ScreenHeight
            };
            var keepAliveStopwatch = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();

                int currentFps = _targetFps;
                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, currentFps));
                var frameProcStopwatch = Stopwatch.StartNew();

                var status = TryAcquireAndCopyFrame(session, frameIntervalMs, ref hasCapturedAtLeastOneFrame);
                if (status == FrameAcquireStatus.AccessLost)
                {
                    Console.WriteLine("[DXGI] Acceso perdido o cambio de escritorio. Reinicializando...");
                    bool attached = WindowsDesktopManager.TryAttachThreadToInputDesktopDetailed(out _, out bool isSecure, out _);
                    if (DesktopSwitchClassifier.ResolveDxgiDisposition(isSecure, attached) == DxgiDesktopDisposition.PauseUnauthorizedSecureDesktop)
                    {
                        WaitForSecureDesktopExit(token);
                    }
                    else
                    {
                        OnSecureDesktopChanged(false);
                    }
                    break;
                }

                bool cursorMoved = IncludeCursor && DetectCursorMovement(sessionBounds, ref cursorState);
                if (ShouldEncodeFrame(status, hasCapturedAtLeastOneFrame, cursorMoved, keepAliveStopwatch.ElapsedMilliseconds))
                {
                    keepAliveStopwatch.Restart();
                    ProcessAndDispatchFrame(session, frameProcStopwatch, fpsTracker);
                }

                ApplyFramePacing(stopwatch, frameIntervalMs);
            }
        }

        private enum FrameAcquireStatus
        {
            NoFrame,
            NewFrame,
            AccessLost
        }

        private static FrameAcquireStatus TryAcquireAndCopyFrame(
            DxgiSessionResources session,
            int frameIntervalMs,
            ref bool hasCapturedAtLeastOneFrame)
        {
            uint timeoutMs = (uint)Math.Max(5, Math.Min(10, frameIntervalMs / 2));
            var acquireResult = session.Duplication!.AcquireNextFrame(timeoutMs, out _, out IDXGIResource? desktopResource);

            if (acquireResult.Success && desktopResource != null)
            {
                try
                {
                    using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    session.Context!.CopyResource(session.StagingTexture!, desktopTexture);
                    hasCapturedAtLeastOneFrame = true;
                    return FrameAcquireStatus.NewFrame;
                }
                finally
                {
                    session.Duplication.ReleaseFrame();
                    desktopResource.Dispose();
                }
            }

            if (acquireResult.Code == Vortice.DXGI.ResultCode.AccessLost.Code ||
                acquireResult.Code == Vortice.DXGI.ResultCode.AccessDenied.Code)
            {
                return FrameAcquireStatus.AccessLost;
            }

            return FrameAcquireStatus.NoFrame;
        }

        private static CursorSample TryGetCurrentCursorSample()
        {
            CursorInfo curInfo = new() { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref curInfo) || curInfo.flags != CURSOR_SHOWING)
            {
                return new CursorSample { IsVisible = false };
            }

            return new CursorSample
            {
                IsVisible = true,
                X = curInfo.ptScreenPos.x,
                Y = curInfo.ptScreenPos.y,
                Handle = curInfo.hCursor
            };
        }

        private static bool DetectCursorMovement(in DxgiOutputRect bounds, ref CursorTrackingState state)
        {
            try
            {
                var sample = TryGetCurrentCursorSample();
                return DxgiCursorTracker.ShouldRedrawCursor(sample, bounds, ref state);
            }
            catch
            {
                // Ignorar fallos de consulta de cursor
            }

            return false;
        }

        private void ProcessAndDispatchFrame(
            DxgiSessionResources session,
            Stopwatch frameProcStopwatch,
            HostFpsTracker fpsTracker)
        {
            bool forceKeyframe = _forceKeyframeRequested;
            _forceKeyframeRequested = false;

            var mapped = session.Context!.Map(session.StagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var buffer = new RawFrameBuffer(
                    mapped.DataPointer,
                    session.Bounds.ScreenWidth,
                    session.Bounds.ScreenHeight,
                    (int)mapped.RowPitch);

                var options = new FrameEncodingOptions(
                    _quality,
                    ResolutionScale,
                    session.Bounds.ScreenLeft,
                    session.Bounds.ScreenTop,
                    _targetWidth,
                    _targetHeight,
                    forceKeyframe);

                var frame = EncodeFrameFromPointer(buffer, options);
                if (frame != null)
                {
                    frameProcStopwatch.Stop();
                    frame.HostFrameTimeMs = (int)frameProcStopwatch.ElapsedMilliseconds;
                    frame.HostFps = fpsTracker.RecordFrameAndGetFps();

                    OnFrameCaptured(frame);
                }
            }
            finally
            {
                session.Context.Unmap(session.StagingTexture!, 0);
            }
        }

        private static void ApplyFramePacing(Stopwatch stopwatch, int frameIntervalMs)
        {
            int elapsed = (int)stopwatch.ElapsedMilliseconds;
            int delay = frameIntervalMs - elapsed;
            if (delay > 0)
            {
                Thread.Sleep(delay);
            }
        }

        public readonly record struct RawFrameBuffer(IntPtr DataPointer, int Width, int Height, int RowPitch);

        public readonly record struct DesktopBounds(int ScreenWidth, int ScreenHeight, int ScreenLeft, int ScreenTop);

        private sealed class HostFpsTracker
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private int _frameCount;
            private int _currentFps;

            public int RecordFrameAndGetFps()
            {
                _frameCount++;
                if (_stopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = (int)Math.Round(_frameCount * 1000.0 / _stopwatch.ElapsedMilliseconds);
                    _frameCount = 0;
                    _stopwatch.Restart();
                }
                return _currentFps > 0 ? _currentFps : _frameCount;
            }
        }

        private sealed class DxgiSessionResources : IDisposable
        {
            public IDXGIFactory1? Factory { get; init; }
            public IDXGIAdapter1? Adapter { get; init; }
            public IDXGIOutput? Output { get; init; }
            public IDXGIOutput1? Output1 { get; init; }
            public ID3D11Device? Device { get; init; }
            public ID3D11DeviceContext? Context { get; init; }
            public IDXGIOutputDuplication? Duplication { get; init; }
            public ID3D11Texture2D? StagingTexture { get; init; }
            public DesktopBounds Bounds { get; init; }

            public void Dispose()
            {
                StagingTexture?.Dispose();
                Duplication?.Dispose();
                Output1?.Dispose();
                Output?.Dispose();
                Context?.Dispose();
                Device?.Dispose();
                Adapter?.Dispose();
                Factory?.Dispose();
            }
        }

        private DxgiSessionResources CreateDxgiSession()
        {
            if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure || factory == null)
            {
                throw new InvalidOperationException("No se pudo crear IDXGIFactory1");
            }

            FindTargetAdapterAndOutput(factory, out IDXGIAdapter1? adapter, out IDXGIOutput? output);
            CreateD3D11Device(adapter, out ID3D11Device? device, out ID3D11DeviceContext? context);
            var output1 = output.QueryInterface<IDXGIOutput1>();

            var bounds = output.Description.DesktopCoordinates;
            int screenWidth = bounds.Right - bounds.Left;
            int screenHeight = bounds.Bottom - bounds.Top;

            var duplication = output1.DuplicateOutput(device);

            var stagingDesc = new Texture2DDescription
            {
                Width = (uint)screenWidth,
                Height = (uint)screenHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            var stagingTexture = device.CreateTexture2D(stagingDesc);

            return new DxgiSessionResources
            {
                Factory = factory,
                Adapter = adapter,
                Output = output,
                Output1 = output1,
                Device = device,
                Context = context,
                Duplication = duplication,
                StagingTexture = stagingTexture,
                Bounds = new DesktopBounds(screenWidth, screenHeight, bounds.Left, bounds.Top)
            };
        }

        private readonly record struct FrameEncodingOptions(
            int Quality,
            double Scale,
            int ScreenLeft,
            int ScreenTop,
            int CustomTargetWidth = 0,
            int CustomTargetHeight = 0,
            bool ForceKeyframe = false);

        private readonly record struct DxgiOutputEntry(
            IDXGIAdapter1 Adapter,
            IDXGIOutput Output,
            DxgiOutputDescriptor Descriptor);

        private static void CollectAdapterOutputs(
            IDXGIAdapter1 adapter,
            int adapterIndex,
            List<DxgiOutputEntry> entries)
        {
            for (uint oIdx = 0; adapter.EnumOutputs(oIdx, out var curOutput).Success && curOutput != null; oIdx++)
            {
                string devName = curOutput.Description.DeviceName.TrimEnd('\0');
                var coords = curOutput.Description.DesktopCoordinates;
                var descriptor = new DxgiOutputDescriptor
                {
                    AdapterIndex = adapterIndex,
                    OutputIndex = (int)oIdx,
                    DeviceName = devName,
                    Bounds = new DxgiOutputRect
                    {
                        Left = coords.Left,
                        Top = coords.Top,
                        Width = coords.Right - coords.Left,
                        Height = coords.Bottom - coords.Top
                    }
                };
                entries.Add(new DxgiOutputEntry(adapter, curOutput, descriptor));
            }
        }

        private static void DisposeUnusedDxgiResources(
            List<IDXGIAdapter1> adapters,
            List<DxgiOutputEntry> entries,
            DxgiOutputEntry? keepEntry = null)
        {
            foreach (var entry in entries.Where(entry => !ReferenceEquals(entry.Output, keepEntry?.Output)))
            {
                entry.Output.Dispose();
            }

            foreach (var adapter in adapters.Where(adapter => !ReferenceEquals(adapter, keepEntry?.Adapter)))
            {
                adapter.Dispose();
            }
        }

        private static (IDXGIAdapter1 adapter, IDXGIOutput output) GetFallbackPrimaryAdapterAndOutput(IDXGIFactory1 factory)
        {
            if (factory.EnumAdapters1(0, out var primaryAdapter).Failure || primaryAdapter == null)
            {
                throw new InvalidOperationException("No se pudo obtener el adaptador gráfico primario (GPU 0)");
            }

            if (primaryAdapter.EnumOutputs(0, out var primaryOutput).Failure || primaryOutput == null)
            {
                primaryAdapter.Dispose();
                throw new InvalidOperationException("No se pudo obtener la salida de pantalla principal (Monitor 0)");
            }

            return (primaryAdapter, primaryOutput);
        }

        private void FindTargetAdapterAndOutput(
            IDXGIFactory1 factory,
            out IDXGIAdapter1 adapter,
            out IDXGIOutput output)
        {
            var adapters = new List<IDXGIAdapter1>();
            var entries = new List<DxgiOutputEntry>();

            for (uint aIdx = 0; factory.EnumAdapters1(aIdx, out var curAdapter).Success && curAdapter != null; aIdx++)
            {
                adapters.Add(curAdapter);
                CollectAdapterOutputs(curAdapter, (int)aIdx, entries);
            }

            var descriptors = new DxgiOutputDescriptor[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                descriptors[i] = entries[i].Descriptor;
            }

            var target = new DxgiOutputTarget(_deviceName, _monitorIndex, new DxgiOutputRect
            {
                Left = _targetX,
                Top = _targetY,
                Width = _targetSourceWidth,
                Height = _targetSourceHeight
            });

            int selectedIndex = DxgiOutputSelector.SelectBestIndex(descriptors, target);
            if (selectedIndex >= 0)
            {
                var winner = entries[selectedIndex];
                adapter = winner.Adapter;
                output = winner.Output;
                DisposeUnusedDxgiResources(adapters, entries, winner);
                return;
            }

            DisposeUnusedDxgiResources(adapters, entries);
            (adapter, output) = GetFallbackPrimaryAdapterAndOutput(factory);
        }

        private static void CreateD3D11Device(
            IDXGIAdapter1 adapter,
            out ID3D11Device device,
            out ID3D11DeviceContext context)
        {
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            var d3dResult = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out ID3D11Device? dev,
                out ID3D11DeviceContext? ctx);

            if (d3dResult.Failure)
            {
                throw new InvalidOperationException($"No se pudo crear el dispositivo Direct3D 11: {d3dResult}");
            }
            if (dev == null || ctx == null)
            {
                throw new InvalidOperationException("Direct3D 11 devolvió un dispositivo o contexto nulo.");
            }

            device = dev;
            context = ctx;

            try
            {
                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                if (dxgiDevice != null)
                {
                    dxgiDevice.SetGPUThreadPriority(7);
                    Console.WriteLine("[DXGI] GPU Thread Priority establecido al nivel máximo (7)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Advertencia configurando GPU Thread Priority: {ex.Message}");
            }
        }

        private static (SKBitmap bitmapToEncode, SKBitmap? disposableBitmap, int finalWidth, int finalHeight) PrepareEncodingBitmap(
            SKBitmap skBitmap,
            in RawFrameBuffer buffer,
            in FrameEncodingOptions options)
        {
            var (bitmapToEncode, resizedBitmap, finalWidth, finalHeight) = PrepareResizedBitmap(
                skBitmap,
                new BitmapResizeOptions(buffer.Width, buffer.Height, options.Scale, options.CustomTargetWidth, options.CustomTargetHeight));

            if (resizedBitmap != null)
            {
                return (bitmapToEncode, resizedBitmap, finalWidth, finalHeight);
            }

            var copied = skBitmap.Copy();
            return (copied ?? skBitmap, copied, buffer.Width, buffer.Height);
        }

        private CapturedFrame? EncodeFrameFromPointer(
            in RawFrameBuffer buffer,
            in FrameEncodingOptions options)
        {
            try
            {
                var imageInfo = new SKImageInfo(buffer.Width, buffer.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBitmap = new SKBitmap();
                skBitmap.InstallPixels(imageInfo, buffer.DataPointer, buffer.RowPitch);

                var (bitmapToEncode, resizedBitmap, finalWidth, finalHeight) = PrepareEncodingBitmap(skBitmap, buffer, options);

                try
                {
                    float scaleX = (float)finalWidth / Math.Max(1, buffer.Width);
                    float scaleY = (float)finalHeight / Math.Max(1, buffer.Height);
                    DrawCursorOnBitmap(bitmapToEncode, new CursorRenderTarget
                    {
                        ScreenLeft = options.ScreenLeft,
                        ScreenTop = options.ScreenTop,
                        ScaleX = scaleX,
                        ScaleY = scaleY
                    });

                    string currentCodec = _codec;
                    byte[]? encodedBytes = currentCodec == "h264"
                        ? EncodeH264(bitmapToEncode, new H264FrameRequest(finalWidth, finalHeight, options.ForceKeyframe))
                        : EncodeJpeg(bitmapToEncode, options.Quality);

                    if (encodedBytes == null || encodedBytes.Length == 0) return null;

                    return new CapturedFrame
                    {
                        Data = encodedBytes,
                        Width = finalWidth,
                        Height = finalHeight,
                        Format = currentCodec,
                        Timestamp = DateTime.UtcNow.Ticks
                    };
                }
                finally
                {
                    resizedBitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Error codificando fotograma: {ex.Message}");
                return null;
            }
        }

        private void DrawCursorOnBitmap(SKBitmap bitmap, in CursorRenderTarget target)
        {
            if (!IncludeCursor || Win32NativeCursorProvider.AreMouseTrailsActive())
                return;

            try
            {
                var sample = TryGetCurrentCursorSample();
                if (sample.IsVisible)
                {
                    _cursorRenderer.DrawCursor(bitmap, sample, target);
                }
            }
            catch
            {
                // Ignorar error de cursor
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _cursorRenderer.Dispose();
            }
        }
        #region Win32 Cursor Structs & Imports

        private const int CURSOR_SHOWING = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public NativePoint ptScreenPos;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorInfo(ref CursorInfo pci);

        [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [LibraryImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

        private sealed partial class Win32NativeCursorProvider : INativeCursorProvider
        {
            private const int DI_NORMAL = 0x0003;
            private const int SM_CXCURSOR = 13;
            private const int SM_CYCURSOR = 14;
            private const int DIB_RGB_COLORS = 0;
            private const int BI_RGB = 0;
            private const uint SPI_GETMOUSETRAILS = 0x005E;

            public static bool AreMouseTrailsActive()
            {
                uint trails = 0;
                return SystemParametersInfo(SPI_GETMOUSETRAILS, 0, ref trails, 0) && trails > 1;
            }

            public bool TryCaptureCursor(IntPtr cursorHandle, out NativeCursorCapture capture)
            {
                capture = default;
                if (!GetIconInfo(cursorHandle, out var iconInfo))
                    return false;

                try
                {
                    var size = ResolveCursorDimensions(iconInfo);
                    var (onBlack, onWhite) = RenderDualPass(cursorHandle, size);
                    if (onBlack == null || onWhite == null)
                        return false;

                    capture = new NativeCursorCapture(size, (iconInfo.xHotspot, iconInfo.yHotspot), (onBlack, onWhite));
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                    if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                }
            }

            private static (int Width, int Height) ResolveCursorDimensions(in IconInfo iconInfo)
            {
                IntPtr hBitmap = iconInfo.hbmColor != IntPtr.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
                if (hBitmap != IntPtr.Zero && TryGetBitmapSize(hBitmap, out int bmpW, out int bmpH))
                {
                    int height = iconInfo.hbmColor != IntPtr.Zero ? bmpH : bmpH / 2;
                    if (bmpW > 0 && height > 0)
                        return (Math.Min(bmpW, 256), Math.Min(height, 256));
                }

                int sysW = Math.Clamp(GetSystemMetrics(SM_CXCURSOR), 16, 128);
                int sysH = Math.Clamp(GetSystemMetrics(SM_CYCURSOR), 16, 128);
                return (sysW, sysH);
            }

            private static bool TryGetBitmapSize(IntPtr hBitmap, out int width, out int height)
            {
                width = 0;
                height = 0;
                var bmp = new GdiBitmap();
                if (GetObjectW(hBitmap, Marshal.SizeOf<GdiBitmap>(), ref bmp) == 0)
                    return false;

                width = bmp.bmWidth;
                height = Math.Abs(bmp.bmHeight);
                return width > 0 && height > 0;
            }

            private static (byte[]? OnBlack, byte[]? OnWhite) RenderDualPass(IntPtr hCursor, (int Width, int Height) size)
            {
                IntPtr hdcMem = CreateCompatibleDC(IntPtr.Zero);
                if (hdcMem == IntPtr.Zero)
                    return (null, null);

                IntPtr hDib = IntPtr.Zero;
                IntPtr hOld = IntPtr.Zero;
                try
                {
                    hDib = CreateTopDownDib32(hdcMem, size, out IntPtr pBits);
                    if (hDib == IntPtr.Zero || pBits == IntPtr.Zero)
                        return (null, null);

                    hOld = SelectObject(hdcMem, hDib);
                    var ctx = new DualPassContext
                    {
                        Hdc = hdcMem,
                        BitsPtr = pBits,
                        CursorHandle = hCursor,
                        Width = size.Width,
                        Height = size.Height
                    };
                    return (CapturePass(ctx, fillWhite: false), CapturePass(ctx, fillWhite: true));
                }
                finally
                {
                    if (hOld != IntPtr.Zero) SelectObject(hdcMem, hOld);
                    if (hDib != IntPtr.Zero) DeleteObject(hDib);
                    DeleteDC(hdcMem);
                }
            }

            private readonly record struct DualPassContext
            {
                public IntPtr Hdc { get; init; }
                public IntPtr BitsPtr { get; init; }
                public IntPtr CursorHandle { get; init; }
                public int Width { get; init; }
                public int Height { get; init; }
                public int ByteCount => Width * Height * 4;
            }

            private static byte[] CapturePass(in DualPassContext ctx, bool fillWhite)
            {
                int byteCount = ctx.ByteCount;
                byte fill = fillWhite ? (byte)0xFF : (byte)0x00;
                byte[] buffer = new byte[byteCount];
                Array.Fill(buffer, fill);
                if (!fillWhite)
                {
                    for (int i = 3; i < byteCount; i += 4)
                        buffer[i] = 0xFF;
                }

                Marshal.Copy(buffer, 0, ctx.BitsPtr, byteCount);
                DrawIconEx(ctx.Hdc, 0, 0, ctx.CursorHandle, ctx.Width, ctx.Height, 0, IntPtr.Zero, DI_NORMAL);
                Marshal.Copy(ctx.BitsPtr, buffer, 0, byteCount);
                return buffer;
            }

            private static IntPtr CreateTopDownDib32(IntPtr hdc, (int Width, int Height) size, out IntPtr pBits)
            {
                var bmi = new BitmapInfo
                {
                    bmiHeader = new BitmapInfoHeader
                    {
                        biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        biWidth = size.Width,
                        biHeight = -size.Height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB
                    }
                };
                return CreateDIBSection(hdc, ref bmi, DIB_RGB_COLORS, out pBits, IntPtr.Zero, 0);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IconInfo
            {
                public int fIcon;
                public int xHotspot;
                public int yHotspot;
                public IntPtr hbmMask;
                public IntPtr hbmColor;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct GdiBitmap
            {
                public int bmType;
                public int bmWidth;
                public int bmHeight;
                public int bmWidthBytes;
                public ushort bmPlanes;
                public ushort bmBitsPixel;
                public IntPtr bmBits;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BitmapInfoHeader
            {
                public uint biSize;
                public int biWidth;
                public int biHeight;
                public ushort biPlanes;
                public ushort biBitCount;
                public uint biCompression;
                public uint biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public uint biClrUsed;
                public uint biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BitmapInfo
            {
                public BitmapInfoHeader bmiHeader;
                public uint bmiColors;
            }

            [LibraryImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

            [LibraryImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool DrawIconEx(
                IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
                int cxWidth, int cyWidth, uint istepIfAniCur,
                IntPtr hbrFlickerFreeDraw, uint diFlags);

            [LibraryImport("user32.dll")]
            private static partial int GetSystemMetrics(int nIndex);

            [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

            [LibraryImport("gdi32.dll")]
            private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

            [LibraryImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool DeleteDC(IntPtr hdc);

            [LibraryImport("gdi32.dll")]
            private static partial IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

            [LibraryImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool DeleteObject(IntPtr hObject);

            [LibraryImport("gdi32.dll", SetLastError = true)]
            private static partial int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref GdiBitmap lpvObject);

            [LibraryImport("gdi32.dll", SetLastError = true)]
            private static partial IntPtr CreateDIBSection(
                IntPtr hdc, ref BitmapInfo pbmi, uint usage,
                out IntPtr ppvBits, IntPtr hSection, uint offset);
        }

        #endregion
    }
}
