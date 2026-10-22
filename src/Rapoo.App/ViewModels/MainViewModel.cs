using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Rapoo.Core;
using Rapoo.Hid;

namespace Rapoo.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly RapooMouseService mouse = new();
    private readonly ConfigurationStore store = new();
    private readonly RecentCommunicationLog logs = new();
    private volatile bool isLogExpanded;
    private int logRefreshPending, batteryRefreshPending;
    private bool hasDeviceConfiguration, loadingDeviceConfiguration;
    private bool busy, connected;
    private volatile bool closing;
    private BatteryReading? battery;
    private PollingRateOption? selectedRate;
    private int? currentPollingRate;
    private string message = "配置只在点击应用后写入设备。", dpiStatus = "尚未发送", rateStatus = "尚未发送";
    private InfoBarSeverity severity = InfoBarSeverity.Informational;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        IReadOnlyDictionary<int, byte>? codes = null;
        try { codes = store.LoadExtraRateCodes(); }
        catch (Exception ex) { message = $"自定义协议配置无法读取：{ex.Message}"; severity = InfoBarSeverity.Warning; }
        Stages = new(Enumerable.Range(0, RapooProtocol.StageCount).Select(i => new DpiStageViewModel(i)));
        Rates = RapooProtocol.PollingRates(codes);
        foreach (var stage in Stages) stage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(DpiStageViewModel.Dpi) or nameof(DpiStageViewModel.DpiY) or nameof(DpiStageViewModel.Enabled))) return;
            if (loadingDeviceConfiguration) return;
            UpdateCurrentDpiStage();
            DpiStatus = "有待应用的更改";
            Refresh();
        };
        mouse.Log += AddLog;
        mouse.BatteryChanged += _ => QueueBatteryRefresh();
        mouse.ConnectionLost += () => dispatcher.TryEnqueue(() =>
        {
            if (closing) return;
            ClearBattery(); Connected = false;
            Show("设备连接中断，请重新连接后应用设置。", InfoBarSeverity.Warning);
        });
        InitializeAdvanced();
    }

    public ObservableCollection<DpiStageViewModel> Stages { get; }
    public IReadOnlyList<PollingRateOption> Rates { get; }
    public PollingRateOption? SelectedRate
    {
        get => selectedRate;
        set { if (Set(ref selectedRate, value)) { RateStatus = "有待应用的更改"; Refresh(); } }
    }
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Refresh(); } }
    public bool Connected { get => connected; private set { if (Set(ref connected, value)) Refresh(); } }
    public bool CanConnect => !Busy;
    public bool CanEdit => Connected && !Busy && hasDeviceConfiguration;
    public bool CanApplyDpi => Connected && !Busy && ValidationError is null;
    public bool CanApplyRate => Connected && !Busy && SelectedRate?.IsSupported == true;
    public string ConnectionText => Connected ? "接收器已连接" : "接收器未连接";
    public string ConnectionDetail => Connected ? "2.4 GHz · VID 24AE / PID 1234" : "连接 2.4 GHz USB 接收器后点击重新连接";
    public string ConnectLabel => Connected ? "重新连接" : "连接设备";
    public string EnabledCount => hasDeviceConfiguration ? $"{Stages.Count(s => s.Enabled)} / 7 个档位启用" : "等待设备配置";
    public string? ValidationError
    {
        get { if (!hasDeviceConfiguration) return "尚未读取鼠标配置，请连接设备或重新连接重试。";
            try { RapooProtocol.ValidateStages(Snapshot()); return null; } catch (ArgumentException ex) { return ex.Message; } }
    }
    public string DpiHint => ValidationError ?? "范围 50–45000 DPI，步进 50；鼠标 DPI 按键按启用档位循环。";
    public string RateHint => SelectedRate is null ? "请选择回报率。" :
        !SelectedRate.IsSupported ? "此选项缺少协议代码。可在本地 protocol-overrides.json 中补充。" :
        SelectedRate.Custom ? $"使用本地提供的代码 0x{SelectedRate.Code:X2}。" : $"报告间隔 {1000.0 / SelectedRate.Hertz:0.###} ms · 协议代码 0x{SelectedRate.Code:X2}";
    public string BatteryText => battery is null ? "—" : $"{battery.Percent}%";
    public double BatteryValue => battery?.Percent ?? 0;
    public string BatteryDetail => !Connected ? "等待设备连接" : battery is null ? "等待鼠标上报电量，请唤醒鼠标" : "来自最近收到的设备报告";
    public string CurrentDpi => battery is null ? "—" : $"{battery.CurrentDpi:N0}";
    public string CurrentPollingRate => currentPollingRate is int rate ? $"{rate:N0} Hz" : "—";
    public string PollingRateDetail => !Connected ? "等待设备连接" : currentPollingRate is null ?
        "未能读取，重新连接可重试" : "设备配置读回值 · 不持续轮询";
    public string DpiStatus { get => dpiStatus; private set => Set(ref dpiStatus, value); }
    public string RateStatus { get => rateStatus; private set => Set(ref rateStatus, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public InfoBarSeverity Severity { get => severity; private set => Set(ref severity, value); }
    public string LogText => IsLogExpanded ? logs.Format() : "";
    public bool IsLogExpanded
    {
        get => isLogExpanded;
        set
        {
            if (isLogExpanded == value) return;
            isLogExpanded = value;
            Notify();
            Notify(nameof(LogText));
        }
    }
    public string SettingsDirectory => store.DirectoryPath;

    public async Task ConnectAsync()
    {
        if (Busy || closing) return;
        ClearBattery(); Busy = true; Connected = false;
        try
        {
            await mouse.ConnectAsync();
            if (closing) return;
            Connected = true;
            QueueBatteryRefresh();
            var configuration = await mouse.ReadDeviceConfigurationAsync();
            if (closing || !Connected) return;
            LoadDeviceConfiguration(configuration);
            await LoadAdvancedAsync();
            if (closing || !Connected) return;
            RateStatus = currentPollingRate is int readRate ? $"设备读回 {readRate:N0} Hz" : "当前回报率读取失败，可重新连接重试";
            DpiStatus = configuration is null ? "设备配置读取失败" : "已读取鼠标 DPI 档位配置";
            Show(configuration is null ? "已连接，但配置读取失败，请重新连接重试。" :
                "已读取鼠标实际配置。修改后点击应用才写入设备；电量与当前 DPI 来自鼠标报告。",
                configuration is null || !sensorLoaded || !diyLoaded ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            if (configuration is not null && (!sensorLoaded || !diyLoaded))
                Show("已连接，部分设置读取失败；对应编辑区已禁用，请唤醒鼠标后重新连接。", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { if (!closing) { Show(ex.Message, InfoBarSeverity.Warning); AddLog(ex.ToString()); } }
        finally { Busy = false; }
    }

    public async Task ApplyDpiAsync()
    {
        if (!CanApplyDpi || closing) return;
        Busy = true;
        try
        {
            var stages = Snapshot();
            await mouse.SetDpiAsync(stages);
            var configuration = await mouse.ReadDeviceConfigurationAsync();
            if (closing || !Connected) return;
            LoadDeviceConfiguration(configuration);
            var expected = stages.Where(s => s.Enabled).Concat(stages.Where(s => !s.Enabled)).ToArray();
            var confirmed = configuration is not null && configuration.Stages.Select((s, i) =>
                s.Dpi == expected[i].Dpi && s.EffectiveYDpi == expected[i].EffectiveYDpi && s.Enabled == expected[i].Enabled).All(v => v);
            DpiStatus = configuration is null ? "已发送，但设备配置读回失败，请重新连接" : confirmed ?
                $"{DateTime.Now:HH:mm:ss} X/Y DPI 已读回确认" : "设备读回与请求不同，已显示实际配置，请检查后重试";
            Show(DpiStatus, confirmed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { DpiStatus = "发送未完成，请重新应用"; Show(ex.Message, InfoBarSeverity.Error); }
        finally { Busy = false; }
    }

    public async Task ApplyRateAsync()
    {
        if (!CanApplyRate || closing) return;
        Busy = true;
        try
        {
            var rate = SelectedRate!;
            await mouse.SetPollingRateAsync(rate);
            SetCurrentPollingRate(await mouse.ReadPollingRateAsync());
            if (closing || !Connected) return;
            RateStatus = $"{DateTime.Now:HH:mm:ss} 已发送 {rate.Hertz:N0} Hz";
            Show($"{rate.Hertz:N0} Hz 回报率配置已发送。", InfoBarSeverity.Success);
            if (currentPollingRate == rate.Hertz)
                RateStatus = $"{DateTime.Now:HH:mm:ss} 设备读回确认 {rate.Hertz:N0} Hz";
            else if (currentPollingRate is int actual)
            {
                RateStatus = $"已发送 {rate.Hertz:N0} Hz，但设备读回 {actual:N0} Hz";
                Show(RateStatus + "，请重新应用。", InfoBarSeverity.Warning);
            }
            else RateStatus += "，读回未确认";
        }
        catch (Exception ex)
        {
            SetCurrentPollingRate(null);
            RateStatus = "发送未完成，请重新应用";
            Show(ex.Message, InfoBarSeverity.Error);
        }
        finally { Busy = false; }
    }

    private void LoadDeviceConfiguration(MouseConfiguration? configuration)
    {
        loadingDeviceConfiguration = true;
        try
        {
            hasDeviceConfiguration = configuration is not null;
            independentDpi = configuration?.Stages.Any(s => s.Dpi != s.EffectiveYDpi) == true;
            Notify(nameof(IndependentDpi));
            for (var i = 0; i < Stages.Count; i++)
            {
                Stages[i].Independent = true;
                Stages[i].Dpi = configuration?.Stages[i].Dpi ?? double.NaN;
                Stages[i].DpiY = configuration?.Stages[i].EffectiveYDpi ?? double.NaN;
                Stages[i].Enabled = configuration?.Stages[i].Enabled ?? false;
                Stages[i].Independent = independentDpi;
            }
            SetCurrentPollingRate(configuration?.PollingRate);
        }
        finally { loadingDeviceConfiguration = false; }
        Refresh();
    }

    private void QueueBatteryRefresh()
    {
        if (Interlocked.Exchange(ref batteryRefreshPending, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref batteryRefreshPending, 0);
            if (closing || !Connected) return;
            var latest = mouse.LatestBatteryReading;
            if (battery?.Percent == latest?.Percent && battery?.CurrentDpi == latest?.CurrentDpi) return;
            battery = latest;
            NotifyBatteryState();
        })) Interlocked.Exchange(ref batteryRefreshPending, 0);
    }

    private void ClearBattery()
    {
        ClearAdvanced();
        LoadDeviceConfiguration(null);
        battery = null;
        NotifyBatteryState();
    }

    private void NotifyBatteryState()
    {
        Notify(nameof(BatteryText)); Notify(nameof(BatteryValue)); Notify(nameof(BatteryDetail)); Notify(nameof(CurrentDpi));
        UpdateCurrentDpiStage();
    }

    private void UpdateCurrentDpiStage()
    {
        foreach (var stage in Stages)
            stage.IsCurrentDpiMatch = Connected && battery is not null && stage.Enabled && stage.Dpi == battery.CurrentDpi;
    }

    private void SetCurrentPollingRate(int? rate)
    {
        currentPollingRate = Connected ? rate : null;
        selectedRate = currentPollingRate is int actual ? Rates.First(r => r.Hertz == actual) : null;
        Notify(nameof(SelectedRate));
        Notify(nameof(RateHint));
        Notify(nameof(CurrentPollingRate));
        Notify(nameof(PollingRateDetail));
    }

    public void ClearLog() { logs.Clear(); Notify(nameof(LogText)); }
    public void ReportError(string text) => Show(text, InfoBarSeverity.Error);
    private DpiStage[] Snapshot() => Stages.Select(s => s.Snapshot()).ToArray();
    private void Refresh()
    {
        RefreshAdvanced();
        foreach (var property in new[] { nameof(CanConnect), nameof(CanEdit), nameof(CanApplyDpi), nameof(CanApplyRate),
            nameof(ConnectionText), nameof(ConnectionDetail), nameof(ConnectLabel), nameof(EnabledCount), nameof(DpiHint), nameof(RateHint),
            nameof(PollingRateDetail) }) Notify(property);
        NotifyBatteryState();
    }

    private void Show(string text, InfoBarSeverity level) { Message = text; Severity = level; }
    private void AddLog(string text)
    {
        if (closing) return;
        logs.Add(text, DateTimeOffset.Now);
        if (!isLogExpanded || Interlocked.Exchange(ref logRefreshPending, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref logRefreshPending, 0);
            if (!closing && isLogExpanded) Notify(nameof(LogText));
        })) Interlocked.Exchange(ref logRefreshPending, 0);
    }

    public async ValueTask DisposeAsync() { closing = true; await mouse.DisposeAsync(); }
}
