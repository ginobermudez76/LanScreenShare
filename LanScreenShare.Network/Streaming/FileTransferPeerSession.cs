using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    internal sealed class TransferProgressState
    {
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
        public long LastReportBytes;
        public long LastReportTimeMs;
    }

    internal sealed class InboundFileTransfer : IDisposable
    {
        public string TransferId { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public long ReceivedBytes;
        public FileStream? FileStream { get; set; }
        public TransferProgressState ProgressState { get; } = new();

        public void Dispose()
        {
            FileStream?.Dispose();
            FileStream = null;
        }
    }

    internal readonly record struct FileTransferCallbacks(
        Action<FileTransferProgress>? OnProgress,
        Action<FileTransferCompleted>? OnCompleted,
        Action<FileTransferError>? OnError);

    internal sealed class FileTransferPeerSession : IDisposable
    {
        private readonly record struct TransferDescriptor(
            string TransferId,
            string FileName,
            long TotalBytes,
            bool IsUpload);

        private readonly Func<string> _getDownloadDirectory;
        private readonly string _logTag;
        private readonly FileTransferCallbacks _callbacks;

        public Stream Stream { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, InboundFileTransfer> InboundTransfers { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<bool>> OutboundAcceptSignals { get; } = new();

        public FileTransferPeerSession(
            Stream stream,
            Func<string> getDownloadDirectory,
            string logTag,
            FileTransferCallbacks callbacks)
        {
            Stream = stream;
            _getDownloadDirectory = getDownloadDirectory;
            _logTag = logTag;
            _callbacks = callbacks;
        }

        public async Task ProcessIncomingLoopAsync(CancellationToken ct)
        {
            byte[] headerBuffer = new byte[FileTransferPacket.HeaderSize];

            while (!ct.IsCancellationRequested)
            {
                await FileTransferPacket.ReadExactBytesAsync(Stream, headerBuffer, ct);

                uint magic = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(0, 4));
                if (magic != FileTransferPacket.Magic)
                {
                    throw new InvalidDataException($"Magic de paquete inválido: 0x{magic:X8}");
                }

                var type = (FileTransferPacketType)headerBuffer[4];
                uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(5, 4));

                if (payloadLength > FileTransferPacket.MaxPayloadSize && type == FileTransferPacketType.Chunk)
                {
                    throw new InvalidDataException($"Carga útil de fragmento excede el máximo permitido ({payloadLength} bytes).");
                }

                await DispatchPacketAsync(type, payloadLength, ct);
            }
        }

        private static readonly Dictionary<FileTransferPacketType, Func<FileTransferPeerSession, uint, CancellationToken, Task>> PacketHandlers = new()
        {
            [FileTransferPacketType.Chunk] = (session, length, ct) => session.HandleChunkPacketAsync(length, ct),
            [FileTransferPacketType.Offer] = (session, length, ct) => session.HandleOfferPacketAsync(length, ct),
            [FileTransferPacketType.Accept] = (session, length, ct) => session.HandleSignalPacketAsync(length, accepted: true, ct),
            [FileTransferPacketType.Reject] = (session, length, ct) => session.HandleSignalPacketAsync(length, accepted: false, ct),
            [FileTransferPacketType.Complete] = (session, length, ct) => session.HandleCompletePacketAsync(length, ct),
            [FileTransferPacketType.Cancel] = (session, length, ct) => session.HandleCancelPacketAsync(length, ct)
        };

        private Task DispatchPacketAsync(FileTransferPacketType type, uint payloadLength, CancellationToken ct) =>
            PacketHandlers.TryGetValue(type, out var handler)
                ? handler(this, payloadLength, ct)
                : Task.CompletedTask;

        private static async Task<T?> ReadJsonDtoAsync<T>(Stream stream, uint payloadLength, CancellationToken ct)
        {
            byte[] jsonBytes = new byte[payloadLength];
            await FileTransferPacket.ReadExactBytesAsync(stream, jsonBytes, ct);
            return JsonSerializer.Deserialize<T>(jsonBytes);
        }

        private async Task HandleOfferPacketAsync(uint payloadLength, CancellationToken ct)
        {
            var offer = await ReadJsonDtoAsync<FileTransferOfferDto>(Stream, payloadLength, ct);
            if (offer != null && !string.IsNullOrWhiteSpace(offer.TransferId))
            {
                await HandleInboundOfferAsync(offer, ct);
            }
        }

        private async Task HandleSignalPacketAsync(uint payloadLength, bool accepted, CancellationToken ct)
        {
            string? transferId = accepted
                ? (await ReadJsonDtoAsync<FileTransferAcceptDto>(Stream, payloadLength, ct))?.TransferId
                : (await ReadJsonDtoAsync<FileTransferRejectDto>(Stream, payloadLength, ct))?.TransferId;

            if (transferId != null && OutboundAcceptSignals.TryGetValue(transferId, out var tcs))
            {
                tcs.TrySetResult(accepted);
            }
        }

        private async Task HandleChunkPacketAsync(uint payloadLength, CancellationToken ct)
        {
            byte[] metaBuffer = new byte[24];
            await FileTransferPacket.ReadExactBytesAsync(Stream, metaBuffer, ct);

            Guid transferId = new(metaBuffer.AsSpan(0, 16));
            long offset = BinaryPrimitives.ReadInt64BigEndian(metaBuffer.AsSpan(16, 8));
            int dataLength = (int)(payloadLength - 24);

            byte[] chunkData = ArrayPool<byte>.Shared.Rent(dataLength);
            try
            {
                await FileTransferPacket.ReadExactBytesAsync(Stream, chunkData.AsMemory(0, dataLength), ct);

                string idStr = transferId.ToString("D");
                if (InboundTransfers.TryGetValue(idStr, out var inbound))
                {
                    await HandleInboundChunkAsync(inbound, offset, chunkData.AsMemory(0, dataLength), ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkData);
            }
        }

        private async Task HandleCompletePacketAsync(uint payloadLength, CancellationToken ct)
        {
            var complete = await ReadJsonDtoAsync<FileTransferCompleteDto>(Stream, payloadLength, ct);
            if (complete != null && InboundTransfers.TryRemove(complete.TransferId, out var inbound))
            {
                inbound.Dispose();
                _callbacks.OnCompleted?.Invoke(new FileTransferCompleted
                {
                    TransferId = inbound.TransferId,
                    FileName = inbound.OriginalFileName,
                    FilePath = inbound.DestinationPath,
                    FileSize = inbound.FileSize,
                    IsUpload = false
                });
                Console.WriteLine($"[{_logTag}] Archivo recibido con éxito: {inbound.DestinationPath} ({inbound.FileSize} bytes)");
            }
        }

        private async Task HandleCancelPacketAsync(uint payloadLength, CancellationToken ct)
        {
            var cancel = await ReadJsonDtoAsync<FileTransferCancelDto>(Stream, payloadLength, ct);
            if (cancel != null && InboundTransfers.TryRemove(cancel.TransferId, out var inbound))
            {
                inbound.Dispose();
                TryDeletePartialFile(inbound.DestinationPath);

                _callbacks.OnError?.Invoke(new FileTransferError
                {
                    TransferId = inbound.TransferId,
                    FileName = inbound.OriginalFileName,
                    ErrorMessage = cancel.Reason ?? "Transferencia cancelada por el remitente.",
                    IsUpload = false
                });
            }
        }

        private static void TryDeletePartialFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore partial file deletion failure
            }
        }

        private async Task HandleInboundOfferAsync(FileTransferOfferDto offer, CancellationToken ct)
        {
            string destination = TcpFileTransferServer.GetUniqueDestinationPath(_getDownloadDirectory(), offer.FileName);
            var inbound = new InboundFileTransfer
            {
                TransferId = offer.TransferId,
                OriginalFileName = offer.FileName,
                DestinationPath = destination,
                FileSize = offer.FileSize,
                FileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true)
            };

            InboundTransfers[offer.TransferId] = inbound;

            await SendLockedAsync(() => FileTransferPacket.WriteAcceptAsync(Stream, offer.TransferId, ct), ct);

            _callbacks.OnProgress?.Invoke(new FileTransferProgress
            {
                TransferId = offer.TransferId,
                FileName = offer.FileName,
                BytesTransferred = 0,
                TotalBytes = offer.FileSize,
                SpeedBytesPerSec = 0,
                IsUpload = false
            });
        }

        private async Task HandleInboundChunkAsync(InboundFileTransfer inbound, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (inbound.FileStream == null) return;

            if (inbound.FileStream.Position != offset)
            {
                inbound.FileStream.Seek(offset, SeekOrigin.Begin);
            }

            await inbound.FileStream.WriteAsync(data, ct);
            long totalReceived = Interlocked.Add(ref inbound.ReceivedBytes, data.Length);

            var info = new TransferDescriptor(inbound.TransferId, inbound.OriginalFileName, inbound.FileSize, IsUpload: false);
            ReportProgressIfDue(info, totalReceived, inbound.ProgressState);
        }

        public async Task<bool> SendFileAsync(string transferId, FileInfo fileInfo, CancellationToken ct)
        {
            try
            {
                if (!await WaitOfferAcceptedAsync(transferId, fileInfo, ct))
                {
                    _callbacks.OnError?.Invoke(new FileTransferError
                    {
                        TransferId = transferId,
                        FileName = fileInfo.Name,
                        ErrorMessage = "El receptor rechazó la transferencia.",
                        IsUpload = true
                    });
                    return false;
                }

                await TransmitFileChunksAsync(transferId, fileInfo, ct);
                return true;
            }
            catch (Exception ex)
            {
                await TrySendCancelAsync(transferId, ex.Message);

                _callbacks.OnError?.Invoke(new FileTransferError
                {
                    TransferId = transferId,
                    FileName = fileInfo.Name,
                    ErrorMessage = ex.Message,
                    IsUpload = true
                });
                return false;
            }
            finally
            {
                OutboundAcceptSignals.TryRemove(transferId, out _);
            }
        }

        private async Task<bool> WaitOfferAcceptedAsync(string transferId, FileInfo fileInfo, CancellationToken ct)
        {
            var acceptTcs = new TaskCompletionSource<bool>();
            OutboundAcceptSignals[transferId] = acceptTcs;

            var offerDto = new FileTransferOfferDto
            {
                TransferId = transferId,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length
            };
            await SendLockedAsync(() => FileTransferPacket.WriteOfferAsync(Stream, offerDto, ct), ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => acceptTcs.TrySetResult(false));

            return await acceptTcs.Task;
        }

        private async Task TransmitFileChunksAsync(string transferId, FileInfo fileInfo, CancellationToken ct)
        {
            Guid transferGuid = Guid.Parse(transferId);
            using var fs = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferPacket.DefaultChunkSize, true);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileTransferPacket.DefaultChunkSize);
            long totalSent = 0;
            var progressState = new TransferProgressState();
            var info = new TransferDescriptor(transferId, fileInfo.Name, fileInfo.Length, IsUpload: true);

            try
            {
                int read;
                while ((read = await fs.ReadAsync(buffer.AsMemory(0, FileTransferPacket.DefaultChunkSize), ct)) > 0)
                {
                    var chunkPayload = new FileChunkPayload(transferGuid, totalSent, buffer.AsMemory(0, read));
                    await SendLockedAsync(() => FileTransferPacket.WriteChunkAsync(Stream, chunkPayload, ct), ct);
                    totalSent += read;
                    ReportProgressIfDue(info, totalSent, progressState);
                }

                await SendLockedAsync(() => FileTransferPacket.WriteCompleteAsync(Stream, transferId, ct), ct);

                _callbacks.OnCompleted?.Invoke(new FileTransferCompleted
                {
                    TransferId = transferId,
                    FileName = fileInfo.Name,
                    FilePath = fileInfo.FullName,
                    FileSize = fileInfo.Length,
                    IsUpload = true
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void ReportProgressIfDue(in TransferDescriptor info, long currentBytes, TransferProgressState state)
        {
            long nowMs = state.Stopwatch.ElapsedMilliseconds;
            if (nowMs - state.LastReportTimeMs < 200 && currentBytes < info.TotalBytes)
            {
                return;
            }

            double elapsedSec = Math.Max(0.001, (nowMs - state.LastReportTimeMs) / 1000.0);
            double speed = (currentBytes - state.LastReportBytes) / elapsedSec;

            state.LastReportTimeMs = nowMs;
            state.LastReportBytes = currentBytes;

            _callbacks.OnProgress?.Invoke(new FileTransferProgress
            {
                TransferId = info.TransferId,
                FileName = info.FileName,
                BytesTransferred = currentBytes,
                TotalBytes = info.TotalBytes,
                SpeedBytesPerSec = speed,
                IsUpload = info.IsUpload
            });
        }

        private async Task SendLockedAsync(Func<Task> action, CancellationToken ct)
        {
            await SendLock.WaitAsync(ct);
            try
            {
                await action();
            }
            finally
            {
                SendLock.Release();
            }
        }

        private async Task TrySendCancelAsync(string transferId, string reason)
        {
            try
            {
                await SendLockedAsync(
                    () => FileTransferPacket.WriteCancelAsync(Stream, transferId, reason, CancellationToken.None),
                    CancellationToken.None);
            }
            catch
            {
                // Ignore network failure while sending cancel packet
            }
        }

        public void Dispose()
        {
            foreach (var inbound in InboundTransfers.Values)
            {
                inbound.Dispose();
            }
            InboundTransfers.Clear();
            SendLock.Dispose();
        }
    }
}
