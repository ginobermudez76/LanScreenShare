using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Media.SecureAgent;

public enum SecureAgentMessageType : byte
{
    StartCapture = 0x01,
    StopCapture = 0x02,
    UpdateSettings = 0x03,
    RequestKeyframe = 0x04,
    SetInputTarget = 0x05,
    SimulateInput = 0x06,
    ReleaseInputs = 0x07,
    Ping = 0x08,
    Frame = 0x81,
    SecureDesktopState = 0x82,
    StatusSnapshot = 0x83,
    Pong = 0x84
}

public sealed record CaptureSourceDto(
    CaptureSourceType Type,
    string Id,
    string Name,
    string? Subtitle,
    long HandleValue,
    int MonitorIndex,
    string? DeviceName,
    string? ProcessName,
    int ProcessId,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    public static CaptureSourceDto FromModel(CaptureSource source) => new(
        source.Type,
        source.Id,
        source.Name,
        source.Subtitle,
        source.Handle.ToInt64(),
        source.MonitorIndex,
        source.DeviceName,
        source.ProcessName,
        source.ProcessId,
        source.X,
        source.Y,
        source.Width,
        source.Height,
        source.IsPrimary);

    public CaptureSource ToModel() => new()
    {
        Type = Type,
        Id = Id,
        Name = Name,
        Subtitle = Subtitle,
        Handle = new IntPtr(HandleValue),
        MonitorIndex = MonitorIndex,
        DeviceName = DeviceName,
        ProcessName = ProcessName,
        ProcessId = ProcessId,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        IsPrimary = IsPrimary
    };
}

public sealed record AgentStartCaptureRequest(
    CaptureSourceDto Source,
    string Engine,
    CaptureProfile Profile);

public sealed record AgentStatusSnapshot(
    bool IsCapturing,
    string? ActiveEncoderDescription,
    bool IsHardwareAccelerated);

public readonly record struct SecureAgentPacket(SecureAgentMessageType Type, byte[] Payload);

public static class SecureAgentProtocol
{
    public const string PipeName = "LanScreenShare.SecureAgent";
    public const int MaxPacketBytes = 16 * 1024 * 1024;
    private const int HeaderBytes = 5;
    private const int FrameHeaderBytes = 25;
    private const int InputPayloadBytes = 16;

    public static byte[] EncodeFramePayload(CapturedFrame frame)
    {
        byte[] buffer = new byte[FrameHeaderBytes + frame.Data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), frame.Height);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8, 8), frame.Timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), frame.HostFps);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20, 4), frame.HostFrameTimeMs);
        buffer[24] = string.Equals(frame.Format, "h264", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
        if (frame.Data.Length > 0)
        {
            Buffer.BlockCopy(frame.Data, 0, buffer, FrameHeaderBytes, frame.Data.Length);
        }
        return buffer;
    }

    public static CapturedFrame? DecodeFramePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < FrameHeaderBytes)
            return null;

        int width = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(0, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(8, 8));
        int hostFps = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16, 4));
        int hostFrameTimeMs = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(20, 4));
        string format = payload[24] == 1 ? "h264" : "jpeg";

        byte[] data = payload.Length > FrameHeaderBytes
            ? payload[FrameHeaderBytes..].ToArray()
            : Array.Empty<byte>();

        return new CapturedFrame
        {
            Width = width,
            Height = height,
            Timestamp = timestamp,
            HostFps = hostFps,
            HostFrameTimeMs = hostFrameTimeMs,
            Format = format,
            Data = data
        };
    }

    public static byte[] EncodeInputPayload(RemoteInputMessage input)
    {
        byte[] buffer = new byte[InputPayloadBytes];
        buffer[0] = (byte)input.Type;
        buffer[1] = (byte)input.Button;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), input.WheelDelta);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), input.KeyCode);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(8, 4), input.NormalizedX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(12, 4), input.NormalizedY);
        return buffer;
    }

    public static RemoteInputMessage? DecodeInputPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < InputPayloadBytes)
            return null;

        return new RemoteInputMessage
        {
            Type = (InputEventType)payload[0],
            Button = (RemoteMouseButton)payload[1],
            WheelDelta = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(2, 2)),
            KeyCode = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)),
            NormalizedX = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(8, 4)),
            NormalizedY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(12, 4))
        };
    }

    public static byte[] EncodeJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    public static T? DecodeJson<T>(ReadOnlySpan<byte> payload) =>
        payload.IsEmpty ? default : JsonSerializer.Deserialize<T>(payload);

    public static async Task WritePacketAsync(
        Stream stream,
        SecureAgentMessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        if (payload.Length > MaxPacketBytes)
            throw new InvalidDataException($"SecureAgent packet size {payload.Length} exceeds limit.");

        byte[] header = new byte[HeaderBytes];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<SecureAgentPacket?> ReadPacketAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[HeaderBytes];
        int firstRead = await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (firstRead == 0)
            return null;

        await stream.ReadExactlyAsync(header.AsMemory(1, HeaderBytes - 1), ct).ConfigureAwait(false);
        var messageType = (SecureAgentMessageType)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPacketBytes)
            throw new InvalidDataException($"Invalid SecureAgent packet length: {length}");

        byte[] payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        return new SecureAgentPacket(messageType, payload);
    }
}
