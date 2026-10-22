using Microsoft.UI.Xaml.Controls;
using Rapoo.Core;

namespace Rapoo.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool independentDpi, sensorLoaded, diyLoaded;
    private SensorEditorViewModel sensor = new();
    private DiyActionOption? selectedDiyAction;
    private string sensorStatus = "尚未读取", diyStatus = "尚未读取", currentDiyAction = "—";
    public IReadOnlyList<int> DebounceOptions => RapooProtocol.DebounceOptions;
    public IReadOnlyList<LodOption> LodOptions => RapooProtocol.LodOptions;
    public IReadOnlyList<DiyActionOption> DiyActions => RapooProtocol.DiyActions;
    public SensorEditorViewModel Sensor { get => sensor; private set => Set(ref sensor, value); }
    public bool IndependentDpi
    {
        get => independentDpi;
        set
        {
            if (!Set(ref independentDpi, value)) return;
            foreach (var stage in Stages) stage.Independent = value;
            if (!loadingDeviceConfiguration) DpiStatus = "有待应用的更改";
            Refresh();
        }
    }
    public bool CanEditSensors => Connected && !Busy && sensorLoaded;
    public string SensorHint
    {
        get
        {
            if (!sensorLoaded) return "连接并读取参数后可编辑。";
            try { Sensor.Snapshot(); return "修改后点击应用参数，等待设备读回确认。"; }
            catch (Exception ex) when (ex is ArgumentException or OverflowException) { return ex.Message; }
        }
    }
    public bool CanApplySensors
    {
        get { if (!CanEditSensors) return false; try { Sensor.Snapshot(); return true; } catch (Exception ex) when (ex is ArgumentException or OverflowException) { return false; } }
    }
    public bool CanEditDiy => Connected && !Busy && diyLoaded;
    public bool CanApplyDiy => CanEditDiy && SelectedDiyAction is not null;
    public DiyActionOption? SelectedDiyAction
    {
        get => selectedDiyAction;
        set { if (Set(ref selectedDiyAction, value)) { DiyStatus = "有待应用的更改"; RefreshAdvanced(); } }
    }
    public string SensorStatus { get => sensorStatus; private set => Set(ref sensorStatus, value); }
    public string DiyStatus { get => diyStatus; private set => Set(ref diyStatus, value); }
    public string CurrentDiyAction { get => currentDiyAction; private set => Set(ref currentDiyAction, value); }

    private void InitializeAdvanced() => TrackSensorChanges();
    private void TrackSensorChanges() => Sensor.PropertyChanged += (_, _) =>
    {
        SensorStatus = "有待应用的更改";
        RefreshAdvanced();
    };

    private void LoadSensors(SensorConfiguration? configuration)
    {
        sensorLoaded = configuration is not null;
        Sensor = configuration is null ? new() : SensorEditorViewModel.From(configuration.Settings);
        TrackSensorChanges();
        SensorStatus = configuration is null ? "参数读取失败，请重新连接" : "已读取设备参数";
        RefreshAdvanced();
    }

    private void LoadDiy(byte[]? bytes)
    {
        diyLoaded = bytes is not null;
        var action = bytes is null ? null : RapooProtocol.DecodeDiyAction(bytes);
        selectedDiyAction = action is null ? null : DiyActions.First(o => o.Action == action);
        Notify(nameof(SelectedDiyAction));
        CurrentDiyAction = bytes is null ? "—" : selectedDiyAction?.Label ?? "其他动作（保留）";
        DiyStatus = bytes is null ? "按键读取失败，请重新连接" : "已读取设备按键配置";
        RefreshAdvanced();
    }

    private async Task LoadAdvancedAsync()
    {
        var parameters = await mouse.ReadSensorConfigurationAsync();
        if (closing || !Connected) return;
        LoadSensors(parameters);
        var action = await mouse.ReadDiyActionAsync();
        if (!closing && Connected) LoadDiy(action);
    }

    private void ClearAdvanced()
    {
        LoadSensors(null);
        LoadDiy(null);
        SensorStatus = DiyStatus = "尚未读取";
    }

    private void RefreshAdvanced()
    {
        foreach (var name in new[] { nameof(CanEditSensors), nameof(CanApplySensors), nameof(CanEditDiy), nameof(CanApplyDiy), nameof(SensorHint) }) Notify(name);
    }

    public async Task ApplySensorsAsync()
    {
        if (!CanApplySensors || closing) return;
        var requested = Sensor.Snapshot();
        Busy = true;
        try
        {
            var actual = await mouse.SetSensorSettingsAsync(requested);
            if (closing || !Connected) return;
            LoadSensors(actual);
            var confirmed = actual.Settings == requested;
            SensorStatus = confirmed ? $"{DateTime.Now:HH:mm:ss} 参数已读回确认" : "设备读回与请求不同，已显示实际参数";
            Show(SensorStatus, confirmed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            LoadSensors(null);
            SensorStatus = "操作未确认，请重新连接读取实际参数";
            Show(ex.Message, InfoBarSeverity.Error);
        }
        finally { Busy = false; }
    }

    public async Task ApplyDiyAsync()
    {
        if (!CanApplyDiy || closing) return;
        var requested = SelectedDiyAction!.Action;
        Busy = true;
        try
        {
            var actual = await mouse.SetDiyActionAsync(requested);
            if (closing || !Connected) return;
            LoadDiy(actual);
            var confirmed = actual.AsSpan().SequenceEqual(RapooProtocol.DiyActionBytes(requested));
            DiyStatus = confirmed ? $"{DateTime.Now:HH:mm:ss} DIY 按键已读回确认" : "设备读回与请求不同，已显示实际动作";
            Show(DiyStatus, confirmed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            LoadDiy(null);
            DiyStatus = "操作未确认，请重新连接读取实际动作";
            Show(ex.Message, InfoBarSeverity.Error);
        }
        finally { Busy = false; }
    }
}
