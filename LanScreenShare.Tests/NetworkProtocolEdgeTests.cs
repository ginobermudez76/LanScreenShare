using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
    private static async Task<(FileTransferPacketType Type, byte[] Payload)> ReadNextFilePacketAsync(Stream stream)
    {
        byte[] header = new byte[FileTransferPacket.HeaderSize];
        await FileTransferPacket.ReadExactBytesAsync(stream, header, default);
        Assert.AreEqual(FileTransferPacket.Magic, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)));
        var type = (FileTransferPacketType)header[4];
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5, 4));
        byte[] payload = new byte[length];
        if (length > 0)
        {
            await FileTransferPacket.ReadExactBytesAsync(stream, payload, default);
        }
        return (type, payload);
    }

    private static (Guid TransferId, long Offset, byte[] Data) ParseChunkPayload(byte[] payload)
    {
        var guid = new Guid(payload.AsSpan(0, 16));
        long offset = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8));
        return (guid, offset, payload.AsSpan(24).ToArray());
    }

    [TestMethod]
    public void AudioPacketProtocol_HandlesOverrideAndEmptyData()
    {
        var chunk = new CapturedAudioChunk { Data = new byte[] { 1, 2, 3, 4 }, SampleRate = 48000, Channels = 2, BitsPerSample = 16, AudioFormat = 1, Source = AudioSourceType.Desktop, Timestamp = 123456 };
        byte[] packet = AudioPacketProtocol.SerializePacket(chunk, AudioSourceType.Microphone);

        Assert.IsTrue(AudioPacketProtocol.TryParseHeader(packet, out var header));
        Assert.AreEqual(AudioSourceType.Microphone, header.Source);

        byte[] emptyPacket = AudioPacketProtocol.SerializePacket(new CapturedAudioChunk { Data = null!, SampleRate = 48000 });
        Assert.AreEqual(AudioPacketProtocol.HeaderSize, emptyPacket.Length);
    }

    [TestMethod]
    public void AudioPacketProtocol_RejectsMalformedHeaders()
    {
        byte[] valid = AudioPacketProtocol.SerializePacket(new CapturedAudioChunk { Data = new byte[] { 1 }, SampleRate = 48000 });
        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] = 0x00;

        byte[] zeroLen = (byte[])valid.Clone();
        BitConverter.GetBytes(0).CopyTo(zeroLen, 4);

        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(new byte[AudioPacketProtocol.HeaderSize - 1], out _));
        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(badMagic, out _));
        Assert.IsFalse(AudioPacketProtocol.TryParseHeader(zeroLen, out _));
    }

    [TestMethod]
    public void VideoPacketProtocol_SerializesParsesAndValidatesMagic()
    {
        var input = new VideoHeaderData(VideoPacketProtocol.MagicH264, 4096, 1920, 1080, 987654321, 60, 3);
        byte[] raw = VideoPacketProtocol.SerializeHeader(input);

        Assert.IsTrue(VideoPacketProtocol.TryParseHeader(raw, out var parsed) && parsed == input);
        Assert.IsFalse(VideoPacketProtocol.TryParseHeader(new byte[VideoPacketProtocol.HeaderSize - 1], out _));
        Assert.IsTrue(VideoPacketProtocol.IsValidFrameMagic(VideoPacketProtocol.MagicJpeg) && !VideoPacketProtocol.IsValidFrameMagic(VideoPacketProtocol.MagicEnd));
    }

    [TestMethod]
    public void Lsi2ControlPacket_PacksRelativeAndNegativeCoordinates()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Lsi2ControlPacket.WritePacket(new byte[10], 1, 1, new RemoteInputMessage()));

        byte[] buf = new byte[Lsi2ControlPacket.PacketSize];
        Lsi2ControlPacket.WritePacket(buf, 10, 20, new RemoteInputMessage { Type = InputEventType.MouseMoveRelative, NormalizedX = -25.4f, NormalizedY = 40.6f });
        bool ok = Lsi2ControlPacket.TryReadPacket(buf, out var header, out var parsedRel, out string? err);

        Assert.IsTrue(ok);
        Assert.AreEqual((10UL, -25f, 41f), (header.SequenceNumber, parsedRel!.NormalizedX, parsedRel.NormalizedY));

        Lsi2ControlPacket.WritePacket(buf, 11, 21, new RemoteInputMessage { Type = InputEventType.MouseMove, NormalizedX = -1f, NormalizedY = -1f });
        Assert.IsTrue(Lsi2ControlPacket.TryReadPacket(buf, out _, out var parsedNeg, out _) && parsedNeg!.NormalizedX < 0f);
    }

    [TestMethod]
    public void Lsi2ControlPacket_RejectsMalformedAndLegacyPackets()
    {
        bool shortOk = Lsi2ControlPacket.TryReadPacket(new byte[2], out _, out _, out string? tooShort);
        Assert.IsFalse(shortOk);
        Assert.AreEqual("PacketTooShort", tooShort);

        byte[] legacy = new byte[Lsi2ControlPacket.PacketSize];
        legacy[0] = (byte)'L';
        legacy[1] = (byte)'S';
        legacy[2] = (byte)'I';
        legacy[3] = (byte)'1';
        Assert.IsFalse(Lsi2ControlPacket.TryReadPacket(legacy, out _, out _, out string? lsi1Err) || !lsi1Err!.StartsWith("BadProtocolVersion"));

        legacy[3] = (byte)'X';
        Assert.IsFalse(Lsi2ControlPacket.TryReadPacket(legacy, out _, out _, out string? badMagicErr));
        Assert.IsTrue(badMagicErr!.StartsWith("InvalidMagic"));
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
        await store.AddPairedDeviceAsync(PairedDevice.Create("id1", "Dev", fp, isRevoked: true));

        var serverOpts = TlsConfigurationHelper.CreateServerOptions(validCert, store, (_, _) => false);
        Assert.IsFalse(serverOpts.RemoteCertificateValidationCallback!(this, null, null, SslPolicyErrors.None));
        Assert.IsFalse(serverOpts.RemoteCertificateValidationCallback!(this, validCert, null, SslPolicyErrors.None));
    }

    [TestMethod]
    public async Task FileTransferPacket_WritesAndReadsControlPackets()
    {
        using var ms = new MemoryStream();
        await FileTransferPacket.WriteOfferAsync(ms, new FileTransferOfferDto { TransferId = "t1", FileName = "a.bin", FileSize = 10 }, default);
        await FileTransferPacket.WriteAcceptAsync(ms, "t1", default);
        await FileTransferPacket.WriteRejectAsync(ms, "t1", "Denied", default);
        await FileTransferPacket.WriteCompleteAsync(ms, "t1", default);
        await FileTransferPacket.WriteCancelAsync(ms, "t1", "Aborted", default);

        ms.Position = 0;

        var offer = await ReadNextFilePacketAsync(ms);
        var accept = await ReadNextFilePacketAsync(ms);
        var reject = await ReadNextFilePacketAsync(ms);
        Assert.AreEqual(FileTransferPacketType.Offer, offer.Type);
        Assert.AreEqual("a.bin", JsonSerializer.Deserialize<FileTransferOfferDto>(offer.Payload)!.FileName);
        Assert.IsTrue(accept.Type == FileTransferPacketType.Accept && JsonSerializer.Deserialize<FileTransferRejectDto>(reject.Payload)!.Reason == "Denied");

        var complete = await ReadNextFilePacketAsync(ms);
        var cancel = await ReadNextFilePacketAsync(ms);
        Assert.AreEqual(FileTransferPacketType.Complete, complete.Type);
        Assert.AreEqual("Aborted", JsonSerializer.Deserialize<FileTransferCancelDto>(cancel.Payload)!.Reason);
    }

    [TestMethod]
    public async Task FileTransferPacket_WritesAndReadsChunkPacketsAndTruncatedStream()
    {
        Guid transferGuid = Guid.NewGuid();
        using var ms = new MemoryStream();
        await FileTransferPacket.WriteChunkAsync(ms, new FileChunkPayload(transferGuid, 0, new byte[] { 1, 2, 3 }), default);
        await FileTransferPacket.WriteChunkAsync(ms, new FileChunkPayload(transferGuid, 3, ReadOnlyMemory<byte>.Empty), default);

        ms.Position = 0;
        var parsed1 = ParseChunkPayload((await ReadNextFilePacketAsync(ms)).Payload);
        var parsed2 = ParseChunkPayload((await ReadNextFilePacketAsync(ms)).Payload);

        Assert.AreEqual((transferGuid, 0L), (parsed1.TransferId, parsed1.Offset));
        Assert.IsTrue(parsed1.Data.AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.AreEqual((transferGuid, 3L, 0), (parsed2.TransferId, parsed2.Offset, parsed2.Data.Length));

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(async () =>
            await FileTransferPacket.ReadExactBytesAsync(new MemoryStream(new byte[4]), new byte[9], default));
    }

    [TestMethod]
    public void ChannelAttachmentMessage_ValidatesInputsAndErrors()
    {
        string sid = Guid.NewGuid().ToString("D");
        string tid = "0123456789abcdef0123456789abcdef";

        Assert.ThrowsException<ArgumentException>(() => ChannelAttachmentMessage.Serialize("not-a-guid", tid, ChannelType.Video));
        Assert.ThrowsException<ArgumentException>(() => ChannelAttachmentMessage.Serialize(sid, "0123", ChannelType.Video));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ChannelAttachmentMessage.Serialize(sid, tid, (ChannelType)99));
    }

    [TestMethod]
    public void ChannelAttachmentMessage_RoundTripsAndRejectsCorruptFrames()
    {
        string sid = Guid.NewGuid().ToString("D");
        string tid = "0123456789abcdef0123456789abcdef";
        byte[] ftBytes = ChannelAttachmentMessage.Serialize(sid, tid, ChannelType.FileTransfer);

        bool roundTripOk = ChannelAttachmentMessage.TryDeserialize(ftBytes, out var ftPayload, out string? err);
        Assert.IsTrue(roundTripOk);
        Assert.AreEqual((ChannelType.FileTransfer, (string?)null), (ftPayload.Channel, err));

        byte[] badMagic = (byte[])ftBytes.Clone();
        badMagic[0] = 0x00;
        bool badMagicOk = ChannelAttachmentMessage.TryDeserialize(badMagic, out _, out string? magicErr);
        Assert.IsFalse(badMagicOk || !magicErr!.Contains("magic"));

        byte[] badChannel = (byte[])ftBytes.Clone();
        badChannel[36] = 99;
        bool badChannelOk = ChannelAttachmentMessage.TryDeserialize(badChannel, out _, out string? chErr);
        Assert.IsFalse(badChannelOk || !chErr!.Contains("channel"));
    }
}
