using System.Buffers.Binary;

namespace Rapoo.Core;

public static partial class RapooProtocol
{
    public static IReadOnlyList<int> DebounceOptions { get; } = Array.AsReadOnly(new[] { 1, 2, 4, 8, 16, 24, 32 });
    public static IReadOnlyList<LodOption> LodOptions { get; } = Array.AsReadOnly(new[]
        { new LodOption(1, "0.7 mm"), new LodOption(2, "1 mm"), new LodOption(3, "2 mm") });
    private static bool ValidDpi(int dpi) => dpi >= MinDpi && dpi <= MaxDpi && dpi % DpiStep == 0;

    // Responses carry no register address. Call only inside the matching explicit read transaction.
    public static bool TryParseRegisterResponse(ReadOnlySpan<byte> response, int length, out byte[] data)
    {
        data = [];
        if (!response.IsEmpty && response[0] == ConfigurationReportId) response = response[1..];
        if (length < 1 || length > 24 || response.Length < length + 4 ||
            !response[..4].SequenceEqual(new byte[] { 1, 0, 0, 0 })) return false;
        data = response.Slice(4, length).ToArray();
        return true;
    }

    public static byte[] CreateRegisterQuery(byte bank, byte address, byte length)
    {
        if (length is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(length));
        var report = Header(0xA4, length, address);
        report[5] = bank;
        return report;
    }

    public static bool TryParseDpiYResponse(ReadOnlySpan<byte> response, out DpiStage[] stages)
    {
        stages = [];
        if (!TryParseRegisterResponse(response, 16, out var data) || data[14] >= StageCount) return false;
        var decoded = new DpiStage[StageCount];
        for (var i = 0; i < StageCount; i++)
        {
            var dpi = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2));
            if (!ValidDpi(dpi)) return false;
            decoded[i] = new(dpi, i <= data[14]);
        }
        stages = decoded;
        return true;
    }

    public static void ValidateSensorSettings(SensorSettings value)
    {
        if (value.LodCode is < 1 or > 3 || !DebounceOptions.Contains(value.PressDebounceMs) ||
            !DebounceOptions.Contains(value.ReleaseDebounceMs) || value.SleepMinutes is < 5 or > 120 ||
            value.Angle is < -30 or > 30)
            throw new ArgumentException("参数无效：LOD 仅支持 0.7/1/2 mm，去抖请选择提供的毫秒值，休眠为 5–120 分钟，角度为 -30°–30°。");
    }

    public static bool TryDecodeSensors(byte[] block84, byte[] blockC0, byte[] blockC4, out SensorConfiguration? configuration)
    {
        configuration = null;
        if (block84.Length != 4 || blockC0.Length != 4 || blockC4.Length != 4 ||
            block84[1] > 1 || blockC4[1] > 1) return false;
        var settings = new SensorSettings(block84[1] == 1, (blockC0[3] & 1) == 0, (blockC0[3] & 2) == 0,
            blockC4[1] == 1, block84[0], blockC0[0], blockC0[1], blockC0[2], unchecked((sbyte)blockC4[0]));
        try { ValidateSensorSettings(settings); } catch (ArgumentException) { return false; }
        configuration = new(settings, block84, blockC0, blockC4);
        return true;
    }

    // gen1-features + gen1-parameters captures: full four-byte blocks, read/preserve, then commit.
    public static IReadOnlyList<byte[]> CreateSensorTransaction(SensorConfiguration original, SensorSettings settings)
    {
        ValidateSensorSettings(settings);
        var b84 = original.Block84;
        var c0 = original.BlockC0;
        var c4 = original.BlockC4;
        if (b84.Length != 4 || c0.Length != 4 || c4.Length != 4) throw new ArgumentException("设备参数块长度无效。");
        b84[0] = (byte)settings.LodCode;
        b84[1] = (byte)(settings.MotionSync ? 1 : 0);
        c0[0] = (byte)settings.PressDebounceMs;
        c0[1] = (byte)settings.ReleaseDebounceMs;
        c0[2] = (byte)settings.SleepMinutes;
        c0[3] = (byte)((c0[3] & ~3) | (settings.LinearCorrection ? 0 : 1) | (settings.RippleCorrection ? 0 : 2));
        c4[0] = unchecked((byte)(sbyte)settings.Angle);
        c4[1] = (byte)(settings.GlassTracking ? 1 : 0);
        return [RegisterWrite(0x08, 0xC0, c0), CreateRegisterQuery(0x08, 0xC4, 4),
            RegisterWrite(0x08, 0xC4, c4), CreateRegisterQuery(0x08, 0x84, 4), RegisterWrite(0x08, 0x84, b84), Commit()];
    }

    private static byte[] RegisterWrite(byte bank, byte address, byte[] data)
    {
        if (data.Length != 4) throw new ArgumentException("参数写入必须保留完整四字节块。");
        var report = Header(0xA5, 4, address);
        report[5] = bank;
        data.CopyTo(report, 8);
        return report;
    }
}
