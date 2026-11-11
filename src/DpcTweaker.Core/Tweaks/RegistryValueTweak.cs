using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

/// <summary>One registry value set to a target (or removed), with journal + .reg backup.</summary>
public sealed class RegistryValueTweak : TweakBase
{
    private readonly Func<string?>? _notApplicable;

    public RegistryValueTweak(TweakInfo info, Journal journal, string keyPath, string name,
        RegistryValueKind kind, object? target, Func<string?>? notApplicable = null)
        : base(info, journal)
    {
        KeyPath = keyPath;
        Name = name;
        Kind = kind;
        Target = target;
        _notApplicable = notApplicable;
    }

    public string KeyPath { get; }
    public string Name { get; }
    public RegistryValueKind Kind { get; }

    /// <summary>The value to write, or null to remove the value.</summary>
    public object? Target { get; }

    public bool RemoveMode => Target is null;

    public string TargetDisplay => RemoveMode
        ? "(not set)"
        : new RegValue(true, Kind, Target).Display;

    public bool Matches(RegValue v)
    {
        if (RemoveMode) return !v.Exists;
        if (!v.Exists) return false;
        return Kind switch
        {
            RegistryValueKind.DWord => v.Kind == RegistryValueKind.DWord && v.AsDword() == new RegValue(true, Kind, Target).AsDword(),
            RegistryValueKind.QWord => v.Kind == RegistryValueKind.QWord && v.AsQword() == new RegValue(true, Kind, Target).AsQword(),
            _ => string.Equals(v.Serialize(), new RegValue(true, Kind, Target).Serialize(), StringComparison.Ordinal),
        };
    }

    protected override Probe Read()
    {
        var cur = RegistryOps.Read(KeyPath, Name);
        string? na = _notApplicable?.Invoke();
        return new Probe(Matches(cur), cur.Display, TargetDisplay, na);
    }

    public override void Apply(TweakContext ctx)
    {
        string? na = _notApplicable?.Invoke();
        if (na is not null) throw new InvalidOperationException(na);

        ctx.Backup.ExportKeyOnce(KeyPath);
        var cur = RegistryOps.Read(KeyPath, Name);
        ctx.Journal.RecordOriginal(Info.Id, "reg", KeyPath + "|" + Name, cur.Exists,
            cur.Exists ? cur.Kind.ToString() : null, cur.Exists ? cur.Serialize() : null);

        if (RemoveMode) RegistryOps.DeleteValue(KeyPath, Name);
        else RegistryOps.Write(KeyPath, Name, Kind, Target!);

        var back = RegistryOps.Read(KeyPath, Name);
        if (!Matches(back)) throw new InvalidOperationException($"Wrote {Name}, but it reads back as {back.Display}.");
        Log.Info($"Set {KeyPath}\\{Name} = {TargetDisplay} (was {cur.Display})");
    }
}
