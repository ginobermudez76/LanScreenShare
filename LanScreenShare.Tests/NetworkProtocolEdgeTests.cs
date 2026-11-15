using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class NetworkProtocolEdgeTests
{
    [TestMethod]
    public void AudioPacketProtocol_HandlesEdgeCasesAndOverrideSource()
    {
        var chunk = new CapturedAudioChunk
        {
            Data = new byte[] { 1, 2, 3, 4 },
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 16,
            AudioFormat = 1,
            Source = AudioSourceType.Desktop,
            Timestamp = 123456
        };

        byte[] packet = AudioPacketProtocol.SerializePacket(chunk, AudioSourceType.Microphone);
        Assert.IsTrue(AudioPacketProtocol.TryParseHeader(packet, out var header));
        Assert.AreEqual(AudioSourceType.Microphone, header.Source);
        Assert.AreEqual(4, header.DataLength);

        var emptyChunk = new CapturedAudioChunk { Data = null!, SampleRate = 48000 };
        byte[] emptyPacket = AudioPacketProtocol.SerializePacket(emptyChunk);
        Assert.AreEqual(AudioPacketProtocol.HeaderSize, emptyPacket.Length);

        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(new byte[AudioPacketProtocol.HeaderSize - 1], out _));

        byte[] badMagic = (byte[])packet.Clone();
        badMagic[0] = 0x00;
        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(badMagic, out _));

        byte[] zeroLen = (byte[])packet.Clone();
        BitConverter.GetBytes(0).CopyTo(zeroLen, 4);
        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(zeroLen, out _));

        byte[] hugeLen = (byte[])packet.Clone();
        BitConverter.GetBytes(AudioPacketProtocol.MaxAudioPayloadSize + 1).CopyTo(hugeLen, 4);
        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(hugeLen, out _));
    }

    [TestMethod]
    public void VideoPacketProtocol_SerializesParsesAndValidatesMagic()
    {
        var input = new VideoHeaderData(
            VideoPacketProtocol.MagicH264,
            DataLength: 4096,
            Width: 1920,
            Height: 1080,
            Timestamp: 987654321,
            HostFps: 60,
            HostFrameTimeMs: 3);

        byte[] raw = VideoPacketProtocol.SerializeHeader(input);
        Assert.IsTrue(VideoPacketProtocol.TryParseHeader(raw, out var parsed));
        Assert.AreEqual(input, parsed);
        Assert.IsFalse(VideoPacketProtocol.TryParseHeader(new byte[VideoPacketProtocol.HeaderSize - 1], out _));

        Assert.IsTrue(VideoPacketProtocol.IsValidFrameMagic(VideoPacketProtocol.MagicJpeg));
        Assert.IsTrue(VideoPacketProtocol.IsValidFrameMagic(VideoPacketProtocol.MagicH264));
        Assert.IsFalse(VideoPacketProtocol.IsValidFrameMagic(VideoPacketProtocol.MagicEnd));
        Assert.IsFalse(VideoPacketProtocol.IsValidFrameMagic(0x12345678));
    }

    [TestMethod]
    public void Lsi2ControlPacket_ValidatesBufferAndRelativeAndNegativeCoordinates()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Lsi2ControlPacket.WritePacket(new byte[10], 1, 1, new RemoteInputMessage()));

        byte[] buf = new byte[Lsi2ControlPacket.PacketSize];
        var relMsg = new RemoteInputMessage
        {
            Type = InputEventType.MouseMoveRelative,
            NormalizedX = -25.4f,
            NormalizedY = 40.6f
        };
        Lsi2ControlPacket.WritePacket(buf, 10, 20, relMsg);
        Assert.IsTrue(Lsi2ControlPacket.TryReadPacket(buf, out var header, out var parsedRel, out string? err));
        Assert.IsNull(err);
        Assert.AreEqual(10UL, header.SequenceNumber);
        Assert.AreEqual(-25f, parsedRel!.NormalizedX);
        Assert.AreEqual(41f, parsedRel.NormalizedY);

        var negAbs = new RemoteInputMessage
        {
            Type = InputEventType.MouseMove,
            NormalizedX = -1f,
            NormalizedY = -1f
        };
        Lsi2ControlPacket.WritePacket(buf, 11, 21, negAbs);
        Assert.IsTrue(Lsi2ControlPacket.TryReadPacket(buf, out _, out var parsedNeg, out _));
        Assert.IsTrue(parsedNeg!.NormalizedX < 0f);

        Assert.IsFalse(Lsi2ControlPacket.TryReadPacket(new byte[2], out _, out _, out string? tooShort));
        Assert.AreEqual("PacketTooShort", tooShort);

        byte[] legacy = new byte[Lsi2ControlPacket.PacketSize];
        legacy[0] = (byte)'L';
        legacy[1] = (byte)'S';
        legacy[2] = (byte)'I';
        legacy[3] = (byte)'1';
        Assert.IsFalse(Lsi2ControlPacket.TryReadPacket(legacy, out _, out _, out string? lsi1Err));
        Assert.IsTrue(lsi1Err!.StartsWith("BadProtocolVersion"));

        legacy[3] = (byte)'X';
        Assert.IsFalse(Lsi2ControlPacket.TryReadPacket(legacy, out _, out _, out string? badMagicErr));
        Assert.IsTrue(badMagicErr!.StartsWith("InvalidMagic"));

        Assert.IsFalse(Lsi2ControlPacket.TryReadPacket(buf.AsSpan(0, 12), out _, out _, out string? incompleteErr));
        Assert.IsTrue(incompleteErr!.StartsWith("IncompletePacket"));
    }

    [TestMethod]
    public async Task TlsConfigurationHelper_ValidatesWeakKeysRevocationAndOptionsCallbacks()
    {
        using var weakRsa = RSA.Create(1024);
        var weakReq = new CertificateRequest("CN=Weak", weakRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        weakReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(TlsConfigurationHelper.ClientAuthOid)], true));
        using var weakCert = weakReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));

        Assert.IsFalse(TlsConfigurationHelper.ValidateCertificate(weakCert, TlsConfigurationHelper.ClientAuthOid, null, (_, _) => true));

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var validReq = new CertificateRequest("CN=Strong", ecdsa, HashAlgorithmName.SHA256);
        validReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(TlsConfigurationHelper.ClientAuthOid)], true));
        using var validCert = validReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));

        string fp = TlsConfigurationHelper.ComputeFingerprint(validCert);
        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(new PairedDevice("id1", "Dev", fp, DateTime.UtcNow, DateTime.UtcNow, true));

        Assert.IsFalse(TlsConfigurationHelper.ValidateCertificate(validCert, TlsConfigurationHelper.ClientAuthOid, store, (_, _) => true));

        var serverOpts = TlsConfigurationHelper.CreateServerOptions(validCert, store, (_, _) => false);
        Assert.IsNotNull(serverOpts.RemoteCertificateValidationCallback);
        Assert.IsFalse(serverOpts.RemoteCertificateValidationCallback!(this, null, null, SslPolicyErrors.None));
        Assert.IsFalse(serverOpts.RemoteCertificateValidationCallback!(this, validCert, null, SslPolicyErrors.None));

        var clientOpts = TlsConfigurationHelper.CreateClientOptions(validCert, "localhost", store, null);
        Assert.IsNotNull(clientOpts.LocalCertificateSelectionCallback);
        Assert.IsNotNull(clientOpts.RemoteCertificateValidationCallback);
    }

    [TestMethod]
    public async Task FileTransferPacket_WritesAllPacketTypesAndReadsExactBytes()
    {
        using var ms = new MemoryStream();
        await FileTransferPacket.WriteOfferAsync(ms, new FileTransferOfferDto { TransferId = "t1", FileName = "a.bin", FileSize = 10 }, default);
        await FileTransferPacket.WriteAcceptAsync(ms, "t1", default);
        await FileTransferPacket.WriteRejectAsync(ms, "t1", "Denied", default);
        await FileTransferPacket.WriteCompleteAsync(ms, "t1", default);
        await FileTransferPacket.WriteCancelAsync(ms, "t1", "Aborted", default);
        await FileTransferPacket.WriteChunkAsync(ms, new FileChunkPayload(Guid.NewGuid(), 0, new byte[] { 1, 2, 3 }), default);
        await FileTransferPacket.WriteChunkAsync(ms, new FileChunkPayload(Guid.NewGuid(), 3, ReadOnlyMemory<byte>.Empty), default);

        Assert.IsTrue(ms.Length > 0);
        ms.Position = 0;

        byte[] readBuf = new byte[9];
        await FileTransferPacket.ReadExactBytesAsync(ms, readBuf, default);
        Assert.AreEqual((byte)FileTransferPacketType.Offer, readBuf[4]);

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(async () =>
            await FileTransferPacket.ReadExactBytesAsync(new MemoryStream(new byte[4]), new byte[9], default));
    }

    [TestMethod]
    public void ChannelAttachmentMessage_ValidatesInputsAndRoundTripsAllChannels()
    {
        string sid = Guid.NewGuid().ToString("D");
        string tid = "0123456789abcdef0123456789abcdef";

        Assert.ThrowsException<ArgumentException>(() => ChannelAttachmentMessage.Serialize("not-a-guid", tid, ChannelType.Video));
        Assert.ThrowsException<ArgumentException>(() => ChannelAttachmentMessage.Serialize(sid, "0123", ChannelType.Video));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ChannelAttachmentMessage.Serialize(sid, tid, (ChannelType)99));

        byte[] ftBytes = ChannelAttachmentMessage.Serialize(sid, tid, ChannelType.FileTransfer);
        Assert.IsTrue(ChannelAttachmentMessage.TryDeserialize(ftBytes, out var ftPayload, out string? err));
        Assert.IsNull(err);
        Assert.AreEqual(ChannelType.FileTransfer, ftPayload.Channel);

        Assert.IsFalse(ChannelAttachmentMessage.TryDeserialize(new byte[5], out _, out string? shortErr));
        Assert.IsTrue(shortErr!.Contains("short"));

        byte[] badMagic = (byte[])ftBytes.Clone();
        badMagic[0] = 0x00;
        Assert.IsFalse(ChannelAttachmentMessage.TryDeserialize(badMagic, out _, out string? magicErr));
        Assert.IsTrue(magicErr!.Contains("magic"));

        byte[] badChannel = (byte[])ftBytes.Clone();
        badChannel[36] = 99;
        Assert.IsFalse(ChannelAttachmentMessage.TryDeserialize(badChannel, out _, out string? chErr));
        Assert.IsTrue(chErr!.Contains("channel"));
    }
}
