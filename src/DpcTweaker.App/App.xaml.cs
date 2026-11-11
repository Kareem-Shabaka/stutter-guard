using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DpcTweaker.Core;
using DpcTweaker.Core.Infra;
using DpcTweaker.UI;

namespace DpcTweaker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Boot tasks run "StutterGuard.exe --agent <verb>" as SYSTEM: do the job, no window, exit.
        if (Agent.IsAgentCall(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = Agent.Run(e.Args);
            Shutdown(code);
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
