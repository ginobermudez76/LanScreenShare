using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public sealed class TcpFileTransferServer : IDisposable
    {
        private readonly ISessionAuthority? _sessionAuthority;
        private SslServerAuthenticationOptions? _sslServerOptions;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, ClientContext> _clients = new();
        private bool _disposed;

        private sealed class ClientContext
        {
            public TcpClient Client { get; init; } = null!;
            public FileTransferPeerSession Session { get; init; } = null!;
            public string? SessionId { get; init; }
        }

        public string DownloadDirectory { get; set; }

        public event EventHandler<FileTransferProgress>? TransferProgress;
        public event EventHandler<FileTransferCompleted>? TransferCompleted;
        public event EventHandler<FileTransferError>? TransferError;
        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;

        public static string GetDefaultDownloadDirectory() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "LanScreenShare");

        public static bool TryEnsureWritableDirectory(string? directoryPath, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(directoryPath))
                return false;

            try
            {
                string fullPath = Path.GetFullPath(directoryPath.Trim());
                Directory.CreateDirectory(fullPath);

                string probeFile = Path.Combine(fullPath, $".lss_write_probe_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    // Probe write permission via DeleteOnClose file creation
                }

                resolvedPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string ResolveWritableDownloadDirectory(string? preferredDirectory)
        {
            if (TryEnsureWritableDirectory(preferredDirectory, out string validDir))
                return validDir;

            string fallback = GetDefaultDownloadDirectory();
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch
            {
                // Ignore fallback directory creation error; caller will handle write failure
            }
            return fallback;
        }

        public TcpFileTransferServer(
            ISessionAuthority? sessionAuthority = null,
            SslServerAuthenticationOptions? sslServerOptions = null)
        {
            _sessionAuthority = sessionAuthority;
            _sslServerOptions = sslServerOptions;
            DownloadDirectory = GetDefaultDownloadDirectory();

            if (_sessionAuthority != null)
            {
                _sessionAuthority.SessionTerminated += OnSessionTerminated;
            }
        }

        public void UpdateSslServerOptions(SslServerAuthenticationOptions? sslServerOptions) =>
            _sslServerOptions = sslServerOptions;

        private void OnSessionTerminated(object? sender, string sessionId) =>
            StreamConnectionHelper.TerminateMatchingSessions(_clients, ctx => ctx.SessionId, sessionId, ForceDisconnectClient);

        private void ForceDisconnectClient(string clientId)
        {
            if (_clients.TryGetValue(clientId, out var ctx))
            {
                CleanupClient(clientId, ctx.Client);
            }
        }

        private bool IsFileTransferAuthorized(string? sessionId)
        {
            if (_sessionAuthority == null || sessionId == null)
                return true;

            var session = _sessionAuthority.GetSession(new SessionId(sessionId));
            return session != null && session.FileTransferAuthorized;
        }

        public Task StartAsync(int port = 5004, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            Console.WriteLine($"[FileTransferServer] Servidor de transferencia de archivos escuchando en puerto {port}...");

            _ = Task.Run(() => AcceptConnectionsLoopAsync(token), token);
            return Task.CompletedTask;
        }

        private async Task AcceptConnectionsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(token);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleIncomingConnectionAsync(client, token), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Console.WriteLine($"[FileTransferServer] Error en AcceptTcpClient: {ex.Message}");
                    }
                }
            }
        }

        internal async Task<bool> ValidateChannelAttachmentAsync(
            SslStream sslStream,
            string peerFingerprint,
            string clientId,
            CancellationToken token) =>
            (await ValidateChannelAttachmentWithSessionAsync(sslStream, peerFingerprint, clientId, token)).Success;

        private async Task<(bool Success, string? SessionId)> ValidateChannelAttachmentWithSessionAsync(
            SslStream sslStream,
            string peerFingerprint,
            string clientId,
            CancellationToken token)
        {
            byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
            await FileTransferPacket.ReadExactBytesAsync(sslStream, attachBuffer, token);

            if (!ChannelAttachmentMessage.TryDeserialize(
                    attachBuffer,
                    out var attachPayload,
                    out string? attachError))
            {
                Console.WriteLine($"[FileTransferServer] Error en CHANNEL_ATTACH de {clientId}: {attachError}");
                return (false, null);
            }

            if (attachPayload.Channel != ChannelType.FileTransfer)
            {
                Console.WriteLine($"[FileTransferServer] Canal incorrecto en CHANNEL_ATTACH de {clientId}: {attachPayload.Channel}");
                return (false, null);
            }

            if (_sessionAuthority != null)
            {
                if (!IsFileTransferAuthorized(attachPayload.SessionId))
                {
                    await sslStream.WriteAsync(new byte[] { (byte)AttachmentStatus.SessionNotFound }, token);
                    Console.WriteLine($"[FileTransferServer] Transferencia de archivos denegada por permiso de dispositivo para {clientId}");
                    return (false, null);
                }

                var status = _sessionAuthority.TryAttachChannel(
                    attachPayload.TokenId,
                    attachPayload.SessionId,
                    peerFingerprint,
                    attachPayload.Channel);

                if (status != AttachmentStatus.Success)
                {
                    await sslStream.WriteAsync(new byte[] { (byte)status }, token);
                    Console.WriteLine($"[FileTransferServer] Rechazado CHANNEL_ATTACH ({status}) para {clientId}");
                    return (false, null);
                }
            }

            await sslStream.WriteAsync(new byte[] { (byte)AttachmentStatus.Success }, token);
            return (true, attachPayload.SessionId);
        }

        private async Task<(Stream? Stream, string? SessionId)> AuthenticateTlsAndChannelAsync(
            Stream stream,
            string clientId,
            CancellationToken token)
        {
            if (_sslServerOptions == null)
                return (stream, null);

            var sslStream = new SslStream(stream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsServerAsync(_sslServerOptions, linkedCts.Token);

            if (sslStream.RemoteCertificate == null)
            {
                Console.WriteLine($"[FileTransferServer] Conexión rechazada para {clientId}: Falta certificado.");
                return (null, null);
            }

            using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
            string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);

            var (attached, sessionId) = await ValidateChannelAttachmentWithSessionAsync(sslStream, peerFingerprint, clientId, linkedCts.Token);
            return attached ? (sslStream, sessionId) : (null, null);
        }

        private void CleanupClient(string clientId, TcpClient client)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                ctx.Session.Dispose();
            }
            try { client.Close(); } catch { /* Ignorar error al cerrar socket */ }
            ClientDisconnected?.Invoke(this, clientId);
        }

        private async Task HandleIncomingConnectionAsync(TcpClient client, CancellationToken token)
        {
            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
            try
            {
                var (stream, sessionId) = await AuthenticateTlsAndChannelAsync(client.GetStream(), clientId, token);
                if (stream == null)
                {
                    client.Close();
                    return;
                }

                var callbacks = new FileTransferCallbacks(
                    progress => TransferProgress?.Invoke(this, progress),
                    completed => TransferCompleted?.Invoke(this, completed),
                    error => TransferError?.Invoke(this, error));

                var ctx = new ClientContext
                {
                    Client = client,
                    SessionId = sessionId,
                    Session = new FileTransferPeerSession(
                        stream,
                        () => DownloadDirectory,
                        "FileTransferServer",
                        callbacks,
                        () => IsFileTransferAuthorized(sessionId))
                };

                _clients[clientId] = ctx;
                ClientConnected?.Invoke(this, clientId);
                Console.WriteLine($"[FileTransferServer] Cliente conectado para transferencia de archivos: {clientId}");

                await ctx.Session.ProcessIncomingLoopAsync(token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransferServer] Error cliente {clientId}: {ex.Message}");
            }
            finally
            {
                CleanupClient(clientId, client);
            }
        }

        public async Task<bool> SendFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("El archivo a enviar no existe.", filePath);

            if (_clients.IsEmpty)
                throw new InvalidOperationException("No hay clientes conectados para recibir el archivo.");

            var fileInfo = new FileInfo(filePath);
            string transferId = Guid.NewGuid().ToString("D");

            foreach (var ctx in _clients.Values)
            {
                if (!IsFileTransferAuthorized(ctx.SessionId))
                    continue;

                if (await ctx.Session.SendFileAsync(transferId, fileInfo, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        public static string GetUniqueDestinationPath(string directory, string originalFileName)
        {
            string targetDir = ResolveWritableDownloadDirectory(directory);
            string safeName = Path.GetFileName(originalFileName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = $"archivo_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            }

            string fullPath = Path.Combine(targetDir, safeName);
            if (!File.Exists(fullPath))
                return fullPath;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
            string ext = Path.GetExtension(safeName);

            int count = 1;
            while (true)
            {
                string candidate = Path.Combine(targetDir, $"{nameWithoutExt} ({count}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
                count++;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;

            foreach (var ctx in _clients.Values)
            {
                try { ctx.Client.Close(); } catch { /* Ignore client close error on stop */ }
            }
            _clients.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_sessionAuthority != null)
            {
                _sessionAuthority.SessionTerminated -= OnSessionTerminated;
            }
            Stop();
            _cts?.Dispose();
        }
    }
}
