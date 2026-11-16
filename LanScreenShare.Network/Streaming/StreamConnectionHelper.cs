using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Network.Streaming
{
    internal static class StreamConnectionHelper
    {
        internal static TcpListener CreateStartedTcpListener(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            return listener;
        }

        internal static (CancellationTokenSource Cts, TcpListener Listener) StartAcceptLoop(
            int port,
            Func<TcpListener, CancellationToken, Task> acceptLoop,
            CancellationToken cancellationToken = default)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var listener = CreateStartedTcpListener(port);
            var token = cts.Token;
            _ = Task.Run(() => acceptLoop(listener, token), token);
            return (cts, listener);
        }

        internal static void TerminateMatchingSessions<T>(
            ConcurrentDictionary<string, T> clients,
            Func<T, string?> getSessionId,
            string terminatedSessionId,
            Action<string> removeClient)
        {
            var matches = clients.Where(kvp =>
                string.Equals(getSessionId(kvp.Value), terminatedSessionId, StringComparison.Ordinal));

            foreach (var kvp in matches)
            {
                removeClient(kvp.Key);
            }
        }
        internal static async Task ReadExactBytesAsync(
            Stream stream,
            Memory<byte> buffer,
            CancellationToken token,
            string eofMessage = "Conexión cerrada antes de completar la lectura.")
        {
            int totalBytesRead = 0;
            int count = buffer.Length;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer.Slice(totalBytesRead, count - totalBytesRead), token);
                if (bytesRead <= 0)
                {
                    throw new EndOfStreamException(eofMessage);
                }
                totalBytesRead += bytesRead;
            }
        }

        internal static void CloseClientResources(CancellationTokenSource? cts, Stream? stream, TcpClient? client)
        {
            cts?.Cancel();
            try { stream?.Dispose(); } catch { /* Ignore stream disposal error on disconnect */ }
            try { client?.Close(); client?.Dispose(); } catch { /* Ignore client disposal error on disconnect */ }
            cts?.Dispose();
        }

        internal static async Task WriteJsonFrameAsync<T>(
            Stream stream,
            T payload,
            int maxBytes,
            CancellationToken ct)
        {
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            if (jsonBytes.Length > maxBytes)
            {
                throw new InvalidOperationException($"La trama de señalización excede el límite de {maxBytes} bytes.");
            }

            byte[] lengthPrefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)jsonBytes.Length);

            await stream.WriteAsync(lengthPrefix, ct);
            await stream.WriteAsync(jsonBytes, ct);
            await stream.FlushAsync(ct);
        }

        internal static async Task<T> ReadJsonFrameAsync<T>(
            Stream stream,
            int maxBytes,
            CancellationToken ct)
        {
            const string eofMessage = "Flujo cerrado antes de completar la lectura del mensaje de señalización.";
            byte[] lengthPrefix = new byte[4];
            await ReadExactBytesAsync(stream, lengthPrefix, ct, eofMessage);

            uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
            if (length == 0 || length > maxBytes)
            {
                throw new InvalidDataException($"Longitud de señalización inválida ({length} bytes). Límite: {maxBytes} bytes.");
            }

            byte[] payload = new byte[length];
            await ReadExactBytesAsync(stream, payload, ct, eofMessage);

            var result = JsonSerializer.Deserialize<T>(payload);
            return result ?? throw new InvalidDataException("No se pudo deserializar el mensaje de señalización JSON.");
        }
    }
}
