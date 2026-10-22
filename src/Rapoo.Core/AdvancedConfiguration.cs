namespace Rapoo.Core;

public sealed record SensorSettings(bool MotionSync, bool LinearCorrection, bool RippleCorrection,
    bool GlassTracking, int LodCode, int PressDebounceMs, int ReleaseDebounceMs, int SleepMinutes, int Angle);

/// <summary>Owns the original blocks so reserved fields survive edits.</summary>
public sealed class SensorConfiguration(SensorSettings settings, byte[] block84, byte[] blockC0, byte[] blockC4)
{
    public SensorSettings Settings { get; } = settings;
    private readonly byte[] original84 = (byte[])block84.Clone();
    private readonly byte[] originalC0 = (byte[])blockC0.Clone();
    private readonly byte[] originalC4 = (byte[])blockC4.Clone();
    public byte[] Block84 => (byte[])original84.Clone();
    public byte[] BlockC0 => (byte[])originalC0.Clone();
    public byte[] BlockC4 => (byte[])originalC4.Clone();
}

public enum DiyAction { Disabled, MiddleClick, KeyboardA, CyclePollingRate }
public sealed record DiyActionOption(DiyAction Action, string Label);
public sealed record LodOption(int Code, string Label);
