using System.Buffers.Binary;

namespace Rapoo.Core;

/// <summary>Exact 32-byte Output Reports reconstructed from the repository's USBPcap captures.</summary>
public static partial class RapooProtocol
{
    public const ushort VendorId = 0x24AE;
    public const ushort ProductId = 0x1234;
    public const byte ConfigurationReportId = 0xBA;
    public const byte BatteryReportId = 0xBB;
    public const int ReportLength = 32;
    public const int StageCount = 7;
    public const int MinDpi = 50;
    public const int MaxDpi = 45000;
    public const int DpiStep = 50;

    public static IReadOnlyList<PollingRateOption> PollingRates(IReadOnlyDictionary<int, byte>? extraCodes = null)
    {
        Dictionary<int, byte> known = new()
        {
            // 125hz.pcapng: frame 11.
            [125] = 0x08,
            [250] = 0x04, [500] = 0x02, [1000] = 0x01, [2000] = 0x84,
            // 1k-4k-8k.pcapng: frames 1931 (4 kHz) and 3995 (8 kHz).
            [4000] = 0x82, [8000] = 0x81
        };
        return new[] { 125, 250, 500, 1000, 2000, 4000, 8000 }.Select(rate =>
            known.TryGetValue(rate, out var code) ? new PollingRateOption(rate, code) :
            extraCodes is not null && extraCodes.TryGetValue(rate, out code) ? new(rate, code, true) :
            new PollingRateOption(rate, null)).ToArray();
    }

    public static void ValidateStages(IReadOnlyList<DpiStage> stages)
    {
        if (stages.Count != StageCount) throw new ArgumentException("必须配置 7 个 DPI 档位。");
        if (!stages.Any(s => s.Enabled)) throw new ArgumentException("至少启用一个 DPI 档位。");
        for (var i = 0; i < stages.Count; i++)
            if (!ValidDpi(stages[i].Dpi) || !ValidDpi(stages[i].EffectiveYDpi))
                throw new ArgumentException($"档位 {i + 1} 的 DPI 必须为 50–45000 之间的 50 的倍数。");
    }

    public static IReadOnlyList<byte[]> CreateDpiTransaction(IReadOnlyList<DpiStage> stages)
    {
        ValidateStages(stages);
        var first = Header(0xA5, 0x10, 0x88);
        // Enabled stages precede disabled stages; retain the UI order within each group.
        var ordered = stages.Where(s => s.Enabled).Concat(stages.Where(s => !s.Enabled)).ToArray();
        for (var i = 0; i < StageCount; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(first.AsSpan(8 + i * 2), (ushort)ordered[i].Dpi);
        first[22] = (byte)(stages.Count(s => s.Enabled) - 1);
        var second = (byte[])first.Clone();
        second[4] = 0xC8;
        for (var i = 0; i < StageCount; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(second.AsSpan(8 + i * 2), (ushort)ordered[i].EffectiveYDpi);
        return [Header(0xA4, 0x18, 0x98), first, second, Header(0xA4, 0x18, 0x98), Commit()];
    }

    public static IReadOnlyList<byte[]> CreatePollingRateTransaction(PollingRateOption option)
    {
        if (!PollingRates().Any(r => r.Hertz == option.Hertz) || option.Code is null)
            throw new ArgumentException("这个回报率尚未配置协议代码。");
        var write = Header(0xA5, 0x04, 0x80);
        write[8] = option.Code.Value;
        write[9] = 0x01;
        write[10] = 0x01;
        write[11] = 0xFF;
        return [Header(0xA4, 0x04, 0x80), write, Commit(), Header(0xA4, 0x18, 0x80)];
    }

    public static bool TryParseBattery(ReadOnlySpan<byte> report, out BatteryReading? reading)
    {
        reading = null;
        // HID buffers can be padded to the collection's longest input report length.
        if (report.Length < 7 || report[0] != 0xBB || report[1] != 0xB0 || report[2] != 0x71 ||
            report[5] != 0x01 || report[6] > 100) return false;
        reading = new(report[6], BinaryPrimitives.ReadUInt16LittleEndian(report[3..5]), DateTimeOffset.Now);
        return true;
    }

    public static byte[] CreatePollingRateQuery() => Header(0xA4, 0x04, 0x80);
    public static byte[] CreateDeviceConfigurationQuery() => Header(0xA4, 0x18, 0x80);

    public static bool TryParseDeviceConfigurationResponse(ReadOnlySpan<byte> response, out MouseConfiguration? configuration)
    {
        configuration = null;
        if (!TryParsePollingRateResponse(response, out var rate)) return false;
        if (response[0] == ConfigurationReportId) response = response[1..];
        // Four status bytes, then 24 bytes from 0x0880. DPI starts at 0x0888.
        if (response.Length < 28 || response[26] >= StageCount) return false;
        var stages = new DpiStage[StageCount];
        for (var i = 0; i < StageCount; i++)
        {
            var dpi = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(12 + i * 2, 2));
            if (dpi < MinDpi || dpi > MaxDpi || dpi % DpiStep != 0) return false;
            stages[i] = new(dpi, i <= response[26]);
        }
        configuration = new(stages, rate);
        return true;
    }

    // Only parse responses to an explicit 0x0880 read, never arbitrary BA acknowledgements.
    public static bool TryParsePollingRateResponse(ReadOnlySpan<byte> response, out int hertz)
    {
        hertz = 0;
        if (!response.IsEmpty && response[0] == ConfigurationReportId) response = response[1..];
        if (response.Length < 8 || response[0] != 1 || response[1] != 0 || response[2] != 0 || response[3] != 0 ||
            response[5] != 1 || response[6] != 1 || response[7] != 0xFF) return false;
        hertz = response[4] switch
        {
            0x08 => 125, 0x04 => 250, 0x02 => 500, 0x01 => 1000,
            0x84 => 2000, 0x82 => 4000, 0x81 => 8000, _ => 0
        };
        return hertz != 0;
    }

    private static byte[] Header(byte operation, byte length, byte address)
    {
        var bytes = new byte[ReportLength];
        bytes[0] = 0xBA;
        bytes[1] = 0xA5;
        bytes[2] = operation;
        bytes[3] = length;
        bytes[4] = address;
        bytes[5] = 0x08;
        return bytes;
    }

    private static byte[] Commit()
    {
        var bytes = new byte[ReportLength];
        bytes[0] = 0xBA; bytes[1] = 0xA5; bytes[2] = 0xA3;
        return bytes;
    }
}
