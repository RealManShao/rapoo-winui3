using Rapoo.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace Rapoo.App.ViewModels;

public sealed class DpiStageViewModel(int index, DpiStage? stage = null) : ObservableObject
{
    private double dpi = stage?.Dpi ?? double.NaN;
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
    public double Dpi { get => dpi; set => Set(ref dpi, value); }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public DpiStage Snapshot()
    {
        if (!double.IsFinite(Dpi) || Dpi != Math.Truncate(Dpi) || Dpi < 50 || Dpi > 45000 || Dpi % 50 != 0)
            throw new ArgumentException($"档位 {index + 1} 的 DPI 必须为 50–45000 之间的 50 的倍数。");
        return new((int)Dpi, Enabled);
    }
}
