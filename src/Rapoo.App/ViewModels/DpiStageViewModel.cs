using Rapoo.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace Rapoo.App.ViewModels;

public sealed class DpiStageViewModel(int index, DpiStage? stage = null) : ObservableObject
{
    private double dpi = stage?.Dpi ?? double.NaN;
    private double dpiY = stage?.EffectiveYDpi ?? double.NaN;
    private bool independent;
    private bool enabled = stage?.Enabled ?? false;
    private bool isCurrentDpiMatch;
    private static readonly Brush CurrentBackground = (Brush)Application.Current.Resources["BrandSubtleBrush"];
    private static readonly SolidColorBrush EmptyBackground = new(Microsoft.UI.Colors.Transparent);
    public bool IsCurrentDpiMatch
    {
        get => isCurrentDpiMatch;
        set
        {
            if (!Set(ref isCurrentDpiMatch, value)) return;
            Notify(nameof(RowBackground));
            Notify(nameof(CurrentIndicator));
        }
    }
    public Brush RowBackground => IsCurrentDpiMatch ? CurrentBackground : EmptyBackground;
    public string CurrentIndicator => IsCurrentDpiMatch ? "当前 DPI" : "";
    public string Number => $"{index + 1:00}";
    public string AccessibleLabel => $"档位 {index + 1} DPI";
    public string ToggleLabel => $"启用档位 {index + 1}";
    public string YAccessibleLabel => $"档位 {index + 1} Y DPI";
    public bool Independent { get => independent; set { if (Set(ref independent, value) && !value) DpiY = Dpi; } }
    public double Dpi { get => dpi; set { if (Set(ref dpi, value) && !Independent) DpiY = value; } }
    public double DpiY { get => dpiY; set => Set(ref dpiY, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public DpiStage Snapshot()
    {
        if (!double.IsFinite(Dpi) || Dpi != Math.Truncate(Dpi) || Dpi < 50 || Dpi > 45000 || Dpi % 50 != 0)
            throw new ArgumentException($"档位 {index + 1} 的 DPI 必须为 50–45000 之间的 50 的倍数。");
        if (!double.IsFinite(DpiY) || DpiY != Math.Truncate(DpiY) || DpiY < 50 || DpiY > 45000 || DpiY % 50 != 0)
            throw new ArgumentException($"档位 {index + 1} 的 Y DPI 必须为 50–45000 之间的 50 的倍数。");
        return new((int)Dpi, Enabled, (int)DpiY);
    }
}
