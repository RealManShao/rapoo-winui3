using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Rapoo.App.ViewModels;
using Windows.Graphics;
using Windows.System;

namespace Rapoo.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private bool initialized;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Rapoo.ico"));
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Light;
        model = new MainViewModel(DispatcherQueue);
        Root.DataContext = model;
        AppWindow.Resize(new SizeInt32(1180, 980));
        Root.Loaded += async (_, _) =>
        {
            if (initialized) return;
            initialized = true; await model.ConnectAsync();
        };
        Closed += async (_, _) => await model.DisposeAsync();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await model.ConnectAsync();
    private async void ApplyDpi_Click(object sender, RoutedEventArgs e) => await model.ApplyDpiAsync();
    private async void ApplyRate_Click(object sender, RoutedEventArgs e) => await model.ApplyRateAsync();
    private async void ApplySensors_Click(object sender, RoutedEventArgs e) => await model.ApplySensorsAsync();
    private void ClearLog_Click(object sender, RoutedEventArgs e) => model.ClearLog();
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(model.SettingsDirectory);
            if (!await Launcher.LaunchFolderPathAsync(model.SettingsDirectory))
                model.ReportError($"无法打开目录：{model.SettingsDirectory}");
        }
        catch (Exception ex) { model.ReportError($"无法打开配置目录：{ex.Message}"); }
    }
}
