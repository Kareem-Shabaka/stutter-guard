using System.Collections.ObjectModel;
using DpcTweaker.Core;
using DpcTweaker.Core.Catalog;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Reports;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.UI;

/// <summary>Top panel of the Services and Tasks screens: the preset, what it changes, and a searchable list of everything.</summary>
public sealed class CleanupPanelViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly List<(string Search, InfoLine Line)> _all = new();
    private Choice<CleanupPreset> _preset;
    private string _filter = "";

    public CleanupPanelViewModel(MainViewModel main, TweakEngine engine, bool tasks)
    {
        _main = main;
        IsTasks = tasks;
        var p = engine.Profile;
        var o = engine.Options;

        Presets = Enum.GetValues<CleanupPreset>()
            .Where(x => !tasks || x != CleanupPreset.WindowsDefault)
            .Select(x => new Choice<CleanupPreset>(x, Presets_Label(x, p), Core.Catalog.Presets.Description(x)))
            .ToList();
        _preset = Presets.FirstOrDefault(x => x.Value == o.Cleanup) ?? Presets.First(x => x.Value == CleanupPreset.Safe);
        UsePresetCommand = new RelayCommand(UsePreset);

        int changes = engine.InModule(tasks ? TasksModule.ModuleName : ServicesModule.ModuleName).Count();
        if (tasks)
        {
            var idle = p.Tasks.Where(t => t.Enabled && (t.IdleTriggered || Catalogs.Tasks.Find(t.Path)?.Idle == true)).ToList();
            Summary = $"{p.Tasks.Count} scheduled tasks, read as administrator. The \"{Core.Catalog.Presets.Label(o.Cleanup)}\" preset lists {changes} below." +
                      (o.Cleanup == CleanupPreset.WindowsDefault ? " (Windows default only applies to services; for tasks, use Revert.)" : "");
            IdleNote = $"{idle.Count} enabled tasks run when the PC is idle or during automatic maintenance. Windows starts them after about 10 minutes without input: " +
                       "on the author's desktop a burst of them (taskhostw, SDXHelper, WmiPrvSE, UsoClient, sppsvc) began 8.5 minutes into a hands-off trace. " +
                       "That's why a long idle LatencyMon run can catch spikes a normal session never sees.";
            foreach (var t in p.Tasks)
            {
                var e = Catalogs.Tasks.Find(t.Path);
                bool idleTask = t.IdleTriggered || e?.Idle == true;
                string verdict = e is null
                    ? (t.IsMicrosoft ? "Not in the catalog: no guidance, leave it." : "Vendor task, not in the catalog: disabling it stops whatever it does for its app.")
                    : e.IsLocked ? "Locked: " + (e.LockedReason ?? "Windows needs it.")
                    : e.Group == "app" ? "Needed by an app you run: left alone. " + e.What
                    : $"{CleanupGroups.Heading(e.Group)}: {e.What} If disabled: {e.IfOff}";
                string value = $"{t.Folder} · {(t.Enabled ? t.State : "disabled")} · {t.TriggersText}{(idleTask ? " · runs at idle" : "")} · runs {t.ActionsText}" +
                               (t.LastRun is DateTime lr ? $" · last run {lr:g}" : "") + $"\n{verdict}";
                _all.Add(((t.Path + " " + verdict + " " + t.ActionsText).ToLowerInvariant(), new InfoLine(t.Name, value)));
            }
        }
        else
        {
            Summary = $"{p.Services.Count(s => !s.IsPerUserInstance)} services (plus {p.Services.Count(s => s.IsPerUserInstance)} per-user instances). " +
                      $"The \"{Core.Catalog.Presets.Label(o.Cleanup)}\" preset lists {changes} below. Changes take effect at the next restart; running services keep running until then.";
            IdleNote = "Hardware the presets check: " + p.Hardware.Describe() + ". Services for hardware this PC has are never offered.";
            foreach (var s in p.Services)
            {
                var e = Catalogs.Services.Find(s.Name) ?? Catalogs.Services.Find(Catalogs.TemplateName(s.Name));
                string verdict = e is null
                    ? (s.IsWindowsBinary ? "Not in the catalog: no guidance, leave it."
                        : $"Vendor service{(string.IsNullOrEmpty(s.Company) ? "" : " (" + s.Company + ")")}, not in the catalog: Manual is usually safe if you don't use its app; Disabled can break the app.")
                    : e.IsLocked ? "Locked: " + (e.LockedReason ?? "Windows needs it.")
                    : e.Group == "peruser" ? "Per-user service, listed only. " + e.What
                    : $"{CleanupGroups.Heading(e.Group)}: {e.What} If off: {e.IfOff}" + (e.Ms is { Length: > 0 } ms ? $" (Microsoft server guidance: {ms})" : "");
                string value = $"{s.Name} · {StartTypes.Label(s.Start)}{(s.TriggerStart ? ", trigger start" : "")} · {s.State}\n{verdict}";
                _all.Add(((s.Name + " " + s.DisplayName + " " + verdict).ToLowerInvariant(), new InfoLine(s.DisplayName, value)));
            }
        }
        ApplyFilter();
    }

    private static string Presets_Label(CleanupPreset x, SystemProfile p)
    {
        string label = Core.Catalog.Presets.Label(x);
        bool recommended = p.IsLaptop ? x == CleanupPreset.Laptop : x == CleanupPreset.Safe;
        return recommended ? label + " (recommended)" : label;
    }

    public bool IsTasks { get; }
    public string Summary { get; }
    public string IdleNote { get; }
    public List<Choice<CleanupPreset>> Presets { get; }

    public Choice<CleanupPreset> PresetChoice
    {
        get => _preset;
        set => Set(ref _preset, value);
    }

    public RelayCommand UsePresetCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) ApplyFilter();
        }
    }

    public ObservableCollection<InfoLine> Lines { get; } = new();

    private string _countText = "";
    public string CountText
    {
        get => _countText;
        private set => Set(ref _countText, value);
    }

    private void ApplyFilter()
    {
        string f = (_filter ?? "").Trim().ToLowerInvariant();
        var hits = _all.Where(x => f.Length == 0 || x.Search.Contains(f)).Select(x => x.Line).ToList();
        Lines.Clear();
        foreach (var l in hits.Take(500)) Lines.Add(l);
        CountText = f.Length == 0 ? $"All {_all.Count}" : $"{hits.Count} of {_all.Count} match";
    }

    private void UsePreset()
    {
        var engine = _main.Engine;
        if (engine is null || PresetChoice.Value == engine.Options.Cleanup) return;
        engine.SetOptions(engine.Options.With(o => o.Cleanup = PresetChoice.Value));
        _main.ReloadPages();
    }
}

public sealed class LinkButton
{
    public LinkButton(string label, RelayCommand command)
    {
        Label = label;
        Command = command;
    }

    public string Label { get; }
    public RelayCommand Command { get; }
}

public sealed class RecommendationCardViewModel
{
    public required string Title { get; init; }
    public required string When { get; init; }
    public required List<string> Lines { get; init; }
    public required List<LinkButton> Links { get; init; }
    public LinkButton? Action { get; init; }
    public bool HasAction => Action is not null;
    public bool HasLinks => Links.Count > 0;
}

/// <summary>Things the app recommends rather than does: WinUtil, Process Lasso (with generated rules), Slimmer / NVCleanstall, LatencyMon, BIOS.</summary>
public sealed class RecommendationsPageViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    public RecommendationsPageViewModel(MainViewModel main) : base("Recommendations", "")
    {
        _main = main;
    }

    public ObservableCollection<RecommendationCardViewModel> Cards { get; } = new();

    public void Load(TweakEngine engine)
    {
        Cards.Clear();
        foreach (var r in Recommendations.For(engine.Profile, engine.Options, engine.Plan))
        {
            Cards.Add(new RecommendationCardViewModel
            {
                Title = r.Title,
                When = r.When,
                Lines = r.Lines.ToList(),
                Links = r.Links.Select(l => new LinkButton(l.Label, new RelayCommand(() => _main.Ui.Open(l.Target)))).ToList(),
                Action = r.Action == "lasso" ? new LinkButton("Save starter rules for Process Lasso", new RelayCommand(() => SaveLasso(engine))) : null,
            });
        }
    }

    private void SaveLasso(TweakEngine engine)
    {
        try
        {
            string file = LassoRules.Write(engine.Profile, engine.Plan);
            Message = "Saved " + file + ". Import it into its own profile in Process Lasso, then add your own games.";
            _main.Ui.Open(Path.GetDirectoryName(file)!);
        }
        catch (Exception ex)
        {
            Message = "Couldn't save the rules: " + ex.Message;
        }
    }
}
