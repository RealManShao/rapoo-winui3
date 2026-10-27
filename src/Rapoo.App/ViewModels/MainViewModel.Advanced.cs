using Microsoft.UI.Xaml.Controls;
using Rapoo.Core;

namespace Rapoo.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool independentDpi, sensorLoaded;
    private SensorEditorViewModel sensor = new();
    private string sensorStatus = "尚未读取";
    public IReadOnlyList<int> DebounceOptions => RapooProtocol.DebounceOptions;
    public IReadOnlyList<LodOption> LodOptions => RapooProtocol.LodOptions;
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
    public string SensorStatus { get => sensorStatus; private set => Set(ref sensorStatus, value); }

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

    private async Task LoadAdvancedAsync()
    {
        var parameters = await mouse.ReadSensorConfigurationAsync();
        if (closing || !Connected) return;
        LoadSensors(parameters);
    }

    private void ClearAdvanced()
    {
        LoadSensors(null);
        SensorStatus = "尚未读取";
    }

    private void RefreshAdvanced()
    {
        foreach (var name in new[] { nameof(CanEditSensors), nameof(CanApplySensors), nameof(SensorHint) }) Notify(name);
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

}
