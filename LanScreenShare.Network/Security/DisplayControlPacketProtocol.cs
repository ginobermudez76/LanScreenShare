using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Network.Security;

public readonly record struct DisplayControlPacketHeader(
    ulong SequenceNumber,
    ulong Timestamp,
    DisplayControlMessageKind Kind,
    int PayloadLength);

/// <summary>
/// Protocolo binario/JSON aislado ("LSDC") para mensajes de control de sesión de pantalla inalámbrica
/// sobre el canal 5003, separado de <see cref="LanScreenShare.Core.Models.InputEventType"/>.
/// </summary>
public static class DisplayControlPacketProtocol
{
    public const uint MagicLsdc = 0x4C534443; // "LSDC"
    public const int HeaderSize = Lsi2ControlPacket.PacketSize;
    public const int MaxPayloadBytes = 4096;

    public static bool IsDisplayControlHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            return false;

        return BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0, 4)) == MagicLsdc;
    }

    public static bool TryParseHeader(
        ReadOnlySpan<byte> source,
        out DisplayControlPacketHeader header,
        out string? error)
    {
        header = default;
        if (!IsDisplayControlHeader(source))
        {
            error = "Invalid display control magic.";
            return false;
        }

        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(4, 8));
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(12, 8));
        uint rawKind = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(20, 4));
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(source.Slice(24, 4));

        if (!Enum.IsDefined((DisplayControlMessageKind)rawKind))
        {
            error = $"Unknown DisplayControlMessageKind: {rawKind}";
            return false;
        }

        if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
        {
            error = $"Invalid display control payload length: {payloadLength}";
            return false;
        }

        header = new DisplayControlPacketHeader(sequence, timestamp, (DisplayControlMessageKind)rawKind, payloadLength);
        error = null;
        return true;
    }

    public static byte[] SerializePacket(ulong sequenceNumber, DisplaySessionControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (jsonBytes.Length == 0 || jsonBytes.Length > MaxPayloadBytes)
            throw new InvalidOperationException($"Payload LSDC fuera de límites ({jsonBytes.Length} bytes).");

        byte[] packet = new byte[HeaderSize + jsonBytes.Length];
        Span<byte> header = packet.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(0, 4), MagicLsdc);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(4, 8), sequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(12, 8), (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(20, 4), (uint)message.Kind);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(24, 4), jsonBytes.Length);

        jsonBytes.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    public static async Task WriteMessageAsync(
        Stream stream,
        ulong sequenceNumber,
        DisplaySessionControlMessage message,
        CancellationToken ct = default)
    {
        byte[] packet = SerializePacket(sequenceNumber, message);
        await stream.WriteAsync(packet, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<DisplaySessionControlMessage> ReadMessagePayloadAsync(
        Stream stream,
        int payloadLength,
        CancellationToken ct = default)
    {
        if (payloadLength <= 0 || payloadLength > MaxPayloadBytes)
            throw new InvalidDataException($"Longitud de payload LSDC inválida: {payloadLength}");

        byte[] buffer = new byte[payloadLength];
        await StreamConnectionHelper.ReadExactBytesAsync(
            stream,
            buffer,
            ct,
            "Flujo cerrado antes de completar lectura del mensaje de control de pantalla.");

        var message = JsonSerializer.Deserialize<DisplaySessionControlMessage>(buffer);
        return message ?? throw new InvalidDataException("No se pudo deserializar DisplaySessionControlMessage.");
    }
}
