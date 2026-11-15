using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    }
}
