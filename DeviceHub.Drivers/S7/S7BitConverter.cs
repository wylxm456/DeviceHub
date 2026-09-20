using System.Buffers.Binary;

namespace DeviceHub.Drivers.S7;

/// <summary>
/// S7 协议在大端字节序：Real/Int 的读写统一走这里，
/// 驱动（读端）与模拟器（写端）共用同一份转换，避免两端各写一套出不一致。
/// </summary>
public static class S7BitConverter
{
    public static float ToSingle(ReadOnlySpan<byte> span) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(span));

    public static void WriteSingle(Span<byte> span, float value) =>
        BinaryPrimitives.WriteInt32BigEndian(span, BitConverter.SingleToInt32Bits(value));

    public static short ToInt16(ReadOnlySpan<byte> span) =>
        BinaryPrimitives.ReadInt16BigEndian(span);

    public static void WriteInt16(Span<byte> span, short value) =>
        BinaryPrimitives.WriteInt16BigEndian(span, value);

    public static bool ToBool(byte data, int bit) => (data & (1 << bit)) != 0;
}
