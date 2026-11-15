using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Network.Streaming
{
    internal static class StreamConnectionHelper
    {
        internal static void TerminateMatchingSessions<T>(
            ConcurrentDictionary<string, T> clients,
            Func<T, string?> getSessionId,
            string terminatedSessionId,
            Action<string> removeClient)
        {
            foreach (var kvp in clients)
            {
                if (string.Equals(getSessionId(kvp.Value), terminatedSessionId, StringComparison.Ordinal))
                {
                    removeClient(kvp.Key);
                }
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
    }
}
