using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DpcTweaker.Core;
using DpcTweaker.Core.Infra;
using DpcTweaker.UI;

namespace DpcTweaker.App;

public partial class App : Application
{
    /// <summary>Held for the life of the process: a second copy must not share journal.json.</summary>
    private Mutex? _singleInstance;

    /// <summary>Signalled by a second copy so the running one brings its window forward.</summary>
    private EventWaitHandle? _activateSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Boot tasks run "StutterGuard.exe --agent <verb>" as SYSTEM: do the job, no window, exit.
        // They are short-lived and must never be blocked by the GUI's single-instance lock.
        if (Agent.IsAgentCall(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = Agent.Run(e.Args);
            Shutdown(code);
            return;
        }

        if (!TakeSingleInstanceLock())
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled: " + ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Stutter Guard - error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var host = new UiHost(Dispatcher);
        var vm = new MainViewModel(host);
        var window = new MainWindow(vm);
        host.Owner = window;
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// One GUI instance per session. Two copies would each hold their own copy of journal.json in
    /// memory and the last one to save would silently drop the other's recorded before-states.
    /// </summary>
    private bool TakeSingleInstanceLock()
    {
        try
        {
            _singleInstance = new Mutex(initiallyOwned: true, @"Local\StutterGuard_SingleInstance_Mutex", out bool created);
            if (!created)
            {
                // Ask the running copy to come to the front, then leave.
                if (EventWaitHandle.TryOpenExisting(@"Local\StutterGuard_Activate_Event", out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
                MessageBox.Show(
                    "Stutter Guard is already running.\n\nIts window has been brought to the front. Only one copy may run at a time, " +
                    "because two copies would overwrite each other's journal and backup records.",
                    "Stutter Guard", MessageBoxButton.OK, MessageBoxImage.Information);
                _singleInstance.Dispose();
                _singleInstance = null;
                return false;
            }

            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StutterGuard_Activate_Event");
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try { _activateSignal.WaitOne(); }
                    catch { return; }
                    Dispatcher.BeginInvoke(new Action(BringToFront));
                }
            })
            { IsBackground = true, Name = "single-instance activate" };
            thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            // Never block startup on the lock itself.
            Log.Warn("Single-instance lock failed, continuing without it: " + ex.Message);
            return true;
        }
    }

    private void BringToFront()
    {
        if (MainWindow is not Window w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activateSignal?.Dispose(); } catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        base.OnExit(e);
    }
}

internal sealed class UiHost : IUiHost
{
    private readonly Dispatcher _dispatcher;

    public UiHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Window? Owner { get; set; }

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public bool Confirm(string title, string message) =>
        Owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            : MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Inform(string title, string message)
    {
        if (Owner is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public string? Prompt(string title, string message, string defaultText = "")
    {
        string? result = null;
        var dlg = new Window
        {
            Title = title,
            Width = 500,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
            Foreground = Application.Current.TryFindResource("TextFillColorPrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,
        };

        var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 13,
            Foreground = dlg.Foreground
        });

        var tb = new System.Windows.Controls.TextBox
        {
            Text = defaultText,
            AcceptsReturn = true,
            Height = 120,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };
        sp.Children.Add(tb);

        var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new System.Windows.Controls.Button { Content = "Submit", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { result = tb.Text; dlg.DialogResult = true; dlg.Close(); };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 28, IsCancel = true };
        cancel.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };

        btnPanel.Children.Add(ok);
        btnPanel.Children.Add(cancel);
        sp.Children.Add(btnPanel);

        dlg.Content = sp;
        tb.Focus();
        bool? dialogRes = dlg.ShowDialog();
        return dialogRes == true ? result : null;
    }

    public void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Inform("Couldn't open", path + Environment.NewLine + ex.Message);
        }
    }

    public void Restart()
    {
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { UseShellExecute = false, CreateNoWindow = true });
    }
}