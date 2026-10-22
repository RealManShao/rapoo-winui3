using Rapoo.Core;

namespace Rapoo.App.ViewModels;

public sealed class SensorEditorViewModel : ObservableObject
{
    private bool motionSync, linearCorrection, rippleCorrection, glassTracking;
    private LodOption? lod;
    private int? pressMs, releaseMs;
    private double sleepMinutes = double.NaN, angle = double.NaN;
    public bool MotionSync { get => motionSync; set => Set(ref motionSync, value); }
    public bool LinearCorrection { get => linearCorrection; set => Set(ref linearCorrection, value); }
    public bool RippleCorrection { get => rippleCorrection; set => Set(ref rippleCorrection, value); }
    public bool GlassTracking { get => glassTracking; set => Set(ref glassTracking, value); }
    public LodOption? Lod { get => lod; set => Set(ref lod, value); }
    public int? PressMs { get => pressMs; set => Set(ref pressMs, value); }
    public int? ReleaseMs { get => releaseMs; set => Set(ref releaseMs, value); }
    public double SleepMinutes { get => sleepMinutes; set => Set(ref sleepMinutes, value); }
    public double Angle { get => angle; set => Set(ref angle, value); }

    public SensorSettings Snapshot()
    {
        if (!double.IsFinite(SleepMinutes) || SleepMinutes != Math.Truncate(SleepMinutes) ||
            !double.IsFinite(Angle) || Angle != Math.Truncate(Angle) || Lod is null || PressMs is null || ReleaseMs is null)
            throw new ArgumentException("请填写完整参数，休眠分钟和角度必须为整数。");
        var value = new SensorSettings(MotionSync, LinearCorrection, RippleCorrection, GlassTracking,
            Lod.Code, PressMs.Value, ReleaseMs.Value, checked((int)SleepMinutes), checked((int)Angle));
        RapooProtocol.ValidateSensorSettings(value);
        return value;
    }

    public static SensorEditorViewModel From(SensorSettings value) => new()
    {
        MotionSync = value.MotionSync, LinearCorrection = value.LinearCorrection,
        RippleCorrection = value.RippleCorrection, GlassTracking = value.GlassTracking,
        Lod = RapooProtocol.LodOptions.First(o => o.Code == value.LodCode), PressMs = value.PressDebounceMs,
        ReleaseMs = value.ReleaseDebounceMs, SleepMinutes = value.SleepMinutes, Angle = value.Angle
    };
}
