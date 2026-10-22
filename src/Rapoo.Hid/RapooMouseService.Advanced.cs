using Rapoo.Core;
using static Rapoo.Hid.NativeMethods;

namespace Rapoo.Hid;

public sealed partial class RapooMouseService
{
    // Every helper below runs under gate, including fresh reads, writes, commit and verification.
    private async Task<T> WithDeviceAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsConnected) throw new IOException("接收器未连接，请重新连接。");
            return await Task.Run(operation, token);
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> ReadBlockCoreAsync(byte bank, byte address, byte length, CancellationToken token,
        Func<byte[], bool>? validate = null)
    {
        if (controlInfo!.InputLength < length + 5 || !controlInfo.InputIds.Contains(0xBA))
            throw new IOException("设备输入报告长度不足，无法读取配置。");
        var query = RapooProtocol.CreateRegisterQuery(bank, address, length);
        await Task.Delay(50, token); // Match the driver's spacing before the next control command.
        WriteCore(query);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(50, token);
            var response = ReadCore();
            if (RapooProtocol.TryParseRegisterResponse(response, length, out var data) &&
                (validate is null || validate(data)))
            { await Task.Delay(50, token); return data; }
        }
        throw new IOException($"读取 {bank:X2}{address:X2} 配置失败，请唤醒鼠标并重新连接。");
    }

    private void WriteCore(byte[] report)
    {
        if (!HidD_SetOutputReport(control!, report, report.Length)) throw NativeError("配置写入失败");
        Log?.Invoke($"TX {Convert.ToHexString(report)}");
    }

    private byte[] ReadCore()
    {
        var response = new byte[controlInfo!.InputLength];
        response[0] = 0xBA;
        if (!HidD_GetInputReport(control!, response, response.Length)) throw NativeError("配置读取失败");
        Log?.Invoke($"RX {Convert.ToHexString(response)}");
        return response;
    }

    private async Task WriteReportsCoreAsync(IReadOnlyList<byte[]> reports, CancellationToken token, int responseReads = 2)
    {
        foreach (var report in reports)
        {
            token.ThrowIfCancellationRequested();
            WriteCore(report);
            for (var i = 0; i < responseReads; i++) { await Task.Delay(50, token); ReadCore(); }
        }
    }

    private static byte[] WithStatus(byte[] data) => new byte[] { 1, 0, 0, 0 }.Concat(data).ToArray();

    private async Task<MouseConfiguration?> ReadFullConfigurationAsync(CancellationToken token)
    {
        try
        {
            return await WithDeviceAsync<MouseConfiguration?>(async () =>
            {
                var x = await ReadBlockCoreAsync(8, 0x80, 24, token,
                    d => RapooProtocol.TryParseDeviceConfigurationResponse(WithStatus(d), out _));
                RapooProtocol.TryParseDeviceConfigurationResponse(WithStatus(x), out var config);
                var y = await ReadBlockCoreAsync(8, 0xC8, 16, token,
                    d => RapooProtocol.TryParseDpiYResponse(WithStatus(d), out _));
                RapooProtocol.TryParseDpiYResponse(WithStatus(y), out var yStages);
                if (config!.Stages.Where((s, i) => s.Enabled != yStages[i].Enabled).Any())
                    throw new IOException("X/Y 档位启用数量不一致，请重新读取配置。");
                return config with { Stages = config.Stages.Select((s, i) => s with { YDpi = yStages[i].Dpi }).ToArray() };
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        { Log?.Invoke($"DPI 配置读取失败：{ex.Message}"); return null; }
    }

    private async Task<SensorConfiguration> ReadSensorsCoreAsync(CancellationToken token)
    {
        var b84 = await ReadBlockCoreAsync(8, 0x84, 4, token, d => d[0] is >= 1 and <= 3 && d[1] <= 1);
        var c0 = await ReadBlockCoreAsync(8, 0xC0, 4, token, d =>
            RapooProtocol.DebounceOptions.Contains(d[0]) && RapooProtocol.DebounceOptions.Contains(d[1]) && d[2] is >= 5 and <= 120);
        var c4 = await ReadBlockCoreAsync(8, 0xC4, 4, token, d =>
            unchecked((sbyte)d[0]) is >= -30 and <= 30 && d[1] <= 1);
        if (!RapooProtocol.TryDecodeSensors(b84, c0, c4, out var configuration))
            throw new IOException("设备参数格式未识别，未采用默认值。");
        return configuration!;
    }

    public async Task<SensorConfiguration?> ReadSensorConfigurationAsync(CancellationToken token = default)
    {
        try { return await WithDeviceAsync(() => ReadSensorsCoreAsync(token), token); }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        { Log?.Invoke($"参数读取失败：{ex.Message}"); return null; }
    }

    public Task<SensorConfiguration> SetSensorSettingsAsync(SensorSettings settings, CancellationToken token = default)
    {
        RapooProtocol.ValidateSensorSettings(settings);
        return WithDeviceAsync(async () =>
        {
            var original = await ReadSensorsCoreAsync(token);
            // The 2026-10-22 feature capture has one Input BA response for each output command.
            await WriteReportsCoreAsync(RapooProtocol.CreateSensorTransaction(original, settings), token, responseReads: 1);
            return await ReadSensorsCoreAsync(token);
        }, token);
    }

    private async Task<byte[]> ReadDiyCoreAsync(CancellationToken token)
    {
        // The captured 24-byte read at 0630 contains DIY's four bytes at offset 4.
        var block = await ReadBlockCoreAsync(6, 0x30, 24, token,
            d => d.AsSpan(0, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }) &&
                !d.AsSpan(4, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }));
        return block.AsSpan(4, 4).ToArray();
    }

    public async Task<byte[]?> ReadDiyActionAsync(CancellationToken token = default)
    {
        try { return await WithDeviceAsync(() => ReadDiyCoreAsync(token), token); }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        { Log?.Invoke($"DIY 按键读取失败：{ex.Message}"); return null; }
    }

    public Task<byte[]> SetDiyActionAsync(DiyAction action, CancellationToken token = default)
    {
        var reports = RapooProtocol.CreateDiyTransaction(action);
        return WithDeviceAsync(async () =>
        {
            await ReadDiyCoreAsync(token); // Refuse writes if current device configuration cannot be read.
            await WriteReportsCoreAsync(reports, token);
            return await ReadDiyCoreAsync(token);
        }, token);
    }
}
