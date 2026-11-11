using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DpcTweaker.UI;

namespace DpcTweaker.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _closeWatch;

    /// <summary>build.cmd drops this file next to the .exe to ask a running copy to close, so the build can replace it.</summary>
    public static string CloseRequestFile =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory, "close-request");

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        Loaded += async (_, _) => await vm.InitializeAsync();

        // Live status: re-read the visible page every 5 seconds.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await vm.RefreshVisibleAsync();
        _timer.Start();

        // A rebuild is waiting: close, unless something is being applied or measured right now.
        try { File.Delete(CloseRequestFile); } catch { }
        _closeWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeWatch.Tick += (_, _) =>
        {
            if (vm.GlobalBusy || !File.Exists(CloseRequestFile)) return;
            try { File.Delete(CloseRequestFile); } catch { }
            Close();
        };
        _closeWatch.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _closeWatch.Stop();
            Application.Current.Shutdown();
        };
    }
}
