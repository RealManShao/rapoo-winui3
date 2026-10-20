using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rapoo.Core;
using static Rapoo.Hid.NativeMethods;

namespace Rapoo.Hid;

/// <summary>Serializes native control transfers; listens only to vendor telemetry, never mouse movement.</summary>
public sealed class RapooMouseService : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SafeFileHandle? control;
    private HidDeviceInfo? controlInfo;
    private readonly List<FileStream> telemetryStreams = [];
    private readonly List<Task> telemetryTasks = [];
    private CancellationTokenSource? lifetime;
    private bool disposed;
    private BatteryReading? latestBatteryReading;
    public BatteryReading? LatestBatteryReading => Volatile.Read(ref latestBatteryReading);
    public bool IsConnected => control is { IsInvalid: false, IsClosed: false };
    public event Action<string>? Log;
    public event Action<BatteryReading>? BatteryChanged;
    public event Action? ConnectionLost;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await DisconnectCoreAsync();
            var devices = await Task.Run(HidDeviceInfo.Enumerate, cancellationToken);
            foreach (var device in devices) Log?.Invoke($"HID {device.Label} {device.Path}");
            var candidates = devices.Where(d => d.IsConfiguration).ToArray();
            if (candidates.Length == 0) throw new IOException("未找到 VT1 Air Max 配置接口（VID 24AE / PID 1234，Output BA）。请连接 2.4 GHz 接收器。");
            // Never broadcast settings to multiple receivers.
            if (candidates.Length > 1) throw new IOException("检测到多个配置接口，请只保留一个目标接收器后重新连接。");
            var target = candidates[0];
            control = CreateFile(target.Path, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (control.IsInvalid) throw NativeError("无法打开配置接口，请退出官方驱动后重试");
            controlInfo = target;
            lifetime = new CancellationTokenSource();
            foreach (var device in devices.Where(d => d.IsTelemetry))
            {
                var handle = CreateFile(device.Path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); Log?.Invoke("无法打开电量输入接口，配置功能仍可使用。"); continue; }
                try
                {
                    var stream = new FileStream(handle, FileAccess.Read, device.InputLength, true);
                    telemetryStreams.Add(stream);
                    telemetryTasks.Add(ReadTelemetryAsync(stream, device.InputLength, lifetime.Token));
                }
                catch { handle.Dispose(); throw; }
            }
            Log?.Invoke(telemetryStreams.Count > 0 ? "接收器已连接，正在监听电量。" : "接收器已连接，未找到可读的 BB 电量接口。");
        }
        catch { await DisconnectCoreAsync(); throw; }
        finally { gate.Release(); }
    }

    public Task SetDpiAsync(IReadOnlyList<DpiStage> stages, CancellationToken token = default) =>
        SendTransactionAsync(RapooProtocol.CreateDpiTransaction(stages), token);

    public Task SetPollingRateAsync(PollingRateOption option, CancellationToken token = default) =>
        SendTransactionAsync(RapooProtocol.CreatePollingRateTransaction(option), token);

    public async Task<int?> ReadPollingRateAsync(CancellationToken token = default)
    {
        var response = await ReadConfigurationResponseAsync(false, token);
        return response is not null && RapooProtocol.TryParsePollingRateResponse(response, out var rate) ? rate : null;
    }

    public async Task<MouseConfiguration?> ReadDeviceConfigurationAsync(CancellationToken token = default)
    {
        var response = await ReadConfigurationResponseAsync(true, token);
        return response is not null && RapooProtocol.TryParseDeviceConfigurationResponse(response, out var configuration) ? configuration : null;
    }

    private async Task<byte[]?> ReadConfigurationResponseAsync(bool includeDpi, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsConnected || controlInfo!.InputLength < (includeDpi ? 28 : 8) || !controlInfo.InputIds.Contains(0xBA))
                return null;
            return await Task.Run(async () =>
            {
                var query = includeDpi ? RapooProtocol.CreateDeviceConfigurationQuery() : RapooProtocol.CreatePollingRateQuery();
                if (!HidD_SetOutputReport(control!, query, query.Length)) throw NativeError("回报率读取请求失败");
                Log?.Invoke($"TX {Convert.ToHexString(query)}");
                var response = new byte[controlInfo.InputLength];
                // A cold receiver can initially return ACK 02 00 instead of the data.
                // Bound retries to this explicit transaction; never start a background poll.
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(50, token);
                    Array.Clear(response);
                    response[0] = RapooProtocol.ConfigurationReportId;
                    if (!HidD_GetInputReport(control!, response, response.Length)) throw NativeError("回报率读取应答失败");
                    Log?.Invoke($"RX {Convert.ToHexString(response)}");
                    if (includeDpi ? RapooProtocol.TryParseDeviceConfigurationResponse(response, out _) :
                        RapooProtocol.TryParsePollingRateResponse(response, out _)) return response;
                }
                Log?.Invoke("配置应答格式未识别，当前值保持未知。");
                return null;
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            // Optional status read failures must not discard a working configuration connection.
            Log?.Invoke($"回报率读取失败：{ex.Message}");
            return null;
        }
        finally { gate.Release(); }
    }

    private async Task SendTransactionAsync(IReadOnlyList<byte[]> reports, CancellationToken token)
    {
        await gate.WaitAsync(token);
        var written = 0;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsConnected) throw new IOException("接收器未连接，请先连接设备。");
            // Run synchronous HidD IOCTLs off the UI thread. Keep the handle alive under gate.
            await Task.Run(async () =>
            {
                foreach (var report in reports)
                {
                    token.ThrowIfCancellationRequested();
                    if (!HidD_SetOutputReport(control!, report, report.Length)) throw NativeError("HID Output Report 写入失败");
                    written++;
                    Log?.Invoke($"TX {Convert.ToHexString(report)}");
                    // Captured driver queries Input BA twice, at roughly 50 ms intervals.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        await Task.Delay(50, token);
                        if (controlInfo!.InputLength < RapooProtocol.ReportLength || !controlInfo.InputIds.Contains(0xBA)) continue;
                        var response = new byte[controlInfo.InputLength];
                        response[0] = 0xBA;
                        if (HidD_GetInputReport(control!, response, response.Length))
                            Log?.Invoke($"RX {Convert.ToHexString(response)}");
                        else Log?.Invoke($"BA 输入应答不可用，Win32={Marshal.GetLastWin32Error()}；继续抓包中的写入序列。");
                    }
                }
            }, token);
            Log?.Invoke("整组配置报文已发送。");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"配置中断（已发送 {written}/{reports.Count} 条）：{ex.Message}。设备可能已接收部分设置，请重新连接后完整应用。");
            await DisconnectCoreAsync();
            ConnectionLost?.Invoke();
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task ReadTelemetryAsync(FileStream stream, int reportLength, CancellationToken token)
    {
        var buffer = new byte[reportLength];
        var previousReport = new byte[reportLength];
        var previousCount = 0;
        string? reportLog = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) throw new IOException("电量输入接口已断开。");
                if (RapooProtocol.TryParseBattery(buffer.AsSpan(0, count), out var reading))
                {
                    var previous = Interlocked.Exchange(ref latestBatteryReading, reading);
                    if (previousCount != count || !buffer.AsSpan(0, count).SequenceEqual(previousReport.AsSpan(0, count)))
                    {
                        buffer.AsSpan(0, count).CopyTo(previousReport);
                        previousCount = count;
                        reportLog = $"BAT {Convert.ToHexString(buffer.AsSpan(0, count))}";
                    }
                    Log?.Invoke(reportLog!);
                    // Repeated reports only replace the cache/timestamp; no UI dispatch.
                    if (previous is null || previous.Percent != reading!.Percent || previous.CurrentDpi != reading.CurrentDpi)
                        BatteryChanged?.Invoke(reading!);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Log?.Invoke($"电量监听中断：{ex.Message}");
                ConnectionLost?.Invoke();
            }
        }
    }

    private async Task DisconnectCoreAsync()
    {
        if (lifetime is not null) await lifetime.CancelAsync();
        foreach (var stream in telemetryStreams) stream.Dispose();
        await Task.WhenAll(telemetryTasks);
        telemetryStreams.Clear(); telemetryTasks.Clear();
        Interlocked.Exchange(ref latestBatteryReading, null);
        lifetime?.Dispose(); lifetime = null;
        control?.Dispose(); control = null; controlInfo = null;
    }

    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; await DisconnectCoreAsync(); }
        finally { gate.Release(); }
    }

    private static Win32Exception NativeError(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new(error, $"{operation}（Win32 {error}：{new Win32Exception(error).Message}）");
    }
}
