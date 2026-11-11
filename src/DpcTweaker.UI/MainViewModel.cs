using System.Collections.ObjectModel;
using DpcTweaker.Core;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;

namespace DpcTweaker.UI;

public sealed class MainViewModel : ObservableObject
{
    private PageViewModel? _selected;
    private bool _loading = true;
    private string _loadingText = "Scanning this PC...";
    private string _statusText = "";
    private bool _restartNeeded;
    /// <summary>Set when something other than a row state needs a restart (a failed adapter restart).</summary>
    private bool _restartForced;
    private bool _globalBusy;
    private int _refreshing;

    public MainViewModel(IUiHost ui)
    {
        Ui = ui;
        Home = new HomePageViewModel(this);
        Power = new ModulePageViewModel(this, "Power", "Power plan", "",
            "The tuned power plan: light C-states, EPP 0, minimum state 100%, and USB / PCIe / disk power saving off. On AMD dual-CCD and Intel hybrid CPUs you choose between Windows parking (Balanced) and managing cores yourself (Ultimate).");
        Gpu = new ModulePageViewModel(this, "GPU", "GPU", "",
            "Driver registry keys, each with its own checkbox. The app scans your installed driver file and greys out any key it never reads. A driver update removes these keys again.");
        Interrupts = new ModulePageViewModel(this, "Interrupts", "Interrupts", "\uE950",
            "Every busy device gets its own core, away from the cores games use: the GPU first, then the USB controller with your mouse, your audio path, the network adapter (its receive queues on the same cores), then the other USB controllers. Plus MSI mode for devices still on a shared IRQ line. Takes effect after a restart; then run the check.");
        Network = new ModulePageViewModel(this, "Network", "Network", "\uE839",
            "Power saving off on wired adapters: Energy-Efficient Ethernet, Green Ethernet and the vendor's own link power saving, plus Interrupt Moderation on desktops. Each change restarts the adapter once (2-5 seconds offline), no reboot.");
        BootTasks = new ModulePageViewModel(this, "Boot tasks", "Boot tasks", "",
            "Scheduled tasks in the \\Stutter Guard\\ folder of Task Scheduler: device power saving off at every boot, and the system timer held at 0.5 ms from every logon.");
        Services = new ModulePageViewModel(this, "Services", "Services", "\uE912",
            "Every installed service with what it does and what you lose without it. A preset lists only what it would change; nothing changes until you press Apply selected, and each row has its own Revert. Windows, security and game services are locked.");
        Tasks = new ModulePageViewModel(this, "Tasks", "Scheduled tasks", "\uE823",
            "Every scheduled task, read as administrator. The preset only disables (never deletes) telemetry, background updaters and optional maintenance. Tasks your apps need are left alone.");
        Recommendations = new RecommendationsPageViewModel(this);
        Safety = new SafetyPageViewModel(this);
        // Temporarily on this version: Gpu tab is omitted by default and only inserted if an AMD GPU is detected.
        Pages = new ObservableCollection<PageViewModel> { Home, Power, Interrupts, Network, BootTasks, Services, Tasks, Recommendations, Safety };
        _selected = Home;
        RestartCommand = new RelayCommand(() =>
        {
            if (Ui.Confirm("Restart now", "Restart Windows now? Save your work first.")) Ui.Restart();
        });
        OpenSupportCommand = new RelayCommand(() => Ui.Open("https://github.com/sponsors/Kareem-Shabaka"));
        OpenGitHubCommand = new RelayCommand(() => Ui.Open("https://github.com/Kareem-Shabaka/stutter-guard"));
    }

    public IUiHost Ui { get; }
    public TweakEngine? Engine { get; private set; }

    public HomePageViewModel Home { get; }
    public ModulePageViewModel Power { get; }
    public ModulePageViewModel Interrupts { get; }
    public ModulePageViewModel Network { get; }
    public ModulePageViewModel Gpu { get; }
    public ModulePageViewModel Services { get; }
    public ModulePageViewModel Tasks { get; }
    public RecommendationsPageViewModel Recommendations { get; }

    /// <summary>The last interrupt check, kept across page rebuilds.</summary>
    public CheckResult? LastCheck { get; set; }
    public ModulePageViewModel BootTasks { get; }
    public SafetyPageViewModel Safety { get; }
    public ObservableCollection<PageViewModel> Pages { get; }

    public RelayCommand RestartCommand { get; }
    public RelayCommand OpenSupportCommand { get; }
    public RelayCommand OpenGitHubCommand { get; }

    public bool RestorePointDone { get; set; }

    public PageViewModel? SelectedPage
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && value is not null) _ = value.RefreshAsync();
        }
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public string LoadingText
    {
        get => _loadingText;
        private set => Set(ref _loadingText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool RestartNeeded
    {
        get => _restartNeeded;
        set => Set(ref _restartNeeded, value);
    }

    public bool GlobalBusy
    {
        get => _globalBusy;
        private set => Set(ref _globalBusy, value);
    }

    public string Version => "0.7.0, built " + BuildTime;

    /// <summary>When this .exe was published: makes an old copy obvious.</summary>
    public static string BuildTime
    {
        get
        {
            try { return Environment.ProcessPath is { } p ? File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm") : "?"; }
            catch { return "?"; }
        }
    }

    public void SetGlobalBusy(bool busy) => GlobalBusy = busy;

    /// <summary>A restart is warranted for a reason a row status can't express (a failed adapter restart).</summary>
    public void ForceRestartNeeded() { _restartForced = true; RestartNeeded = true; }

    /// <summary>Called when a new apply/revert cycle starts, so an old reason doesn't outlive it.</summary>
    public void ClearForcedRestart() => _restartForced = false;
    public async Task InitializeAsync()
    {
        Loading = true;
        LoadingText = "Scanning this PC...";
        try
        {
            var (profile, journal) = await Task.Run(() =>
            {
                var p = Detector.Detect(s => Ui.Post(() => LoadingText = s));
                return (p, Journal.OpenDefault());
            });
            Engine = new TweakEngine(profile, journal);
            Home.Load(profile);
            ReloadPages();
            Loading = false;
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            LoadingText = "The scan failed: " + ex.Message;
            Log.Error("Scan failed: " + ex);
        }
    }

    /// <summary>Rebuild every module page from the engine's catalog (after options change).</summary>
    public void ReloadPages()
    {
        var e = Engine;
        if (e is null) return;
        Home.LoadBench();
        Power.OptionsPanel = new PowerOptionsViewModel(this, e.Options, e.Profile);
        BootTasks.OptionsPanel = new TimerOptionsViewModel(this, e.Options);
        Interrupts.OptionsPanel = new InterruptOptionsViewModel(this, e);
        Network.OptionsPanel = new NetworkSummaryViewModel(e.Profile);
        Power.Load(e);
        Interrupts.Load(e);
        Network.Load(e);
        Services.Load(e);
        Tasks.Load(e);
        Services.OptionsPanel = new CleanupPanelViewModel(this, e, tasks: false);
        Tasks.OptionsPanel = new CleanupPanelViewModel(this, e, tasks: true);
        Recommendations.Load(e);
        BootTasks.Load(e);

        // Temporarily on this version: only show the GPU tab if an AMD GPU is detected (until NVIDIA tweaks land)
        bool hasAmdGpu = e.Profile.Gpus.Any(g => g.Vendor == GpuVendor.Amd);
        if (hasAmdGpu)
        {
            if (!Pages.Contains(Gpu))
            {
                int netIdx = Pages.IndexOf(Network);
                if (netIdx >= 0) Pages.Insert(netIdx + 1, Gpu);
                else Pages.Add(Gpu);
            }
            Gpu.OptionsPanel = new GpuSummaryViewModel(e.Profile);
            Gpu.Load(e);
        }
        else
        {
            if (Pages.Contains(Gpu))
            {
                if (_selected == Gpu) SelectedPage = Home;
                Pages.Remove(Gpu);
            }
        }
        if (Power.OptionsPanel is PowerOptionsViewModel pov)
        {
            var target = new Core.Tweaks.PowerPlanTarget(e.Journal, () => e.Options);
            pov.PlanDescription = "Target plan: " + target.Describe();
        }
        OnPropertyChanged(nameof(Pages));
        _ = RefreshAllAsync();
    }

    public async Task RefreshAllAsync()
    {
        foreach (var p in Pages.OfType<ModulePageViewModel>()) await p.RefreshAsync();
        await Safety.RefreshAsync();
    }

    /// <summary>Called by the 5-second timer: refresh only the visible page, never overlapping.</summary>
    public async Task RefreshVisibleAsync()
    {
        if (Engine is null || GlobalBusy) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            if (SelectedPage is not null) await SelectedPage.RefreshAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public void OnRowsUpdated()
    {
        var rows = Pages.OfType<ModulePageViewModel>().SelectMany(p => p.Rows).ToList();
        Home.UpdateCounts(rows);
        int applied = rows.Count(r => r.Status == TweakStatus.Applied);
        int pending = rows.Count(r => r.Status == TweakStatus.PendingReboot);
        int drifted = rows.Count(r => r.Status == TweakStatus.Drifted);
        // Derived, not sticky: reverting the last pending change clears the banner again.
        RestartNeeded = pending > 0 || _restartForced;
        StatusText = $"{applied} applied · {pending} pending restart · {drifted} drifted";
    }

    /// <summary>A restore point before the first apply of each session.</summary>
    public async Task<bool> EnsureRestorePointAsync()
    {
        if (RestorePointDone) return true;
        SetGlobalBusy(true);
        Safety.Message = "Creating a restore point before the first change...";
        var point = await Task.Run(() => RestorePoint.Create("Stutter Guard", Safety.TurnOnProtection, Engine?.Journal));
        bool ok = point.Ok;
        string msg = point.Message;
        if (point.TurnedProtectionOn) Safety.Message = "System Protection was off; it was turned on and recorded so Revert everything can put it back.";
        SetGlobalBusy(false);
        if (ok)
        {
            RestorePointDone = true;
            Safety.Message = "Restore point created.";
            return true;
        }
        bool go = Ui.Confirm("No restore point",
            "Windows couldn't create a restore point:\n\n" + msg +
            "\n\nThe app still keeps its own backups and journal for every change. Continue without a restore point?");
        if (go) RestorePointDone = true;
        return go;
    }
}
