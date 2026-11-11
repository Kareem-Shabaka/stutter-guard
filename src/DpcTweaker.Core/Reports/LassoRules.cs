using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;

namespace DpcTweaker.Core.Reports;

/// <summary>
/// Starter Process Lasso rules for core separation, in Bitsum's JSON rules format
/// (processlasso.schema.json 2026-01-17, the format the author's 48-rule "Game Mode" file uses):
/// games and dwm.exe on the game cores at High priority with thread priority boost off,
/// and a catch-all *.* on the other cores as the last rule.
/// </summary>
public static class LassoRules
{
    public const string SchemaId = "https://bitsum.com/json-schemas/2026-01-17-00/processlasso.schema.json";

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

    /// <summary>Which CPUs games get and which everything else gets. Null when the CPU has nothing to separate.</summary>
    public static Split? SplitFor(SystemProfile p, AffinityPlan plan)
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
        else if (topo.IsDualCcdX3D)
        {
            game = topo.VCacheGroup!.Cpus().ToList();
            label = "the V-Cache CCD";
        }
        else if (topo.IsMultiCcd)
        {
            int topCpu = p.CpuSets.Count > 0 ? p.CpuSets.OrderByDescending(s => s.SchedulingClass).ThenBy(s => s.LogicalIndex).First().LogicalIndex : 0;
            var group = topo.L3Groups.FirstOrDefault(g => g.Cpus().Contains(topCpu)) ?? topo.L3Groups[0];
            game = group.Cpus().ToList();
            label = "the CCD with the top-ranked cores";
        }
        else return null;

        var other = all.Except(game).ToList();
        string note = "";
        // Like the author's catch-all (18-31): keep background apps off the GPU's interrupt core too, if there's room.
        if (plan.Assignments.FirstOrDefault(a => a.Role == DeviceRole.Gpu) is { } gpu)
        {
            var gpuCore = topo.Cpus.Where(c => gpu.Cpus.Any(g => topo.Cpus.First(x => x.Index == g).CoreId == c.CoreId)).Select(c => c.Index).ToList();
            if (gpuCore.All(other.Contains) && other.Count - gpuCore.Count >= 4)
            {
                other = other.Except(gpuCore).ToList();
                note = $" CPUs {CpuTopology.FormatCpus(gpuCore)} (the GPU's interrupt core) are left out of the catch-all, so background apps don't share it.";
            }
        }
        return new Split(game, other, label, note);
    }

    public static string Build(SystemProfile p, AffinityPlan plan)
    {
        var split = SplitFor(p, plan) ?? throw new InvalidOperationException("This CPU has one CCD and no E-cores: there's nothing to separate.");
        string games = CpuTopology.FormatCpus(split.GameCpus);
        string other = CpuTopology.FormatCpus(split.OtherCpus);

        var rules = new JsonArray
        {
            new JsonObject { ["cpuAffinity"] = other, ["cpuAffinityDelay"] = 0, ["processName"] = "*.*" },
            new JsonObject { ["cpuAffinity"] = games, ["cpuAffinityDelay"] = 0, ["processName"] = "dwm.exe" },
        };
        foreach (string g in Games)
        {
            rules.Add(new JsonObject
            {
                ["cpuAffinity"] = games,
                ["cpuAffinityDelay"] = 0,
                ["priorityClass"] = "High",
                ["priorityClassEnforcedByRegistry"] = false,
                ["processName"] = g,
                ["threadPriorityBoosts"] = "off",
            });
        }

        var order = new JsonArray();
        foreach (string g in Games) order.Add(g);
        order.Add("dwm.exe");
        order.Add("*.*");
        var gamesOnly = new JsonArray();
        foreach (string g in Games) gamesOnly.Add(g);
        var gamesOnly2 = new JsonArray();
        foreach (string g in Games) gamesOnly2.Add(g);

        var root = new JsonObject
        {
            ["$id"] = SchemaId,
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Process Lasso Rules",
            ["description"] = $"Stutter Guard starter rules for {p.CpuName}: games and dwm.exe on {split.GameLabel} (CPUs {games}), High priority, thread priority boost off; everything else on CPUs {other}. Add your own games.",
            ["cpuModel"] = p.CpuName,
            ["cpuAffinityRuleOrder"] = order,
            ["priorityClassRuleOrder"] = gamesOnly,
            ["priorityBoostRuleOrder"] = gamesOnly2,
            ["ruleSets"] = rules,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Write(SystemProfile p, AffinityPlan plan, string? folder = null)
    {
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string file = Path.Combine(folder, $"processlasso-rules-dpc-tweaker-{DateTime.Now:yyyyMMdd-HHmm}.json");
        File.WriteAllText(file, Build(p, plan), new UTF8Encoding(false));
        Log.Info("Lasso rules written: " + file);
        return file;
    }
}
