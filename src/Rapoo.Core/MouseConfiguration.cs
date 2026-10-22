namespace Rapoo.Core;

public sealed record DpiStage(int Dpi, bool Enabled, int? YDpi = null)
{
    public int EffectiveYDpi => YDpi ?? Dpi;
}

public sealed record MouseConfiguration(DpiStage[] Stages, int PollingRate);

public sealed record BatteryReading(int Percent, int CurrentDpi, DateTimeOffset ReceivedAt);

public sealed record PollingRateOption(int Hertz, byte? Code, bool Custom = false)
{
    public bool IsSupported => Code.HasValue;
    public string Label => $"{Hertz:N0} Hz" + (Custom ? " · 自定义代码" : IsSupported ? "" : " · 缺少协议代码");
}
