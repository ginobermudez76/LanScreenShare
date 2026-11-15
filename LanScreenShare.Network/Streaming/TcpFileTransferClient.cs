using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public sealed class TcpFileTransferClient : IDisposable
    {
        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private FileTransferPeerSession? _session;
        private bool _disposed;

        public string DownloadDirectory { get; set; }
        public bool IsConnected => _client != null && _client.Connected && _stream != null;

        public event EventHandler<FileTransferProgress>? TransferProgress;
        public event EventHandler<FileTransferCompleted>? TransferCompleted;
        public event EventHandler<FileTransferError>? TransferError;
        public event EventHandler? Disconnected;

        public TcpFileTransferClient()
        {
            DownloadDirectory = TcpFileTransferServer.GetDefaultDownloadDirectory();
        }

        public Task ConnectAsync(string hostIp, int port = 5004, CancellationToken cancellationToken = default) =>
            ConnectAsync(new FileTransferClientConnectionOptions(hostIp, port), cancellationToken);

        public Task ConnectAsync(
            string hostIp,
            int port,
            SslClientAuthenticationOptions? sslOptions,
            CancellationToken cancellationToken = default) =>
            ConnectAsync(new FileTransferClientConnectionOptions(hostIp, port, sslOptions), cancellationToken);

        public async Task ConnectAsync(FileTransferClientConnectionOptions options, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(options.HostIp, options.Port, token);
            Stream stream = client.GetStream();

            if (options.SslOptions != null)
            {
                stream = await EstablishTlsHandshakeAsync(client, stream, options, token);
            }

            var callbacks = new FileTransferCallbacks(
                progress => TransferProgress?.Invoke(this, progress),
                completed => TransferCompleted?.Invoke(this, completed),
                error => TransferError?.Invoke(this, error));

            _client = client;
            _stream = stream;
            _session = new FileTransferPeerSession(stream, () => DownloadDirectory, "FileTransferClient", callbacks);

            _ = Task.Run(() => ProcessIncomingStreamAsync(_session, token), token);
            Console.WriteLine($"[FileTransferClient] Conectado a {options.HostIp}:{options.Port} para transferencia de archivos.");
        }

        private static async Task<Stream> EstablishTlsHandshakeAsync(
            TcpClient client,
            Stream rawStream,
            FileTransferClientConnectionOptions options,
            CancellationToken token)
        {
            var sslStream = new SslStream(rawStream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsClientAsync(options.SslOptions!, linkedCts.Token);

            if (!string.IsNullOrEmpty(options.SessionId) && !string.IsNullOrEmpty(options.AttachmentToken))
            {
                byte[] attachBytes = ChannelAttachmentMessage.Serialize(options.SessionId, options.AttachmentToken, ChannelType.FileTransfer);
                await sslStream.WriteAsync(attachBytes, linkedCts.Token);

                byte[] resp = new byte[1];
                await FileTransferPacket.ReadExactBytesAsync(sslStream, resp, linkedCts.Token);
                var status = (AttachmentStatus)resp[0];

                if (status != AttachmentStatus.Success)
                {
                    client.Close();
                    throw new InvalidOperationException($"Asociación de canal de transferencia de archivos rechazada por el servidor ({status}).");
                }
            }

            return sslStream;
        }

        private async Task ProcessIncomingStreamAsync(FileTransferPeerSession session, CancellationToken ct)
        {
            try
            {
                await session.ProcessIncomingLoopAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[FileTransferClient] Desconectado del servidor: {ex.Message}");
            }
            finally
            {
                session.Dispose();
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        public Task<bool> SendFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("El archivo a enviar no existe.", filePath);

            if (_session == null || !IsConnected)
                throw new InvalidOperationException("No hay conexión activa con el anfitrión.");

            var fileInfo = new FileInfo(filePath);
            string transferId = Guid.NewGuid().ToString("D");
            return _session.SendFileAsync(transferId, fileInfo, cancellationToken);
        }

        public Task DisconnectAsync()
        {
            StreamConnectionHelper.CloseClientResources(_cts, _stream, _client);
            _cts = null;
            _stream = null;
            _client = null;
            _session?.Dispose();
            _session = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ = DisconnectAsync();
        }
    }
}
