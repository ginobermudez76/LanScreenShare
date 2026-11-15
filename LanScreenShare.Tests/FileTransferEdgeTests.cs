using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class FileTransferEdgeTests
{
    private sealed class FragmentedReadStream(byte[] data, int maxChunkSize) : MemoryStream(data, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int allowed = Math.Min(maxChunkSize, buffer.Length);
            return base.ReadAsync(buffer.Slice(0, allowed), cancellationToken);
        }
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; }

        public TempDirectoryScope(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp directory cleanup
            }
        }
    }

    private static int GetAvailableTcpPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<(FileTransferPacketType Type, byte[] Payload)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[FileTransferPacket.HeaderSize];
        await FileTransferPacket.ReadExactBytesAsync(stream, header, ct);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        Assert.AreEqual(FileTransferPacket.Magic, magic);

        var type = (FileTransferPacketType)header[4];
        uint len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        byte[] payload = new byte[len];
        await FileTransferPacket.ReadExactBytesAsync(stream, payload, ct);
        return (type, payload);
    }

    private static X509Certificate2 CreateEcdsaCert(string cn, string ekuOid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ekuOid) }, critical: true));

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private readonly record struct CancelScenarioSpec(
        string FileName,
        long FileSize,
        int ChunkBytes,
        string Reason);

    private static async Task SendOfferChunkThenCancelAsync(Stream stream, Guid transferGuid, CancelScenarioSpec spec)
    {
        string transferId = transferGuid.ToString("D");
        await FileTransferPacket.WriteOfferAsync(stream, new FileTransferOfferDto
        {
            TransferId = transferId,
            FileName = spec.FileName,
            FileSize = spec.FileSize
        }, CancellationToken.None);

        var (ackType, _) = await ReadPacketAsync(stream, CancellationToken.None);
        Assert.AreEqual(FileTransferPacketType.Accept, ackType);

        await FileTransferPacket.WriteChunkAsync(stream, new FileChunkPayload(transferGuid, 0, new byte[spec.ChunkBytes]), CancellationToken.None);
        await FileTransferPacket.WriteCancelAsync(stream, transferId, spec.Reason, CancellationToken.None);
    }

    [TestMethod]
    public async Task SendFileAsync_MissingFileOrDisconnected_ThrowsExpectedExceptions()
    {
        using var client = new TcpFileTransferClient();
        using var server = new TcpFileTransferServer();
        using var tempDir = new TempDirectoryScope("LssGuard");
        string missingFile = Path.Combine(tempDir.Path, "missing.txt");
        string existingFile = Path.Combine(tempDir.Path, "sample.txt");
        await File.WriteAllTextAsync(existingFile, "hola");

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => client.SendFileAsync(missingFile));
        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() => server.SendFileAsync(missingFile));

        await client.DisconnectAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.SendFileAsync(existingFile));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => server.SendFileAsync(existingFile));
    }

    [TestMethod]
    public async Task SendFileAsync_WhenPeerRejectsOffer_ReturnsFalseAndRaisesError()
    {
        using var tempDir = new TempDirectoryScope("LssReject");
        string sourceFile = Path.Combine(tempDir.Path, "reject_me.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4]);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpFileTransferClient();
        var errorTcs = new TaskCompletionSource<FileTransferError>();
        client.TransferError += (_, err) => errorTcs.TrySetResult(err);

        var serverTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = accepted.GetStream();
            var (type, payload) = await ReadPacketAsync(stream, CancellationToken.None);
            Assert.AreEqual(FileTransferPacketType.Offer, type);

            var offer = JsonSerializer.Deserialize<FileTransferOfferDto>(payload)!;
            await FileTransferPacket.WriteRejectAsync(stream, offer.TransferId, "Espacio insuficiente", CancellationToken.None);
        });

        await client.ConnectAsync("127.0.0.1", port);
        bool sent = await client.SendFileAsync(sourceFile);
        await serverTask;
        var err = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(sent);
        Assert.IsTrue(err.IsUpload);
        StringAssert.Contains(err.ErrorMessage, "rechazó");
    }

    [TestMethod]
    public async Task ClientInboundTransfer_WhenCancelReceived_DeletesPartialFileAndRaisesError()
    {
        using var tempDir = new TempDirectoryScope("LssClientCancel");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpFileTransferClient { DownloadDirectory = tempDir.Path };
        var errorTcs = new TaskCompletionSource<FileTransferError>();
        client.TransferError += (_, err) => errorTcs.TrySetResult(err);

        Guid transferGuid = Guid.NewGuid();
        string transferId = transferGuid.ToString("D");
        var scenario = new CancelScenarioSpec("partial.bin", 1024, 64, "Cancelado por prueba");

        var stubTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = accepted.GetStream();
            await SendOfferChunkThenCancelAsync(stream, transferGuid, scenario);
        });

        await client.ConnectAsync("127.0.0.1", port);
        var error = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stubTask;

        Assert.AreEqual((transferId, false, scenario.Reason), (error.TransferId, error.IsUpload, error.ErrorMessage));
        Assert.IsFalse(File.Exists(Path.Combine(tempDir.Path, scenario.FileName)));
    }

    [TestMethod]
    public async Task ServerInboundTransfer_WhenCancelReceived_DeletesPartialFileAndRaisesError()
    {
        using var tempDir = new TempDirectoryScope("LssServerCancel");
        int port = GetAvailableTcpPort();
        using var server = new TcpFileTransferServer { DownloadDirectory = tempDir.Path };
        await server.StartAsync(port);

        var errorTcs = new TaskCompletionSource<FileTransferError>();
        server.TransferError += (_, err) => errorTcs.TrySetResult(err);

        Guid transferGuid = Guid.NewGuid();
        string transferId = transferGuid.ToString("D");
        var scenario = new CancelScenarioSpec("server_partial.bin", 2048, 32, "Cancelado desde emisor");

        using var rawClient = new TcpClient();
        await rawClient.ConnectAsync("127.0.0.1", port);
        using NetworkStream stream = rawClient.GetStream();

        await SendOfferChunkThenCancelAsync(stream, transferGuid, scenario);

        var error = await errorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual((transferId, false, scenario.Reason), (error.TransferId, error.IsUpload, error.ErrorMessage));
        Assert.IsFalse(File.Exists(Path.Combine(tempDir.Path, scenario.FileName)));
    }

    [TestMethod]
    public async Task FileTransferMtls_WhenChannelTokenIsWrongOrInvalid_RejectsClientConnection()
    {
        using var hostCert = CreateEcdsaCert("FileHostReject", TlsConfigurationHelper.ServerAuthOid);
        using var viewerCert = CreateEcdsaCert("FileViewerReject", TlsConfigurationHelper.ClientAuthOid);

        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(PairedDevice.Create("h1", "Host", TlsConfigurationHelper.ComputeFingerprint(hostCert)));
        await store.AddPairedDeviceAsync(PairedDevice.Create("v1", "Viewer", TlsConfigurationHelper.ComputeFingerprint(viewerCert)));

        var authority = new SessionAuthority(store);
        var (_, session, _) = await authority.TryCreateSessionAsync(TlsConfigurationHelper.ComputeFingerprint(viewerCert));
        var wrongChannelToken = authority.IssueChannelToken(session!.SessionId, ChannelType.RemoteControl);

        int port = GetAvailableTcpPort();
        using var server = new TcpFileTransferServer(authority, TlsConfigurationHelper.CreateServerOptions(hostCert, store));
        await server.StartAsync(port);

        using var client = new TcpFileTransferClient();
        var clientSslOpts = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", store);
        var badAttachOptions = new FileTransferClientConnectionOptions("127.0.0.1", port, clientSslOpts, session.SessionId, wrongChannelToken!.TokenId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.ConnectAsync(badAttachOptions));
    }

    [TestMethod]
    public async Task StreamConnectionHelper_ReadExactBytes_ReassemblesFragmentsAndDetectsEof()
    {
        byte[] source = [10, 20, 30, 40, 50, 60, 70];
        using var fragmented = new FragmentedReadStream(source, maxChunkSize: 2);
        byte[] actual = new byte[source.Length];

        await StreamConnectionHelper.ReadExactBytesAsync(fragmented, actual, CancellationToken.None);
        CollectionAssert.AreEqual(source, actual);

        using var truncated = new FragmentedReadStream([1, 2], maxChunkSize: 1);
        byte[] larger = new byte[5];
        var ex = await Assert.ThrowsExceptionAsync<EndOfStreamException>(
            () => StreamConnectionHelper.ReadExactBytesAsync(truncated, larger, CancellationToken.None, "EOF esperado"));

        Assert.AreEqual("EOF esperado", ex.Message);
    }

    [TestMethod]
    public void StreamConnectionHelper_CloseClientResources_HandlesNullAndInitializedResources()
    {
        StreamConnectionHelper.CloseClientResources(null, null, null);

        using var cts = new CancellationTokenSource();
        var stream = new MemoryStream();
        var client = new TcpClient();

        StreamConnectionHelper.CloseClientResources(cts, stream, client);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = cts.Token);
    }
}
