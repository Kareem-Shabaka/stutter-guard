using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DpcTweaker.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public RelayCommand(Action run, Func<bool>? can = null)
        : this(_ => run(), can is null ? null : _ => can())
    { }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _run(parameter);

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>What the view models need from the WPF side.</summary>
public interface IUiHost
{
    /// <summary>Run on the UI thread.</summary>
    void Post(Action action);

    bool Confirm(string title, string message);

    void Inform(string title, string message);

    /// <summary>Prompt the user for multi-line text (e.g. bug report description).</summary>
    string? Prompt(string title, string message, string defaultText = "");

    /// <summary>Open a file or folder with its default app.</summary>
    void Open(string path);

    void Restart();
}
