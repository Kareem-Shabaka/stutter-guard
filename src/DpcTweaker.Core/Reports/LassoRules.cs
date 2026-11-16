using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core.Reports;

/// <summary>
/// Starter Process Lasso rules for core separation, in Bitsum's JSON rules format
/// (processlasso.schema.json 2026-01-17, the format the author's 48-rule "Game Mode" file uses):
/// games and dwm.exe on the game cores at High priority. Dynamic thread priority boost is left ON:
/// Windows' wake boosts (+6 mouse/keyboard, +8 sound) are what let a woken input or audio thread
/// preempt, so switching them off works against the very latency being targeted.
/// and a catch-all *.* on the other cores as the last rule.
/// </summary>
public static class LassoRules
{
    public const string SchemaId = "https://bitsum.com/json-schemas/2026-01-17-00/processlasso.schema.json";

    /// <summary>
    /// Fewest CPUs a catch-all may name. Below this, refusing to emit it is the better answer: a
    /// catch-all of one to three CPUs would squeeze every background process on the machine into them,
    /// and it happens precisely when the device plan has claimed most of the machine, so the fix there
    /// is to trim device pins rather than to emit a crippled mask.
    /// </summary>
    public const int MinBackgroundCpus = 4;

    /// <summary>The author's 46 games plus common competitive titles. The user edits the list in Lasso afterwards.</summary>
    public static readonly string[] Games =
    {
        "cs2.exe", "valorant-win64-shipping.exe", "fortniteclient-win64-shipping.exe", "r5apex.exe", "r5apex_dx12.exe",
        "overwatch.exe", "rainbowsix.exe", "rainbowsix_vulkan.exe", "dota2.exe", "league of legends.exe", "rocketleague.exe",
        "eldenring.exe", "gta5.exe", "gta5_enhanced.exe", "rdr2.exe", "bf2042.exe", "marvel-win64-shipping.exe",
        "balls.exe", "blackops3.exe", "blur.exe", "borderlands3.exe", "chainedtogether-win64-shipping.exe", "cod.exe", "crysis3.exe",
        "cyberpunk2077.exe", "deadcells.exe", "devotion.exe", "disco elysium.exe", "firewatch.exe", "gow.exe", "gowr.exe", "hades.exe",
        "hades2.exe", "hitman3.exe", "hollow knight silksong.exe", "hollow_knight.exe", "ittakestwo.exe", "lanoire.exe", "left4dead2.exe",
        "lop-win64-shipping.exe", "megabonk.exe", "mk12.exe", "monstertrain.exe", "mortalshell2-win64-shipping.exe",
        "neighbours back from hell.exe", "nfs11.exe", "olgame.exe", "p5r.exe", "re9.exe", "reanimal.exe", "romancing saga 2 rots-win64.exe",
        "shproto-win64-shipping.exe", "sifu-win64-shipping.exe", "skyrimse.exe", "soma_nosteam.exe", "spiritfarer.exe",
        "stillwakesthedeep.exe", "thequarry-win64-shipping.exe", "ultrakill.exe", "vampiresurvivors.exe", "wicked seed.exe",
        "witcher3.exe", "yakuzalikeadragon.exe",
    };

    public sealed record Split(IReadOnlyList<int> GameCpus, IReadOnlyList<int> OtherCpus, string GameLabel, string Note);

    /// <summary>
    /// Which CPUs games get and which everything else gets. Null when the CPU has nothing to separate.
    /// <para>
    /// On a CPU with two or more L3 groups the choice is explicit: <see cref="AppOptions.GameCcd"/> wins
    /// when the user set it, and otherwise the measured L3 sizes decide. The label describes the group by
    /// its cache size, never as "the V-Cache CCD", because that phrase is false on a symmetric part such
    /// as the 9950X3D2 where both CCDs carry V-Cache and the app previously fell through to a clock
    /// ranking that carried no cache information at all.
    /// </para>
    /// </summary>
    public static Split? SplitFor(SystemProfile p, AffinityPlan plan, GameCcdChoice gameCcd = GameCcdChoice.Auto)
    {
        var topo = p.Cpu;
        var all = topo.Cpus.Where(c => c.Index < 64).Select(c => c.Index).ToList();
        List<int> game;
        string label;
        if (topo.IsHybrid)
        {
            int maxEff = topo.Cpus.Max(c => c.EfficiencyClass);
            game = topo.Cpus.Where(c => c.EfficiencyClass == maxEff).Select(c => c.Index).ToList();
            label = "P-cores";
        }
        else if (topo.L3Groups.Count >= 2)
        {
            var chosen = ResolveGameGroup(p, topo, gameCcd);
            game = chosen.Group.Cpus().ToList();
            label = CcdLabel(chosen.Group) + chosen.Why;
        }
        else return null;

        // Background gets every logical processor that is NOT pinned to a device. Game cores stay in the
        // mask on purpose: games hold them anyway via their own rule plus High priority, and background
        // may legitimately use the V-Cache CCD while no game is running.
        //
        // Subtracting the devices' CPUs is the whole point. This used to leave out only the GPU's core -
        // reasoned as "like the author's catch-all 18-31" - which on a 9950X3D emitted 18-31, exactly the
        // cores the interrupt plan had reserved for seven other devices. Measured cost of that placement:
        // median wake latency 782 us against 8.9 us idle, about 90x.
        var deviceCpus = plan.DeviceCpus.ToHashSet();
        var other = all.Where(c => !deviceCpus.Contains(c)).ToList();
        string note = deviceCpus.Count == 0
            ? ""
            : $" Devices hold CPUs {CpuTopology.FormatCpus(deviceCpus.OrderBy(x => x))}; they are left out of the catch-all so background apps never share an interrupt core.";
        return new Split(game, other, label, note);
    }

    /// <summary>A cache group named by what it measurably is: "CCD 0 (96 MB L3)".</summary>
    public static string CcdLabel(CacheGroup g) => $"CCD {g.Id} ({g.SizeBytes / (1024 * 1024)} MB L3)";

    /// <summary>Which group games should use, and one clause saying why.</summary>
    public readonly record struct GameGroup(CacheGroup Group, string Why);

    /// <summary>
    /// Resolves the game group from the user's choice, else the measured cache sizes, else Windows'
    /// core ranking. Each branch returns the reason it fired, because "games go on CCD 1" without a
    /// reason is indistinguishable from a bug to the person reading it.
    /// </summary>
    public static GameGroup ResolveGameGroup(SystemProfile p, CpuTopology topo, GameCcdChoice choice)
    {
        // An explicit choice wins. It is legitimate to move games off the larger cache: on every
        // asymmetric X3D part the cache CCD is also the slower CCD, so a frequency-bound game can be
        // faster on the other one. The label says what was given up rather than pretending it is free.
        if (choice is GameCcdChoice.Ccd0 or GameCcdChoice.Ccd1)
        {
            int want = choice == GameCcdChoice.Ccd0 ? 0 : 1;
            var pick = topo.L3Groups.FirstOrDefault(g => g.Id == want) ?? topo.L3Groups[0];
            var biggest = topo.L3Groups.MaxBy(g => g.SizeBytes)!;
            string trade = pick.SizeBytes == biggest.SizeBytes
                ? ", the same cache as the other CCD, so this is a preference rather than a trade"
                : $", giving up {biggest.SizeBytes / (1024 * 1024)} MB of L3 on CCD {biggest.Id}";
            return new GameGroup(pick, $" - chosen by you{trade}.");
        }

        // Auto: the largest cache group when the groups differ by enough to be a real difference.
        // VCacheGroup already applies the 64 MB floor and the 1.5x ratio; reusing it keeps one rule.
        if (topo.VCacheGroup is { } vc)
            return new GameGroup(vc, " - the largest L3 on this CPU.");

        // Symmetric cache (9950X3D2: 96 + 96) or an asymmetric part below the V-Cache floor. There is no
        // cache difference to prefer, so fall back to Windows' own ranking - and say that is what happened.
        int topCpu = p.CpuSets.Count > 0
            ? p.CpuSets.OrderByDescending(s => s.SchedulingClass).ThenBy(s => s.LogicalIndex).First().LogicalIndex
            : 0;
        var group = topo.L3Groups.FirstOrDefault(g => g.Cpus().Contains(topCpu)) ?? topo.L3Groups[0];
        bool symmetric = topo.L3Groups.Select(g => g.SizeBytes).Distinct().Count() == 1;
        return new GameGroup(group, symmetric
            ? " - both CCDs have the same L3, so this follows Windows' core ranking rather than cache."
            : " - the largest L3 on this CPU.");
    }
    public static string Build(SystemProfile p, AffinityPlan plan, GameCcdChoice gameCcd = GameCcdChoice.Auto)
    {
        var split = SplitFor(p, plan, gameCcd) ?? throw new InvalidOperationException("This CPU has one CCD and no E-cores: there's nothing to separate.");
        string games = CpuTopology.FormatCpus(split.GameCpus, ";");

        // Refuse a catch-all that would be worse than none. Emitting a mask that overlapped device CPUs
        // would be the old defect all over again, so the only two honest options are "a useful mask" or
        // "no catch-all, and say why".
        bool emitCatchAll = split.OtherCpus.Count >= MinBackgroundCpus;
        string other = emitCatchAll ? CpuTopology.FormatCpus(split.OtherCpus, ";") : "";

        var rules = new JsonArray();
        if (emitCatchAll)
            rules.Add(new JsonObject { ["cpuAffinity"] = other, ["cpuAffinityDelay"] = 0, ["processName"] = "*.*" });
        rules.Add(new JsonObject { ["cpuAffinity"] = games, ["cpuAffinityDelay"] = 0, ["processName"] = "dwm.exe" });
        foreach (string g in Games)
        {
            // No priority key. Bitsum's own setup guide: "Leave the game's priority alone.
            // Raising a game to High or Realtime priority rarely helps and can cause stutter or
            // hangs." Emitting High for sixty games shipped the thing the vendor warns against.
            rules.Add(new JsonObject
            {
                ["cpuAffinity"] = games,
                ["cpuAffinityDelay"] = 0,
                ["processName"] = g,
            });
        }

        var order = new JsonArray();
        foreach (string g in Games) order.Add(g);
        order.Add("dwm.exe");
        // Keep the order array in step with the rules: an order entry for a rule that was never emitted
        // is a second, quieter version of the same bug.
        if (emitCatchAll) order.Add("*.*");

        string background = emitCatchAll
            ? $"background on CPUs {other}, which excludes every CPU a device uses for interrupts"
            : $"no catch-all rule: only {split.OtherCpus.Count} CPU(s) are free of devices, too few to hold every background process, so leave background placement to Windows and trim device pins";

        var root = new JsonObject
        {
            ["$id"] = SchemaId,
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Process Lasso Rules",
            ["description"] = $"Stutter Guard starter rules for {p.CpuName}: games and dwm.exe on {split.GameLabel} (CPUs {games}); {background}. Priorities are left at Windows defaults and dynamic thread priority boost stays on. Add your own games.",
            ["cpuModel"] = p.CpuName,
            ["cpuAffinityRuleOrder"] = order,
            ["ruleSets"] = rules,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Write(SystemProfile p, AffinityPlan plan, string? folder = null, GameCcdChoice gameCcd = GameCcdChoice.Auto)
    {
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string file = Path.Combine(folder, $"processlasso-rules-dpc-tweaker-{DateTime.Now:yyyyMMdd-HHmm}.json");
        File.WriteAllText(file, Build(p, plan, gameCcd), new UTF8Encoding(false));
        Log.Info("Lasso rules written: " + file);
        return file;
    }
}
