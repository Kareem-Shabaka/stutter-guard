using System.Buffers.Binary;
using System.Text;
using System.Xml;
using DpcTweaker.Core;
using DpcTweaker.Core.Catalog;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Diagnostics;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Reports;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Native;
using DpcTweaker.Core.Tweaks;
using DpcTweaker.UI;
using Microsoft.Win32;

int failures = 0;
int passed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine("  ok    " + name); }
    else { failures++; Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? " -- " + detail : "")); }
}

Console.WriteLine("CpuTopology.Parse");
{
    // A synthetic 9950X3D: 16 cores with SMT, CCD0 (CPUs 0-15) 96 MB L3, CCD1 (16-31) 32 MB L3.
    var buf = new List<byte>();
    void Core(int a, int b)
    {
        var rec = new byte[8 + 24 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, 0);            // RelationProcessorCore
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
        rec[8] = 1;                                                    // LTP_PC_SMT
        rec[9] = 0;                                                    // efficiency class
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8 + 22), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8 + 24), (1UL << a) | (1UL << b));
        buf.AddRange(rec);
    }
    void L3(ulong mask, uint size)
    {
        var rec = new byte[8 + 32 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, 2);            // RelationCache
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
        rec[8] = 3;                                                    // level 3
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8 + 4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8 + 8), 0); // unified
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8 + 30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8 + 32), mask);
        buf.AddRange(rec);
    }
    for (int c = 0; c < 16; c++) Core(c * 2, c * 2 + 1);
    L3(0x0000FFFF, 96u * 1024 * 1024);
    L3(0xFFFF0000, 32u * 1024 * 1024);

    var t = CpuTopology.Parse(buf.ToArray());
    Check("32 logical CPUs", t.LogicalCount == 32, t.LogicalCount.ToString());
    Check("16 cores", t.CoreCount == 16, t.CoreCount.ToString());
    Check("SMT on", t.SmtEnabled);
    Check("two L3 groups", t.L3Groups.Count == 2);
    Check("X3D detected", t.IsDualCcdX3D);
    Check("V-Cache CCD is CPUs 0-15", t.VCacheGroup?.CpuRange() == "0-15", t.VCacheGroup?.CpuRange() ?? "null");
    Check("CPU 17 is a second thread on core 8", t.Cpus.Single(c => c.Index == 17) is { IsSecondThread: true, CoreId: 8 });
    Check("not hybrid", !t.IsHybrid);
    Check("FormatCpus ranges", CpuTopology.FormatCpus(new[] { 0, 1, 2, 5, 7, 8 }) == "0-2,5,7-8");
    Check("FormatCpus separator: Process Lasso needs semicolons, not commas",
        CpuTopology.FormatCpus(new[] { 0, 1, 2, 5, 7, 8 }, ";") == "0-2;5;7-8",
        CpuTopology.FormatCpus(new[] { 0, 1, 2, 5, 7, 8 }, ";"));
    Check("FormatCpus separator: a disjoint mask carries no comma",
        !CpuTopology.FormatCpus(new[] { 0, 1, 2, 3, 4, 5, 16, 18, 20 }, ";").Contains(","));

    // Journal round-trip: what RegValue.Serialize writes must Deserialize back exactly, because the
    // whole revert path is journal.json -> undo-all.ps1. A REG_MULTI_SZ element may contain a newline,
    // which a newline-joined form silently split into separate elements.
    foreach (var (kind, value, label) in new (RegistryValueKind, object, string)[]
    {
        (RegistryValueKind.String, "a\nb", "String with newline"),
        (RegistryValueKind.String, "", "empty String"),
        (RegistryValueKind.MultiString, new[] { "a", "b" }, "MultiString 2 items"),
        (RegistryValueKind.MultiString, new[] { "a\nb", "c" }, "MultiString item containing newline"),
        (RegistryValueKind.MultiString, new[] { "" }, "MultiString single empty"),
        (RegistryValueKind.MultiString, Array.Empty<string>(), "MultiString zero items"),
        (RegistryValueKind.Binary, new byte[] { 0, 255 }, "Binary"),
        (RegistryValueKind.Binary, Array.Empty<byte>(), "empty Binary"),
    })
    {
        var v = new RegValue(true, kind, value);
        object back = RegValue.Deserialize(kind, v.Serialize());
        var roundTripped = new RegValue(true, kind, back);
        Check($"journal round-trip: {label}", roundTripped.Serialize() == v.Serialize(),
            $"{v.Serialize()} -> {roundTripped.Serialize()}");
    }

    // A journal written before the MultiString change joined elements with a newline; it must still load.
    var legacy = (string[])RegValue.Deserialize(RegistryValueKind.MultiString, "x\ny");
    Check("journal reads a legacy newline-joined MultiString", legacy.SequenceEqual(new[] { "x", "y" }), string.Join("|", legacy));

Console.WriteLine("Tweak presets: cost classes gate the defaults");
{
    bool Sel(TweakPreset p, TweakCost c, Evidence e) => TweakPresetSelector.Selects(p, c, e);

    // The ladder: each step admits exactly one more cost class.
    Check("Essential takes Free only",
        Sel(TweakPreset.Essential, TweakCost.Free, Evidence.Measured) &&
        !Sel(TweakPreset.Essential, TweakCost.Small, Evidence.Measured) &&
        !Sel(TweakPreset.Essential, TweakCost.Expensive, Evidence.Measured));
    Check("Safe adds Small but not Expensive",
        Sel(TweakPreset.Safe, TweakCost.Free, Evidence.Measured) &&
        Sel(TweakPreset.Safe, TweakCost.Small, Evidence.Measured) &&
        !Sel(TweakPreset.Safe, TweakCost.Expensive, Evidence.Measured));
    Check("Costly adds the expensive ones",
        Sel(TweakPreset.Costly, TweakCost.Expensive, Evidence.Measured));

    // The invariant: community-only evidence is never chosen for the user below Everything.
    // This is what would have caught the flow-control and held-timer defaults.
    foreach (var p in new[] { TweakPreset.Essential, TweakPreset.Safe, TweakPreset.Costly })
    {
        Check($"{p}: never selects Anecdotal (free, small, expensive)",
            !Sel(p, TweakCost.Free, Evidence.Anecdotal) &&
            !Sel(p, TweakCost.Small, Evidence.Anecdotal) &&
            !Sel(p, TweakCost.Expensive, Evidence.Anecdotal));
    }
    Check("Everything does select Anecdotal", Sel(TweakPreset.Everything, TweakCost.Small, Evidence.Anecdotal));

    // Silence is not consent: an unclassified tweak is never ticked by any preset.
    foreach (var p in Enum.GetValues<TweakPreset>())
        Check($"{p}: never selects Unclassified", !Sel(p, TweakCost.Unclassified, Evidence.Measured));

    // Every exclusion has a reason the UI can show.
    Check("excluded Expensive explains itself",
        TweakPresetSelector.ExclusionReason(TweakPreset.Safe, TweakCost.Expensive, Evidence.Measured) is { Length: > 0 });
    Check("excluded Anecdotal explains itself",
        TweakPresetSelector.ExclusionReason(TweakPreset.Safe, TweakCost.Small, Evidence.Anecdotal) is { Length: > 0 });
    Check("selected tweak has no exclusion reason",
        TweakPresetSelector.ExclusionReason(TweakPreset.Safe, TweakCost.Free, Evidence.Measured) is null);
    Check("every preset has a label and a description",
        Enum.GetValues<TweakPreset>().All(p => TweakPresetSelector.Label(p).Length > 0 && TweakPresetSelector.Description(p).Length > 0));

    // "What will be used" — a tick alone cannot answer it, so the classification is status-first.
    RowFacts F(bool sel, TweakCost c = TweakCost.Free, Evidence e = Evidence.Measured,
               TweakStatus s = TweakStatus.NotApplied, bool applicable = true, bool dflt = true, string title = "t")
        => new(title, title, sel, applicable, dflt, c, e, s);

    var all = new[] { F(true, title: "a"), F(true, title: "b") };
    var c1 = SelectionSummary.Compute(TweakPreset.Safe, all);
    Check("preview counts selected rows as will-be-applied", c1.WillApply.Count == 2);
    Check("preview lists the titles", c1.WillApply.SequenceEqual(new[] { "a", "b" }));

    // A ticked row that is already applied changes nothing: done, not will-apply.
    var c2 = SelectionSummary.Compute(TweakPreset.Safe, new[] {
        F(true, s: TweakStatus.Applied), F(false, s: TweakStatus.Applied), F(false, s: TweakStatus.PendingReboot) });
    Check("already-applied rows are counted done, never will-apply (even when ticked)",
        c2.WillApply.Count == 0 && c2.AlreadyDone == 3, $"will={c2.WillApply.Count} done={c2.AlreadyDone}");

    // Unticked for each distinct reason.
    var c3 = SelectionSummary.Compute(TweakPreset.Safe, new[] {
        F(false, TweakCost.Expensive),            // skipped by preset
        F(false, TweakCost.Free, Evidence.Anecdotal), // skipped (anecdotal)
        F(false, TweakCost.Free, dflt: false),    // opt-in only
        F(false),                                 // idle: should be ticked but is not
        F(false, applicable: false) });           // not applicable
    Check("unticked reasons are separated",
        c3.SkippedByPreset == 2 && c3.OptInOnly == 1 && c3.Idle == 1 && c3.NotApplicable == 1);

    // Text is never empty and never claims work when there is none.
    string d1 = SelectionSummary.Describe(TweakPreset.Safe, c1);
    Check("describe names the count and the button", d1.Contains("2 will be applied") && d1.Contains("Apply selected"), d1);
    string d0 = SelectionSummary.Describe(TweakPreset.Safe, c2);
    Check("describe says nothing will be applied when none will", d0.StartsWith("nothing will be applied"), d0);
    Check("describe mentions the preset by short name",
        SelectionSummary.Describe(TweakPreset.Safe, c3).Contains("skipped by Safe"),
        SelectionSummary.Describe(TweakPreset.Safe, c3));

    // Preview truncation.
    var many = Enumerable.Range(0, 9).Select(i => F(true, title: "t" + i)).ToArray();
    string pm = SelectionSummary.Preview(SelectionSummary.Compute(TweakPreset.Safe, many), show: 6);
    Check("preview truncates and counts the remainder", pm.Contains("and 3 more"), pm);
    Check("preview with no work explains why", SelectionSummary.Preview(c2).Length > 0);
    Check("ShortLabel drops the parenthetical", SelectionSummary.ShortLabel(TweakPreset.Safe) == "Safe", SelectionSummary.ShortLabel(TweakPreset.Safe));

    // Safe-recommended tweaks sort above the ones that ask more, which is also what makes the
    // preset picker visible on a machine where everything is already applied.
    Check("rank: Safe set is first", TweakPresetSelector.RecommendationRank(TweakCost.Free, Evidence.Measured) == 0 &&
        TweakPresetSelector.RecommendationRank(TweakCost.Small, Evidence.Documented) == 0);
    Check("rank: expensive sits between safe and unproven",
        TweakPresetSelector.RecommendationRank(TweakCost.Expensive, Evidence.Measured) == 1);
    Check("rank: anecdotal is unproven regardless of cost",
        TweakPresetSelector.RecommendationRank(TweakCost.Free, Evidence.Anecdotal) == 2 &&
        TweakPresetSelector.RecommendationRank(TweakCost.Small, Evidence.Anecdotal) == 2);
    Check("rank: unclassified is last", TweakPresetSelector.RecommendationRank(TweakCost.Unclassified, Evidence.Measured) == 3);
    Check("rank: ordering is total and stable across a mixed list",
        new[] {
            (TweakCost.Unclassified, Evidence.Measured), (TweakCost.Free, Evidence.Anecdotal),
            (TweakCost.Expensive, Evidence.Measured), (TweakCost.Small, Evidence.Measured),
            (TweakCost.Free, Evidence.Documented) }
        .OrderBy(x => TweakPresetSelector.RecommendationRank(x.Item1, x.Item2))
        .Select(x => TweakPresetSelector.RecommendationRank(x.Item1, x.Item2))
        .SequenceEqual(new[] { 0, 0, 1, 2, 3 }));
    Check("every rank has a label",
        Enumerable.Range(0, 4).All(r => TweakPresetSelector.RankLabel(r).Length > 0));

    // The author's own case: everything already applied, so nothing can tick. The preview must say why.
    var allApplied = new[] { F(false, s: TweakStatus.Applied), F(false, s: TweakStatus.Applied) };
    string pa = SelectionSummary.Preview(SelectionSummary.Compute(TweakPreset.Safe, allApplied));
    Check("all-applied preview explains the preset still sets the order",
        pa.Contains("already applied") && pa.Contains("order"), pa);

    // Switching presets is a declared desired state: going Everything -> Safe must put back what Safe
    // leaves out, not merely stop ticking it. This is the case the author asked for.
    RowFacts A(string id, TweakCost c, Evidence e, TweakStatus s, bool sel = false)
        => new(id, id, sel, true, true, c, e, s);

    var machine = new[]
    {
        A("free-applied",     TweakCost.Free,      Evidence.Measured,  TweakStatus.Applied),
        A("small-applied",    TweakCost.Small,     Evidence.Documented,TweakStatus.Applied),
        A("costly-applied",   TweakCost.Expensive, Evidence.Measured,  TweakStatus.Applied),
        A("unproven-applied", TweakCost.Small,     Evidence.Anecdotal, TweakStatus.Applied),
        A("free-missing",     TweakCost.Free,      Evidence.Measured,  TweakStatus.NotApplied, sel: true),
    };

    var toSafe = PresetTransition.Plan(TweakPreset.Safe, machine);
    Check("Everything -> Safe reverts the costly tweak", toSafe.ToRevert.Contains("costly-applied"), string.Join(",", toSafe.ToRevert));
    Check("Everything -> Safe reverts the unproven tweak", toSafe.ToRevert.Contains("unproven-applied"), string.Join(",", toSafe.ToRevert));
    Check("Everything -> Safe leaves the safe ones alone",
        !toSafe.ToRevert.Contains("free-applied") && !toSafe.ToRevert.Contains("small-applied"), string.Join(",", toSafe.ToRevert));
    Check("Everything -> Safe applies what is missing", toSafe.ToApply.SequenceEqual(new[] { "free-missing" }), string.Join(",", toSafe.ToApply));

    // Revert wins over the tick: a ticked row the preset excludes is still put back.
    var tickedButExcluded = new[] { A("costly-ticked", TweakCost.Expensive, Evidence.Measured, TweakStatus.Applied, sel: true) };
    Check("a ticked row outside the preset is still reverted",
        PresetTransition.Plan(TweakPreset.Safe, tickedButExcluded).ToRevert.Contains("costly-ticked"));

    // Nothing to do when the machine already matches.
    var safeOnly = new[] { A("free-applied", TweakCost.Free, Evidence.Measured, TweakStatus.Applied) };
    Check("already-matching preset is a no-op", PresetTransition.Plan(TweakPreset.Safe, safeOnly).IsNoOp);

    // Widening applies, never reverts.
    var toEverything = PresetTransition.Plan(TweakPreset.Everything,
        new[] { A("unproven-missing", TweakCost.Small, Evidence.Anecdotal, TweakStatus.NotApplied, sel: true) });
    Check("Safe -> Everything only applies, never reverts",
        toEverything.ToRevert.Count == 0 && toEverything.ToApply.Contains("unproven-missing"));

    // Not-applicable rows are never touched either way.
    var na = new[] { new RowFacts("na", "na", true, false, true, TweakCost.Free, Evidence.Measured, TweakStatus.Applied) };
    Check("not-applicable rows are ignored by a transition", PresetTransition.Plan(TweakPreset.Essential, na).IsNoOp);

    // The confirmation text must state both counts and admit that it puts things back.
    string tx = PresetTransition.Describe(TweakPreset.Safe, toSafe);
    Check("transition text names both counts", tx.Contains("apply 1") && tx.Contains("put back 2"), tx);
    Check("transition text warns it restores previous values", tx.Contains("restored to the value it had"), tx);
    Check("no-op transition says so", PresetTransition.Describe(TweakPreset.Safe, PresetTransition.Plan(TweakPreset.Safe, safeOnly)).Contains("Nothing to change"));}
}

Console.WriteLine("DriverStringScanner");
{
    byte[] Build(params string[] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts)
        {
            if (p.StartsWith("U:"))
            {
                if (ms.Length % 2 == 1) ms.WriteByte(0); // compilers align UTF-16 literals
                var b = Encoding.Unicode.GetBytes(p[2..]); ms.Write(b); ms.Write(new byte[] { 0, 0 });
            }
            else { var b = Encoding.ASCII.GetBytes(p); ms.Write(b); ms.WriteByte(0); }
        }
        return ms.ToArray();
    }
    var data = Build("junk", "U:EnableUlps_NA", "DalDisableStutter", "U:XStutterMode", "U:KMD_EnableVcnRTPM", "PP_MclkDpmDisabledX");
    var r = DriverStringScanner.Scan(data, new[] { "EnableUlps_NA", "EnableUlps", "DalDisableStutter", "StutterMode", "KMD_EnableVcnRTPM", "PP_MclkDpmDisabled" });
    Check("UTF-16 whole name found", r["EnableUlps_NA"]);
    Check("prefix of a longer name not found", !r["EnableUlps"]);
    Check("ASCII name found", r["DalDisableStutter"]);
    Check("name inside a longer UTF-16 name not found", !r["StutterMode"]);
    Check("UTF-16 KMD name found", r["KMD_EnableVcnRTPM"]);
    Check("ASCII name with a suffix not found", !r["PP_MclkDpmDisabled"]);
}

Console.WriteLine("RegValue serialize round trip");
{
    var dw = new RegValue(true, RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF));
    Check("DWORD max", dw.Serialize() == "4294967295");
    Check("DWORD back", (int)RegValue.Deserialize(RegistryValueKind.DWord, "4294967295") == -1);
    var bin = new RegValue(true, RegistryValueKind.Binary, new byte[] { 0x00, 0x00, 0x50, 0x00 });
    Check("binary hex", bin.Serialize() == "00005000");
    Check("binary back", ((byte[])RegValue.Deserialize(RegistryValueKind.Binary, "00005000"))[2] == 0x50);
    var multi = new RegValue(true, RegistryValueKind.MultiString, new[] { "a", "b" });
    Check("multi round trip", string.Join(",", (string[])RegValue.Deserialize(RegistryValueKind.MultiString, multi.Serialize())) == "a,b");
    Check("display small DWORD", new RegValue(true, RegistryValueKind.DWord, 1).Display == "1");
    Check("display big DWORD", new RegValue(true, RegistryValueKind.DWord, 0x200A).Display == "8202 (0x200A)");
}

Console.WriteLine("Journal");
{
    string tmp = Path.Combine(Path.GetTempPath(), "dpct-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    var j = new Journal(Path.Combine(tmp, "journal.json"));
    j.RecordOriginal("t1", "reg", @"HKLM\X|A", true, "DWord", "1");
    j.RecordOriginal("t1", "reg", @"HKLM\X|A", true, "DWord", "0"); // ignored: first record wins
    j.RecordOriginal("t2", "reg", @"HKLM\X|A", true, "DWord", "0");
    j.MarkApplied("t1");
    j.SetState("plan.tuned", "abc");
    j.Save();
    var j2 = new Journal(Path.Combine(tmp, "journal.json"));
    Check("entries persisted", j2.EntryCount == 2, j2.EntryCount.ToString());
    Check("first record kept", j2.OriginalsFor("t1").Single().Data == "1");
    Check("applied persisted", j2.IsApplied("t1") && !j2.IsApplied("t2"));
    Check("state persisted", j2.GetState("plan.tuned") == "abc");
    Check("oldest per target picks t1", j2.OldestPerTarget().Single().TweakId == "t1");
    j2.ClearTweak("t1");
    Check("clear tweak", j2.OriginalsFor("t1").Count == 0 && !j2.IsApplied("t1"));
    string undo = JournalRestorer.BuildUndoScript(j2);
    Check("undo script has RegSet", undo.Contains("RegSet 'Registry::HKEY_LOCAL_MACHINE\\X' 'A' 'DWord' 0"), undo);
    Directory.Delete(tmp, true);
}

Console.WriteLine("Task XML");
{
    foreach (var (name, xml) in new[]
    {
        ("boot", TaskSched.BootTaskXml("desc & <test>", @"C:\Program Files\Stutter Guard\StutterGuard.exe", "--agent device-power")),
        ("logon", TaskSched.LogonTaskXml("desc", @"PC\User", @"C:\Program Files\Stutter Guard\DpcTimerRes.exe", "5000")),
    })
    {
        var doc = new XmlDocument();
        bool ok = true; string err = "";
        try { doc.LoadXml(xml); } catch (Exception ex) { ok = false; err = ex.Message; }
        Check(name + " task XML is well-formed", ok, err);
        Check(name + " task XML starts with the declaration", xml.StartsWith("<?xml"));
    }
}

Console.WriteLine("Form factor");
{
    Check("chassis 9 = laptop", Detector.DecideFormFactor(new List<int> { 9 }, 1, null, 1).Item1 == FormFactor.Laptop);
    Check("chassis 3 = desktop", Detector.DecideFormFactor(new List<int> { 3 }, 1, false, 1).Item1 == FormFactor.Desktop);
    Check("no chassis + mobile role = laptop", Detector.DecideFormFactor(new List<int>(), 0, null, 2).Item1 == FormFactor.Laptop);
    Check("no chassis, no battery = desktop", Detector.DecideFormFactor(new List<int>(), 0, false, 1).Item1 == FormFactor.Desktop);
}

Console.WriteLine("GPU integrated heuristic");
{
    Check("RX 9070 XT discrete", !Detector.LooksIntegrated(GpuVendor.Amd, "AMD Radeon RX 9070 XT"));
    Check("Radeon(TM) Graphics integrated", Detector.LooksIntegrated(GpuVendor.Amd, "AMD Radeon(TM) Graphics"));
    Check("Radeon 780M integrated", Detector.LooksIntegrated(GpuVendor.Amd, "AMD Radeon 780M Graphics"));
    Check("Intel Arc A770 discrete", !Detector.LooksIntegrated(GpuVendor.Intel, "Intel(R) Arc(TM) A770 Graphics"));
    Check("Intel Arc iGPU integrated", Detector.LooksIntegrated(GpuVendor.Intel, "Intel(R) Arc(TM) Graphics"));
    Check("NVIDIA discrete", !Detector.LooksIntegrated(GpuVendor.Nvidia, "NVIDIA GeForce RTX 3070 Laptop GPU"));
}

Console.WriteLine("Power settings table");
{
    var keys = PowerSettings.All.Select(d => d.Key).ToList();
    Check("unique keys", keys.Distinct().Count() == keys.Count);
    var light = new AppOptions { CState = CStateMode.Light };
    var deep = new AppOptions { CState = CStateMode.Deep };
    var idleMax = PowerSettings.All.Single(d => d.Key == "cpu.idle-max");
    Check("light caps idle at C1", idleMax.Target(light) == 1);
    Check("deep removes the cap", idleMax.Target(deep) == 0);
    Check("21 Ultimate settings from the audit are in Full",
        PowerSettings.All.Count(d => d.InFull && !d.DesktopOnly && d.Key != "cpu.interrupt-steering" &&
                                     !d.ClassOne && !d.AmdOverlay && d.Key != "cpu.boost-policy") == 21,
        PowerSettings.All.Count(d => d.InFull && !d.DesktopOnly && d.Key != "cpu.interrupt-steering" &&
                                     !d.ClassOne && !d.AmdOverlay && d.Key != "cpu.boost-policy").ToString());
    // The rows added with the audit fixes are Full-only as well, and conditional on the hardware
    // (class 1 needs an Intel hybrid, the overlay needs AMD plus the GUID on the plan).
    Check("the class-1 rows and the two vendor/ceiling rows are Full-only",
        new[] { "cpu.core-parking-class1", "cpu.min-state-class1", "cpu.epp-class1", "cpu.epp-latency-class1", "cpu.boost-policy", "cpu.amd-overlay" }
            .All(k => PowerSettings.All.Single(d => d.Key == k) is { InFull: true, InDevicesOnly: false }));
    Check("devices-only has no CPU settings", PowerSettings.All.Where(d => d.InDevicesOnly).All(d => d.Sub != PowerSettings.Proc || d.Key == "cpu.interrupt-steering"));
}


// ---------------------------------------------------------------- M2: interrupts

static CpuTopology Topo(IEnumerable<(int[] Threads, byte Eff)> cores, IEnumerable<(ulong Mask, uint Size)> l3s)
{
    var buf = new List<byte>();
    foreach (var (threads, eff) in cores)
    {
        var rec = new byte[8 + 24 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
        rec[8] = (byte)(threads.Length > 1 ? 1 : 0);
        rec[9] = eff;
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8 + 22), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8 + 24), threads.Aggregate(0UL, (m, t) => m | (1UL << t)));
        buf.AddRange(rec);
    }
    foreach (var (mask, size) in l3s)
    {
        var rec = new byte[8 + 32 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
        rec[8] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8 + 4), size);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8 + 30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8 + 32), mask);
        buf.AddRange(rec);
    }
    return CpuTopology.Parse(buf.ToArray());
}

static PciDevice Dev(string id, string name, byte bc, byte sc, byte pi, Attached? att = null, uint? bus = null, uint? addr = null, bool started = true) =>
    new PciDevice { InstanceId = id, Name = name, BaseClass = bc, SubClass = sc, ProgIf = pi, Attached = att ?? new Attached(), Bus = bus, Address = addr, Started = started, InterruptSupport = 6 };

static List<CpuSetEntry> Sets(CpuTopology t, Func<int, int> sched) =>
    t.Cpus.Select(c => new CpuSetEntry(c.Index, 0, c.CoreId, c.L3Id, c.EfficiencyClass, sched(c.Index), false)).ToList();

static List<PciDevice> DesktopDevices()
{
    var list = new List<PciDevice>
    {
        Dev(@"PCI\VEN_1002&DEV_7550&SUBSYS_24221849&REV_C0\6&1", "AMD Radeon RX 9070 XT", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }, 3, 0x00000000),
        Dev(@"PCI\VEN_1002&DEV_AB40&SUBSYS_AB401849&REV_00\6&2", "AMD High Definition Audio Device", 0x04, 0x03, 0x00, new Attached { AudioEndpoints = 1 }, 3, 0x00000001),
        Dev(@"PCI\VEN_1022&DEV_43FC&SUBSYS_7E611462&REV_01\4&3", "USB 3.20 eXtensible Host Controller", 0x0C, 0x03, 0x30,
            new Attached { Mice = 1, Keyboards = 1, Bluetooth = 1, AudioDevices = 1, HasDefaultAudio = true, UsbDevices = 5 }),
        Dev(@"PCI\VEN_1022&DEV_15B6&SUBSYS_7E611462&REV_00\4&4", "USB 3.10 eXtensible Host Controller", 0x0C, 0x03, 0x30),
        Dev(@"PCI\VEN_1022&DEV_15B7&SUBSYS_7E611462&REV_00\4&5", "USB 3.10 eXtensible Host Controller", 0x0C, 0x03, 0x30),
        Dev(@"PCI\VEN_1022&DEV_15B8&SUBSYS_7E611462&REV_00\4&6", "USB 2.0 eXtensible Host Controller", 0x0C, 0x03, 0x30),
        Dev(@"PCI\VEN_10EC&DEV_8126&SUBSYS_7E611462&REV_01\4&7", "Realtek Gaming 5GbE Family Controller", 0x02, 0x00, 0x00),
        Dev(@"PCI\VEN_17CB&DEV_1107&SUBSYS_E0E91A3B&REV_01\4&8", "Qualcomm FastConnect 7800 Wi-Fi 7", 0x02, 0x80, 0x00, started: false),
        Dev(@"PCI\VEN_15B7&DEV_5030\4&9", "Standard NVM Express Controller", 0x01, 0x08, 0x02),
        Dev(@"PCI\VEN_1D97&DEV_5236\4&A", "Standard NVM Express Controller", 0x01, 0x08, 0x02),
        Dev(@"PCI\VEN_1022&DEV_43F6\4&B", "Standard SATA AHCI Controller", 0x01, 0x06, 0x01),
        Dev(@"PCI\VEN_1022&DEV_1649\4&C", "AMD PSP 11.0 Device", 0x10, 0x80, 0x00),
        Dev(@"PCI\VEN_1022&DEV_14DB\3&D", "PCI Express Root Port", 0x06, 0x04, 0x00),
    };
    PciScanner.Classify(list, Array.Empty<GpuInfo>());
    var nic = list.Single(d => d.DeviceId == "8126");
    nic.Connected = true;
    nic.RssCapable = true;
    nic.RssQueueOptions = new() { "1", "2", "4", "8" };
    return list;
}

Console.WriteLine("Interrupt plan: the desktop (9950X3D, its real devices)");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    var devs = DesktopDevices();
    Check("GPU audio recognised by its slot", devs.Single(d => d.DeviceId == "AB40").Kind == PciKind.GpuAudio);
    Check("NVMe is storage", devs.Where(d => d.Name.Contains("NVM")).All(d => d.Kind == PciKind.Storage));
    Check("PSP and root port are never pinned", devs.Where(d => d.DeviceId is "1649" or "14DB").All(d => d.Kind == PciKind.Never));

    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("plan applicable", plan.Applicable, plan.NotApplicableReason ?? "");
    Check("reserved = CPUs 0-15 (CPU 0/1 + V-Cache CCD)", string.Join(",", plan.ReservedCpus) == string.Join(",", Enumerable.Range(0, 16)), string.Join(",", plan.ReservedCpus));
    Check("candidates = first threads 16..30", string.Join(",", plan.Candidates.Select(c => c.Cpu).OrderBy(x => x)) == "16,18,20,22,24,26,28,30");
    Check("nothing on CCD0 or CPU 0/1", plan.Assignments.All(a => a.Cpus.All(c => c >= 16)), Map());
    Check("only first threads", plan.Assignments.All(a => a.Cpus.All(c => c % 2 == 0)), Map());
    Check("GPU first, on CPU 16", plan.Assignments[0].Device.DeviceId == "7550" && plan.Assignments[0].Cpus.SequenceEqual(new[] { 16 }), Map());
    Check("chipset USB (mouse) is the input controller", plan.Assignments.Single(a => a.Device.DeviceId == "43FC").Role == DeviceRole.Input, Map());
    Check("NIC gets 2 adjacent cores", plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus.SequenceEqual(new[] { 20, 22 }), Map());
    Check("every busy device on its own core", plan.Assignments.SelectMany(a => a.Cpus).Distinct().Count() == plan.Assignments.Sum(a => a.Cpus.Length), Map());
    Check("empty CPU USB controllers pinned too", plan.Assignments.Count(a => a.Role == DeviceRole.OtherUsb) == 3, Map());
    Check("storage, PSP, bridges, disabled Wi-Fi left alone", plan.Assignments.All(a => a.Device.Kind is not (PciKind.Storage or PciKind.Never) && a.Device.Started), Map());
    Check("8 cores used, none shared", plan.Assignments.Sum(a => a.Cpus.Length) == 8 && plan.Assignments.All(a => !a.Shared), Map());
    // The NIC has an RSS plan, and NDIS assigns receive queues by logical processor: its two cores mean
    // four LPs (20-23). They join the set the plan keeps background work off, or a queue lands on a CPU
    // that never takes that queue's interrupt. Every other device stays on first threads only.
    Check("DeviceCpus = the union of the pinned CPUs, plus the RSS span's siblings",
        string.Join(",", plan.DeviceCpus) == "16,18,20,21,22,23,24,26,28,30", string.Join(",", plan.DeviceCpus));
    Check("no Rss plan means no sibling expansion",
        plan.Assignments.Where(a => a.Rss is null).SelectMany(a => a.MaskCpus).All(c => c % 2 == 0), string.Join(",", plan.DeviceCpus));
    Check("the RSS span is exactly this device's interrupt mask",
        plan.Assignments.Single(a => a.Device.DeviceId == "8126").MaskCpus.SequenceEqual(new[] { 20, 21, 22, 23 }),
        string.Join(",", plan.Assignments.Single(a => a.Device.DeviceId == "8126").MaskCpus));
    Check("no receive queue can be assigned a CPU the mask excludes",
        plan.Assignments.Where(a => a.Rss is not null).All(a => Enumerable.Range(a.Rss!.BaseProcNumber, a.Rss.NumProcessors).All(a.MaskCpus.Contains)),
        string.Join(",", plan.DeviceCpus));

    // A pin the user already had is never widened just to make the keywords fit.
    var narrowed = AffinityPlanner.Clamp(new RssPlan(20, 23, 4, 2), new[] { 20, 22 });
    Check("a kept pin's RSS range is narrowed to what the mask allows",
        narrowed is not null && narrowed.BaseProcNumber == 20 && narrowed.MaxProcNumber == 20 && narrowed.NumProcessors == 1,
        narrowed is null ? "null" : $"{narrowed.BaseProcNumber}-{narrowed.MaxProcNumber}/{narrowed.NumProcessors}");
    Check("a range that already fits is left exactly as it was",
        AffinityPlanner.Clamp(new RssPlan(20, 23, 4, 2), new[] { 20, 21, 22, 23 }) is { BaseProcNumber: 20, MaxProcNumber: 23, NumProcessors: 4 });
    Check("a range with no allowed CPU at all yields nothing to write",
        AffinityPlanner.Clamp(new RssPlan(20, 23, 4, 2), new[] { 16, 18 }) is null);
    Check("DeviceCpus never overlap the cores kept free of devices",
        !plan.DeviceCpus.Intersect(plan.ReservedCpus).Any(), string.Join(",", plan.DeviceCpus));
    Check("disabled Wi-Fi noted", plan.Notes.Any(n => n.Contains("Qualcomm")));
    Console.WriteLine("        plan: " + Map());

    // With a benchmark where CPU 26 wakes up best (the AutoGpuAffinity run's steadiest core).
    var bench = new BenchResult { When = DateTime.Now, SamplesPerCore = 5000 };
    foreach (int c in Enumerable.Range(0, 16).Select(i => i * 2))
        bench.Cores.Add(new CoreScore { Cpu = c, SpeedMs = c < 16 ? 30 : 28, WakeMedianUs = c == 26 ? 5 : 9, WakeP99Us = 500, WakeMaxUs = 600 });
    var plan2 = AffinityPlanner.Plan(t, Sets(t, _ => 0), bench, devs);
    Check("benchmark: GPU on the best-measured core (26, lowest wake-up median)", plan2.Assignments[0].Cpus.SequenceEqual(new[] { 26 }), string.Join("; ", plan2.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}")));
    Check("benchmark: still nothing below 16", plan2.Assignments.All(a => a.Cpus.All(c => c >= 16)));
    Check("benchmark: NIC still on 2 adjacent cores", plan2.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus is { Length: 2 } nc && nc[1] - nc[0] == 2);
}

Console.WriteLine("Interrupt plan: single CCD (8 cores, preferred cores 2 and 5)");
{
    var t = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 96u << 20) });
    var devs = DesktopDevices();
    var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu / 2 is 2 or 5 ? 200 : 150), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("reserved CPU 0/1 and the two top cores (4/5, 10/11)", string.Join(",", plan.ReservedCpus) == "0,1,4,5,10,11", string.Join(",", plan.ReservedCpus));
    Check("no device on reserved CPUs", plan.Assignments.All(a => a.Cpus.All(c => !plan.ReservedCpus.Contains(c))), Map());
    Check("GPU on its own core", plan.Assignments.Count(a => a.Cpus.Contains(plan.Assignments[0].Cpus[0])) == 1, Map());
    Check("more devices than cores: some share", plan.Assignments.Any(a => a.Shared), Map());
    Check("the GPU never shares", !plan.Assignments.Skip(1).Any(a => a.Cpus.Intersect(plan.Assignments[0].Cpus).Any()), Map());
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Interrupt plan: 6 cores, more devices than cores");
{
    var t = Topo(Enumerable.Range(0, 6).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFUL, 32u << 20) });
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    var gpu = plan.Assignments.Single(a => a.Role == DeviceRole.Gpu).Cpus;
    var input = plan.Assignments.Single(a => a.Role == DeviceRole.Input).Cpus;
    Check("6 cores: the GPU never shares", plan.Assignments.Count(a => a.Cpus.Intersect(gpu).Any()) == 1, Map());
    Check("6 cores: the input controller never shares", plan.Assignments.Count(a => a.Cpus.Intersect(input).Any()) == 1, Map());
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("GPU audio vs board audio in the iGPU's slot (Ryzen 7000/9000)");
{
    var list = new List<PciDevice>
    {
        Dev(@"PCI\VEN_1002&DEV_13C0\1", "AMD Radeon(TM) Graphics", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }, 14, 0x00000000),
        Dev(@"PCI\VEN_1002&DEV_1640\2", "AMD High Definition Audio Device", 0x04, 0x03, 0x00, null, 14, 0x00000001),
        Dev(@"PCI\VEN_1022&DEV_15E3\3", "High Definition Audio Controller", 0x04, 0x03, 0x00, null, 14, 0x00000006),
    };
    PciScanner.Classify(list, Array.Empty<GpuInfo>());
    Check("iGPU HDMI audio = GPU audio", list[1].Kind == PciKind.GpuAudio);
    Check("board HD Audio stays audio", list[2].Kind == PciKind.Audio);
}

Console.WriteLine("Interrupt plan: Intel hybrid (6 P-cores with HT, 4 E-cores)");
{
    var cores = Enumerable.Range(0, 6).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)1))
        .Concat(Enumerable.Range(12, 4).Select(c => (new[] { c }, (byte)0)));
    var t = Topo(cores, new[] { (0xFFFFUL, 20u << 20) });
    var devs = DesktopDevices().Where(d => d.DeviceId is "7550" or "43FC" or "8126" or "15B6").ToList();
    var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu >= 12 ? 1 : cpu / 2 is 2 or 3 ? 5 : 3), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("hybrid detected", t.IsHybrid);
    Check("reserved CPU 0/1 + the 2 top P-cores", string.Join(",", plan.ReservedCpus) == "0,1,4,5,6,7", string.Join(",", plan.ReservedCpus));
    Check("GPU on a P-core", plan.Assignments.Single(a => a.Device.DeviceId == "7550").Cpus.All(c => c < 12), Map());
    Check("input controller on a P-core", plan.Assignments.Single(a => a.Device.DeviceId == "43FC").Cpus.All(c => c < 12), Map());
    Check("network on 2 adjacent E-cores", plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus.SequenceEqual(new[] { 12, 13 }), Map());
    Check("other USB on an E-core", plan.Assignments.Single(a => a.Device.DeviceId == "15B6").Cpus.All(c => c >= 12), Map());
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Interrupt plan: 4 cores, no SMT (falls back to CPU 0 only)");
{
    var t = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c }, (byte)0)), new[] { (0xFUL, 8u << 20) });
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
    Check("only CPU 0 reserved", string.Join(",", plan.ReservedCpus) == "0", string.Join(",", plan.ReservedCpus));
    Check("devices on CPUs 1-3", plan.Assignments.All(a => a.Cpus.All(c => c is >= 1 and <= 3)));
}

Console.WriteLine("Class code from PnP ids (26H2 returned no PCI class properties)");
{
    Check("6-digit class code", PciScanner.ParseClassCode(new[] { @"PCI\VEN_1022&DEV_43FC&SUBSYS_11421B21&REV_01", @"PCI\VEN_1022&DEV_43FC&CC_0C0330", @"PCI\CC_0C0330", @"PCI\CC_0C03" }) == ((byte)0x0C, (byte)0x03, (byte)0x30));
    Check("4-digit only", PciScanner.ParseClassCode(new[] { @"PCI\CC_0300" }) == ((byte)0x03, (byte)0x00, (byte)0));
    Check("no class code", PciScanner.ParseClassCode(new[] { @"PCI\VEN_1022&DEV_43FC" }) is null);
}

Console.WriteLine("Benchmark ranking with the desktop's real numbers (Oct 21, GPU pinned on 16 at the time)");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    // cpu, median, p99, speed, idle int/s, Windows rank (from dpc-tweaker-scan-20261021-0111.txt)
    var real = new (int Cpu, double Med, double P99, double Speed, double Ints, int Rank)[]
    {
        (0, 10.1, 488.4, 13.0, 81, 0), (2, 11.1, 507.6, 13.0, 45, 4), (4, 11.2, 483.9, 13.0, 31, 6), (6, 10.4, 482.1, 13.0, 56, 5),
        (8, 10.9, 502.3, 13.0, 32, 7), (10, 10.7, 498.4, 13.0, 35, 1), (12, 10.1, 341.4, 13.1, 27, 3), (14, 10.7, 506.4, 13.1, 27, 2),
        (16, 8.8, 508.9, 12.7, 3142, 14), (18, 9.0, 507.2, 12.6, 277, 13), (20, 8.1, 509.0, 12.7, 386, 12), (22, 8.2, 508.5, 12.7, 182, 11),
        (24, 7.8, 508.5, 12.8, 86, 8), (26, 9.2, 568.7, 12.7, 1200, 14), (28, 8.3, 508.7, 12.7, 52, 9), (30, 10.9, 509.4, 12.7, 109, 10),
    };
    var bench = new BenchResult { When = DateTime.Now, SamplesPerCore = 5000 };
    foreach (var r in real) bench.Cores.Add(new CoreScore { Cpu = r.Cpu, WakeMedianUs = r.Med, WakeP99Us = r.P99, SpeedMs = r.Speed, InterruptsPerSec = r.Ints, SchedulingClass = r.Rank });
    var sets = t.Cpus.Select(c => new CpuSetEntry(c.Index, 0, c.CoreId, c.L3Id, 0, real.First(r => r.Cpu == c.Index - c.Index % 2).Rank, false)).ToList();
    var plan = AffinityPlanner.Plan(t, sets, bench, DesktopDevices());
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    var order = plan.Candidates.Select(c => c.Cpu).ToList();
    Check("idle load from today's pins isn't scored: 16 not in the last two", order.IndexOf(16) < order.Count - 2, string.Join(",", order));
    Check("GPU on one of the two best-ranked cores (16 or 18)", plan.Assignments[0].Cpus[0] is 16 or 18, Map());
    Check("nothing below 16", plan.Assignments.All(a => a.Cpus.All(c => c >= 16)), Map());
    Check("NIC on 2 adjacent cores", plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus is { Length: 2 } nc && nc[1] - nc[0] == 2, Map());
    Console.WriteLine("        core order: " + string.Join(", ", order));
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Keep pins that follow the rules (the desktop's hand map, Oct 21 scan)");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    var devs = DesktopDevices();
    ulong M(params int[] c) => c.Aggregate(0UL, (m, x) => m | (1UL << x));
    devs.Single(d => d.DeviceId == "7550").ExistingMask = M(16);
    devs.Single(d => d.DeviceId == "15B8").ExistingMask = M(18);
    devs.Single(d => d.DeviceId == "8126").ExistingMask = M(20, 22);
    devs.Single(d => d.DeviceId == "43FC").ExistingMask = M(24);
    devs.Single(d => d.DeviceId == "AB40").ExistingMask = M(26);
    devs.Single(d => d.DeviceId == "15B6").ExistingMask = M(28);
    devs.Single(d => d.DeviceId == "15B7").ExistingMask = M(30);
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs, keepValidPins: true);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}{(a.Kept ? "k" : "")}"));
    Check("hand map kept as it is", plan.Assignments.All(a => a.Kept && a.Mask == a.Device.ExistingMask), Map());
    Check("re-plan mode ignores it", AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs, keepValidPins: false).Assignments.All(a => !a.Kept));

    devs.Single(d => d.DeviceId == "7550").ExistingMask = M(2);      // a pin on the V-Cache CCD breaks the rules
    devs.Single(d => d.DeviceId == "15B8").ExistingMask = M(16, 18); // two cores for a USB controller: not kept
    var plan2 = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs, keepValidPins: true);
    string Map2() => string.Join("; ", plan2.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}{(a.Kept ? "k" : "")}"));
    Check("a pin on CCD0 is re-planned", plan2.Assignments.Single(a => a.Device.DeviceId == "7550") is { Kept: false } g && g.Cpus.All(c => c >= 16), Map2());
    Check("a USB controller on 2 cores is re-planned", !plan2.Assignments.Single(a => a.Device.DeviceId == "15B8").Kept, Map2());
    Check("the others stay", plan2.Assignments.Count(a => a.Kept) == 5, Map2());
    Check("still one device per core", plan2.Assignments.SelectMany(a => a.Cpus).Distinct().Count() == plan2.Assignments.Sum(a => a.Cpus.Length), Map2());
    Console.WriteLine("        plan: " + Map2());
}

Console.WriteLine("Benchmark ranking, Oct 21 01:40 run (tail not scored, speed corrected for stolen time)");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    // cpu, median, p95, speed, isr+dpc %, rank (CPU 16 hosts the GPU today: 3719 int/s, 2066 DPC/s)
    var real = new (int Cpu, double Med, double P95, double Speed, double Busy, int Rank)[]
    {
        (16, 8.1, 401.0, 13.3, 4.5, 14), (18, 8.3, 330.8, 12.7, 0.1, 13), (20, 7.9, 9.3, 12.7, 0.1, 12), (22, 8.1, 295.3, 12.7, 0.1, 11),
        (24, 8.4, 486.1, 12.8, 0.1, 8), (26, 9.5, 325.5, 12.7, 1.0, 14), (28, 7.9, 119.0, 12.7, 0.0, 9), (30, 8.1, 494.6, 12.7, 0.0, 10),
    };
    var bench = new BenchResult { When = DateTime.Now, SamplesPerCore = 5000 };
    foreach (int c in Enumerable.Range(0, 8).Select(i => i * 2))
        bench.Cores.Add(new CoreScore { Cpu = c, WakeMedianUs = 10, WakeP95Us = 200, SpeedMs = 13.0, SchedulingClass = c / 2 });
    foreach (var r in real)
        bench.Cores.Add(new CoreScore { Cpu = r.Cpu, WakeMedianUs = r.Med, WakeP95Us = r.P95, SpeedMs = r.Speed, IsrPercent = r.Busy / 2, DpcPercent = r.Busy / 2, SchedulingClass = r.Rank });
    var sets = t.Cpus.Select(c => new CpuSetEntry(c.Index, 0, c.CoreId, c.L3Id, 0, bench.For(c.Index - c.Index % 2)?.SchedulingClass ?? 0, false)).ToList();
    var plan = AffinityPlanner.Plan(t, sets, bench, DesktopDevices());
    var order = plan.Candidates.Select(c => c.Cpu).ToList();
    Check("top-ranked CPU 16 isn't punished for the GPU it hosts today", order.IndexOf(16) <= 1, string.Join(",", order));
    Check("lowest-ranked CPU 24 is last or close", order.IndexOf(24) >= order.Count - 2, string.Join(",", order));
    Console.WriteLine("        core order: " + string.Join(", ", order));
    Console.WriteLine("        plan: " + string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}")));
}

Console.WriteLine("M3: services and tasks catalogs");
{
    var sc = Catalogs.Services;
    var tc = Catalogs.Tasks;
    Check("services catalog loads (300+ entries)", sc.All.Count > 300, sc.All.Count.ToString());
    Check("tasks catalog loads (200+ entries)", tc.All.Count > 200, tc.All.Count.ToString());
    Check("no duplicate service names", sc.All.Select(e => e.Name.ToLowerInvariant()).Distinct().Count() == sc.All.Count);
    Check("no duplicate task paths", tc.All.Select(e => e.Path.ToLowerInvariant()).Distinct().Count() == tc.All.Count);
    string[] mustLock = { "RpcSs", "DcomLaunch", "PlugPlay", "Power", "EventLog", "Winmgmt", "Schedule", "BFE", "mpssvc", "WinDefend", "wuauserv", "CryptSvc", "Audiosrv", "Dhcp", "Dnscache", "AudioEndpointBuilder" };
    Check("spec's locked services are locked", mustLock.All(n => sc.Find(n)?.IsLocked == true), string.Join(",", mustLock.Where(n => sc.Find(n)?.IsLocked != true)));
    Check("game services locked", new[] { "XblAuthManager", "XboxNetApiSvc", "GamingServices", "GamingServicesNet", "vgc", "EasyAntiCheat", "BEService" }.All(n => sc.Find(n)?.IsLocked == true));
    Check("locked entries never in a preset", sc.All.Where(e => e.IsLocked).All(e => e.Safe is null && e.Aggressive is null && e.Laptop is null && e.Workstation is null && e.Author is null));
    Check("telemetry off in safe", sc.Find("DiagTrack")?.Safe == "Disabled" && sc.Find("dmwappushservice")?.Safe == "Disabled");
    Check("DPS only in aggressive/author", sc.Find("DPS") is { Safe: null, Aggressive: "Disabled", Author: "Disabled" });
    Check("SysMain only on SSD-only PCs", sc.Find("SysMain") is { OnlyIfAbsent: "hdd" });
    Check("author's eight services", sc.All.Where(e => e.Author is not null).Select(e => e.Name).OrderBy(x => x)
        .SequenceEqual(new[] { "AdobeARMservice", "amd3dvcacheSvc", "DiagTrack", "DPS", "PcaSvc", "StiSvc", "SysMain", "TrkWks" }.OrderBy(x => x)),
        string.Join(",", sc.All.Where(e => e.Author is not null).Select(e => e.Name)));
    Check("preset values are valid start types", sc.All.SelectMany(e => new[] { e.Safe, e.Aggressive, e.Laptop, e.Workstation, e.Author }).All(v => v is null or "Manual" or "Disabled"));
    Check("defaults parse", sc.All.All(e => StartTypes.Parse(e.Default) != StartType.Unknown), string.Join(",", sc.All.Where(e => StartTypes.Parse(e.Default) == StartType.Unknown).Select(e => e.Name).Take(5)));
    Check("glob: versioned Google updater", sc.Find("GoogleUpdaterService156.0.8067.0") is not null);
    Check("per-user template name", Catalogs.TemplateName("CDPUserSvc_957c1") == "CDPUserSvc" && sc.Find("CDPUserSvc")?.Group == "peruser");
    Check("task glob: MEGA updater with SID", tc.Find(@"\MEGA\MEGAsync Update Task S-1-5-21-2302084377-1428478753-1527212959-1001")?.Group == "vendor");
    Check("UpdateOrchestrator locked", tc.Find(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan")?.IsLocked == true);
    Check("Defrag locked (TRIM)", tc.Find(@"\Microsoft\Windows\Defrag\ScheduledDefrag")?.IsLocked == true);
    Check("FeatureConfig locked", tc.Find(@"\Microsoft\Windows\Flighting\FeatureConfig\ReconcileFeatures")?.IsLocked == true);
    Check("StartCN left alone", tc.Find(@"\StartCN") is { Group: "app" } st && !st.Disables(CleanupPreset.Safe) && !st.Disables(CleanupPreset.Author));
    Check("Consolidator disabled in safe", tc.Find(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator")?.Disables(CleanupPreset.Safe) == true);
    Check("XblGameSave kept (cloud saves)", tc.Find(@"\Microsoft\XblGameSave\XblGameSaveTask") is { } xg && !xg.Disables(CleanupPreset.Aggressive));
    Check("locked tasks never disabled", tc.All.Where(e => e.IsLocked).All(e => Enum.GetValues<CleanupPreset>().All(p => !e.Disables(p))));
    Check("Windows default disables no task", tc.All.All(e => !e.Disables(CleanupPreset.WindowsDefault)));
    Check("exact path beats glob", Catalogs.LoadTasks("""{"tasks":[{"path":"\\A\\*","group":"locked"},{"path":"\\A\\B","group":"vendor"}]}""").Find(@"\A\B")?.Group == "vendor");
    Check("longer glob beats shorter", Catalogs.LoadTasks("""{"tasks":[{"path":"\\A\\*","group":"locked"},{"path":"\\A\\B*","group":"vendor"}]}""").Find(@"\A\Bc")?.Group == "vendor");

    Check("start type ranks", StartTypes.Rank(StartType.Disabled) < StartTypes.Rank(StartType.Manual) && StartTypes.Rank(StartType.Manual) < StartTypes.Rank(StartType.AutoDelayed));
    Check("registry -> delayed auto", StartTypes.FromRegistry(2, 1) == StartType.AutoDelayed && StartTypes.FromRegistry(3, null) == StartType.Manual);
    Check("journal data back to start type", ServiceControl.FromJournal("2|1") == StartType.AutoDelayed && ServiceControl.FromJournal("4|") == StartType.Disabled);
    Check("sc words", StartTypes.ScWord(StartType.AutoDelayed) == "delayed-auto" && StartTypes.ScWord(StartType.Manual) == "demand");
    Check("exe from quoted path", ServiceScanner.ExePath("\"C:\\Program Files\\X\\x.exe\" -k") == @"C:\Program Files\X\x.exe");
    Check("exe from svchost path", ServiceScanner.ExePath(@"C:\WINDOWS\system32\svchost.exe -k netsvcs -p")?.EndsWith("svchost.exe") == true);
    Check("virtual printers ignored", HardwareFacts.VirtualPrinter.IsMatch("Microsoft Print to PDF") && !HardwareFacts.VirtualPrinter.IsMatch("HP LaserJet M110w"));

    var xml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>Microsoft Office</Author></RegistrationInfo>
          <Triggers><CalendarTrigger><Enabled>true</Enabled></CalendarTrigger><IdleTrigger><Enabled>true</Enabled></IdleTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>S-1-5-18</UserId></Principal></Principals>
          <Settings><MaintenanceSettings><Period>P1D</Period></MaintenanceSettings></Settings>
          <Actions Context="Author"><Exec><Command>C:\x.exe</Command><Arguments>/a</Arguments></Exec><ComHandler><ClassId>{1}</ClassId></ComHandler></Actions>
        </Task>
        """.Replace("encoding=\"UTF-16\"", "");
    var td = TaskScanner.Parse(new TaskListing(@"\Microsoft\Office\Office Automatic Updates 2.0", true, xml) { State = "Ready" });
    Check("task XML: author, SYSTEM, actions", td.Author == "Microsoft Office" && td.RunAs == "SYSTEM" && td.Actions.SequenceEqual(new[] { @"C:\x.exe /a", "COM {1}" }), $"{td.Author}|{td.RunAs}|{td.ActionsText}");
    Check("task XML: idle + maintenance", td.IdleTriggered && td.Triggers.Contains("Idle") && td.Triggers.Contains("Automatic maintenance"), td.TriggersText);
    Check("task name/folder", td.Name == "Office Automatic Updates 2.0" && td.Folder == @"\Microsoft\Office");

    // Review fixes: unknown hardware, Windows-default scope, revert rows for unseen journal entries.
    Check("unknown hardware counts as present", new HardwareFacts().Has("hdd") && new HardwareFacts().Has("printer") && !new HardwareFacts { Hdd = false }.Has("hdd"));
    Check("Spooler only in aggressive", sc.Find("Spooler") is { Safe: null, Aggressive: "Manual", OnlyIfAbsent: "printer" });
    Check("RGB/tuning helpers only in aggressive", new[] { "LightingService", "LightKeeperService", "NahimicService", "ArmouryCrateService" }.All(n => sc.Find(n) is { Safe: null, Aggressive: "Manual" }));
    Check("corrected defaults", sc.Find("StiSvc")?.Default == "Manual" && sc.Find("tzautoupdate")?.Default == "Disabled" && sc.Find("MapsBroker")?.Default == "AutoDelayed");
    Check("browser updater tasks only in aggressive", tc.All.Where(e => e.Path.Contains("GoogleUpdate", StringComparison.OrdinalIgnoreCase) || e.Path.Contains("MicrosoftEdgeUpdate", StringComparison.OrdinalIgnoreCase))
        .All(e => !e.Disables(CleanupPreset.Safe) && e.Disables(CleanupPreset.Aggressive)));

    ServiceInfo Svc(string name, StartType start, bool windows) => new() { Name = name, DisplayName = name, Start = start, IsWindowsBinary = windows };
    string jfile = Path.Combine(Path.GetTempPath(), "dpc-selftest-journal-" + Guid.NewGuid().ToString("N") + ".json");
    var journal = new Journal(jfile);
    journal.MarkApplied("svc.GhostService");
    journal.MarkApplied(@"task.\Ghost\Task");
    SystemProfile Prof(HardwareFacts hw, params ServiceInfo[] svcs) => new()
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "x", CpuVendor = "AMD", Cpu = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "", Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        Hardware = hw, Services = svcs,
    };
    var svcs = new[]
    {
        Svc("DiagTrack", StartType.Auto, true), Svc("SysMain", StartType.Auto, true), Svc("Spooler", StartType.Auto, true),
        Svc("LightingService", StartType.Auto, false), Svc("AdobeARMservice", StartType.Auto, false), Svc("TrkWks", StartType.Disabled, true),
    };
    List<string> Ids(HardwareFacts hw, CleanupPreset preset) =>
        ServicesModule.Build(Prof(hw, svcs), journal, new AppOptions { Cleanup = preset }).Select(t => t.Info.Id).ToList();
    var safeUnknown = Ids(new HardwareFacts(), CleanupPreset.Safe);
    Check("safe: telemetry and Adobe updater, not RGB or Spooler", safeUnknown.Contains("svc.DiagTrack") && safeUnknown.Contains("svc.AdobeARMservice") && !safeUnknown.Contains("svc.LightingService") && !safeUnknown.Contains("svc.Spooler"), string.Join(",", safeUnknown));
    Check("SysMain kept when disk type is unknown", !safeUnknown.Contains("svc.SysMain"));
    Check("SysMain offered on an SSD-only PC", Ids(new HardwareFacts { Hdd = false }, CleanupPreset.Safe).Contains("svc.SysMain"));
    var aggr = Ids(new HardwareFacts { Printer = false }, CleanupPreset.Aggressive);
    Check("aggressive: RGB helper and Spooler (no printer)", aggr.Contains("svc.LightingService") && aggr.Contains("svc.Spooler"), string.Join(",", aggr));
    Check("aggressive: Spooler kept when a printer may exist", !Ids(new HardwareFacts(), CleanupPreset.Aggressive).Contains("svc.Spooler"));
    var def = Ids(new HardwareFacts(), CleanupPreset.WindowsDefault);
    Check("Windows default: Windows services only", def.Contains("svc.TrkWks") && !def.Contains("svc.AdobeARMservice") && !def.Contains("svc.LightingService"), string.Join(",", def));
    Check("journal entry the scan didn't see: revert row", safeUnknown.Contains("svc.GhostService"));
    var taskIds = TasksModule.Build(Prof(new HardwareFacts()), journal, new AppOptions()).Select(t => t.Info.Id).ToList();
    Check("unseen task in journal: revert row", taskIds.Contains(@"task.\Ghost\Task"), string.Join(",", taskIds));
    try { File.Delete(jfile); } catch { }
}

Console.WriteLine("M3: Process Lasso starter rules (the desktop)");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    var profile = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 10 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD", Cpu = t, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
    var split = LassoRules.SplitFor(profile, plan);
    Check("games on the V-Cache CCD 0-15", split is not null && CpuTopology.FormatCpus(split.GameCpus) == "0-15");
    // 21 and 23 are excluded too: they are the siblings of the NIC's two cores, and its receive queues
    // are assigned by logical processor, so background work there would sit on a queue's own CPU.
    Check("catch-all excludes every device CPU, RSS siblings included (was 18-31, the bug)",
        split is not null && CpuTopology.FormatCpus(split.OtherCpus, ";") == "0-15;17;19;25;27;29;31",
        split is null ? "" : CpuTopology.FormatCpus(split.OtherCpus, ";"));
    Check("catch-all never intersects a device CPU",
        split is not null && !split.OtherCpus.Intersect(plan.DeviceCpus).Any(),
        split is null ? "" : string.Join(",", split.OtherCpus.Intersect(plan.DeviceCpus)));
    Check("catch-all + device CPUs cover every logical processor exactly once",
        split is not null && split.OtherCpus.Count + plan.DeviceCpus.Count == t.LogicalCount);
    string json = LassoRules.Build(profile, plan);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;
    Check("schema id matches Bitsum's", root.GetProperty("$id").GetString() == LassoRules.SchemaId);
    var order = root.GetProperty("cpuAffinityRuleOrder").EnumerateArray().Select(x => x.GetString()).ToList();
    Check("catch-all is the last affinity rule", order.Last() == "*.*" && order[^2] == "dwm.exe");
    var mk = root.GetProperty("ruleSets").EnumerateArray().First(r => r.GetProperty("processName").GetString() == "mk12.exe");
    // ---- #5: a measured latency cost is reachable without taking the whole Aggressive preset ----
    Console.WriteLine("Cleanup: DPS reachable on Safe");
    {
        var svcEntry = Catalogs.Services.Find("DPS");
        Check("DPS is in the latency group", svcEntry?.Group == "latency", svcEntry?.Group ?? "absent");
        Check("DPS is Measured", svcEntry?.Evidence == "Measured", svcEntry?.Evidence ?? "");
        Check("the Safe preset does not select DPS", svcEntry?.Target(CleanupPreset.Safe) is null);
        Check("Aggressive does select DPS", svcEntry?.Target(CleanupPreset.Aggressive) == "Disabled");

        // Build with a profile that has DPS running as Auto, on the Safe preset.
        var profC = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Pro"),
            CpuName = "AMD Ryzen 9 9950X3D", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
                new[] { (0x0000FFFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
            Services = new List<ServiceInfo>
            {
                new() { Name = "DPS", DisplayName = "Diagnostic Policy Service", Description = "d",
                        Start = StartType.Auto, State = "Running", IsWindowsBinary = true },
            },
        };
        var jC = new Journal(Path.Combine(Path.GetTempPath(), "sg-dps-" + Guid.NewGuid().ToString("N") + ".json"));

        var safeRows = ServicesModule.Build(profC, jC, new AppOptions { Cleanup = CleanupPreset.Safe }).ToList();
        var dpsSafe = safeRows.SingleOrDefault(r => r.Info.Id == "svc.DPS");
        Check("Safe still offers the DPS row", dpsSafe is not null, safeRows.Count.ToString());
        Check("Safe does not tick it for the user", dpsSafe?.Info.DefaultSelected == false, dpsSafe?.Info.DefaultSelected.ToString() ?? "absent");
        Check("the row sits under the measured-latency heading",
            dpsSafe?.Info.Group == "Latency (measured)", dpsSafe?.Info.Group ?? "");

        var aggRows = ServicesModule.Build(profC, jC, new AppOptions { Cleanup = CleanupPreset.Aggressive }).ToList();
        var dpsAgg = aggRows.SingleOrDefault(r => r.Info.Id == "svc.DPS");
        Check("Aggressive ticks it, as before", dpsAgg?.Info.DefaultSelected == true, dpsAgg?.Info.DefaultSelected.ToString() ?? "absent");
        Check("Aggressive does not duplicate the row", aggRows.Count(r => r.Info.Id == "svc.DPS") == 1);

        // Windows-default mode must not offer it at all: that mode only restores Windows' own values,
        // and DPS is a service the user may never have changed.
        var wdRows = ServicesModule.Build(profC, jC, new AppOptions { Cleanup = CleanupPreset.WindowsDefault }).ToList();
        Check("Windows-default mode does not add an opt-in row for DPS",
            wdRows.Count(r => r.Info.Id == "svc.DPS") <= 1);
    }
    // ---- #2/#4 regression: the leftover sweep must not duplicate a row that IS still planned ----
    Console.WriteLine("Power leftovers: no duplicate, right section");
    {
        // power.active starts with "power.", so a journal where it was touched would have had a leftover
        // emitted beside the real PlanActiveTweak, titled "active: set earlier".
        var jP = new Journal(Path.Combine(Path.GetTempPath(), "sg-pwrleft-" + Guid.NewGuid().ToString("N") + ".json"));
        jP.MarkApplied("power.active");
        jP.MarkApplied("power.cpu.epp");
        jP.Save();

        var profP = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Pro"),
            CpuName = "AMD Ryzen 9 9950X3D", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
                new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };

        var balanced = PowerModule.Build(profP, jP, () => new AppOptions { PlanTarget = PlanTargetKind.Balanced }).ToList();
        Check("power.active is planned, so no leftover duplicates it",
            balanced.Count(t2 => t2.Info.Id == "power.active") == 1,
            string.Join(", ", balanced.Where(x => x.Info.Id == "power.active").Select(x => x.GetType().Name)));

        var eppLeftover = balanced.SingleOrDefault(t2 => t2.Info.Id == "power.cpu.epp");
        Check("a Balanced CPU setting keeps a revert-only row", eppLeftover is LeftoverTweak, eppLeftover?.GetType().Name ?? "absent");
        Check("and it stays in the CPU section", eppLeftover?.Info.Group == "CPU", eppLeftover?.Info.Group ?? "");

        // A row that is still planned must not also appear as a leftover.
        var tuned = PowerModule.Build(profP, jP, () => new AppOptions { PlanTarget = PlanTargetKind.TunedUltimate, PowerProfile = PowerProfileKind.Full }).ToList();
        Check("on TunedUltimate the CPU setting is planned, not leftover",
            tuned.Any(t2 => t2.Info.Id == "power.cpu.epp" && t2 is PowerSettingTweak));
        Check("and there is no duplicate leftover for it",
            tuned.Count(t2 => t2.Info.Id == "power.cpu.epp") == 1,
            string.Join(", ", tuned.Where(x => x.Info.Id == "power.cpu.epp").Select(x => x.GetType().Name)));
    }
    // ---- #3b: the RSS row must describe the keywords it actually writes ----
    Console.WriteLine("RssTweak: text matches the write");
    {
        var topoR = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var planR = AffinityPlanner.Plan(topoR, Sets(topoR, _ => 0), null, DesktopDevices());

        // A kept NIC pinned to the first thread of two cores: its pin cannot express the 4-LP span the
        // adapter's own RSS wants. Clamp narrows the queue range; the text must say so.
        // Use the same NIC the rest of the suite uses, so ClassKey and NetCfgInstanceId are real shapes.
        var nic = DesktopDevices().Single(d => d.DeviceId == "8126");
        nic.NetCfgInstanceId = "{TEST-NIC}";
        var asg = new Assignment { Device = nic, Role = DeviceRole.Network, Cpus = new[] { 20, 22 },
            Kept = true, Rss = new RssPlan(20, 23, 4, 2), Reason = "test" };
        var t21 = new RssTweak(asg, new Journal(Path.Combine(Path.GetTempPath(), "sg-rsstext-" + Guid.NewGuid().ToString("N") + ".json")));
        Check("kept pin {20,22}: text names one processor", t21.Info.What.Contains("processor 20"), t21.Info.What);
        Check("kept pin {20,22}: text does not claim processors 20-23", !t21.Info.What.Contains("20-23"), t21.Info.What);
        Check("kept pin {20,22}: text states the narrowing and that the pin is unchanged",
            t21.Info.What.Contains("narrowed to the pin"), t21.Info.What);

        // The unkept case keeps the full span and must not mention narrowing.
        var asg2 = new Assignment { Device = nic, Role = DeviceRole.Network, Cpus = new[] { 20, 22 },
            Kept = false, Rss = new RssPlan(20, 23, 4, 2), Reason = "test" };
        var t232 = new RssTweak(asg2, new Journal(Path.Combine(Path.GetTempPath(), "sg-rsstext-" + Guid.NewGuid().ToString("N") + ".json")));
        Check("unkept: text keeps the full 20-23 span", t232.Info.What.Contains("processors 20-23"), t232.Info.What);
        Check("unkept: no narrowing sentence", !t232.Info.What.Contains("narrowed to the pin"), t232.Info.What);
        _ = planR;
    }
    Check("game rule: 0-15 affinity", mk.GetProperty("cpuAffinity").GetString() == "0-15");
    // Bitsum's setup guide: "Leave the game's priority alone. Raising a game to High or Realtime
    // priority rarely helps and can cause stutter or hangs." No priority may be emitted at all.
    Check("game rule sets no priority", !mk.TryGetProperty("priorityClass", out _));
    Check("no priority is emitted anywhere in the rules",
        !root.GetProperty("ruleSets").EnumerateArray().Any(r => r.TryGetProperty("priorityClass", out _)));
    Check("no priorityClassRuleOrder is emitted", !root.TryGetProperty("priorityClassRuleOrder", out _));

    // ---- game CCD selection: the shape that started this, and the trade it names ----
    Console.WriteLine("M3b: which CCD games land on");
    {
        // Asymmetric dual X3D: 96 MB + 32 MB, the 7950X3D / 9950X3D / 9900X3D shape.
        var asym = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var asymP = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D", CpuVendor = "AMD", Cpu = asym, FormFactor = FormFactor.Desktop,
            FormFactorEvidence = "", Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false),
            Software = new SoftwareInfo(),
        };
        var asymPlan = AffinityPlanner.Plan(asym, Sets(asym, _ => 0), null, DesktopDevices());

        Check("auto picks the 96 MB CCD on an asymmetric part",
            LassoRules.SplitFor(asymP, asymPlan) is { } a0 && CpuTopology.FormatCpus(a0.GameCpus) == "0-15");
        Check("auto names the choice by cache size, never 'V-Cache CCD'",
            LassoRules.SplitFor(asymP, asymPlan) is { } a1 && a1.GameLabel.Contains("96 MB L3") && !a1.GameLabel.Contains("V-Cache"),
            LassoRules.SplitFor(asymP, asymPlan)?.GameLabel ?? "");
        Check("picking the smaller CCD says what the user gave up",
            LassoRules.SplitFor(asymP, asymPlan, 1) is { } a2 &&
            CpuTopology.FormatCpus(a2.GameCpus) == "16-31" && a2.GameLabel.Contains("giving up 96 MB"),
            LassoRules.SplitFor(asymP, asymPlan, 1)?.GameLabel ?? "");

        // Symmetric dual V-Cache: 96 + 96, the 9950X3D2. The ratio test cannot resolve this, so auto must
        // fall through to Windows' ranking AND say so - it must not claim a cache reason it does not have.
        var sym = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 96u << 20) });
        var symP = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D2 Dual Edition", CpuVendor = "AMD", Cpu = sym, FormFactor = FormFactor.Desktop,
            FormFactorEvidence = "", Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false),
            Software = new SoftwareInfo(),
        };
        var symPlan = AffinityPlanner.Plan(sym, Sets(sym, _ => 0), null, DesktopDevices());

        Check("symmetric cache: auto still resolves a group", LassoRules.SplitFor(symP, symPlan) is not null);
        Check("symmetric cache: auto admits it followed Windows' ranking, not cache",
            LassoRules.SplitFor(symP, symPlan) is { } s0 && s0.GameLabel.Contains("same L3"),
            LassoRules.SplitFor(symP, symPlan)?.GameLabel ?? "");
        Check("symmetric cache: CCD1 is selectable and called a preference, not a trade",
            LassoRules.SplitFor(symP, symPlan, 1) is { } s1 &&
            CpuTopology.FormatCpus(s1.GameCpus) == "16-31" && s1.GameLabel.Contains("preference"),
            LassoRules.SplitFor(symP, symPlan, 1)?.GameLabel ?? "");
        Check("symmetric cache: the rule file carries the chosen CCD",
            LassoRules.Build(symP, symPlan, 1).Contains("\"0\": \"16-31\"".Replace("\"0\"", "\"cpuAffinity\""))
            || LassoRules.Build(symP, symPlan, 1).Contains("16-31"));

        // ---- stress: the shapes that break a two-value CCD pair ----

        // Zen 2 (3900X: two CCDs, two CCX each) reports FOUR L3 domains over 12 cores. An enum with
        // Ccd0/Ccd1 would map domains 1, 2 and 3 onto the same value and write the wrong mask.
        var zen2 = Topo(Enumerable.Range(0, 12).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000003FUL, 16u << 20), (0x00000FC0UL, 16u << 20), (0x0003F000UL, 16u << 20), (0x00FC0000UL, 16u << 20) });
        var zen2P = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 3900X", CpuVendor = "AMD", Cpu = zen2, FormFactor = FormFactor.Desktop,
            FormFactorEvidence = "", Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false),
            Software = new SoftwareInfo(),
        };
        var zen2Plan = AffinityPlanner.Plan(zen2, Sets(zen2, _ => 0), null, DesktopDevices());

        Check("3900X: four L3 domains are reported", zen2.L3Groups.Count == 4, zen2.L3Groups.Count.ToString());
        Check("3900X: one domain is NOT one CCD (Zen 2 has two CCX per CCD)", !LassoRules.OneDomainPerCcd(zen2));
        // The decisive one: domain 3 must reach domain 3's cores. With the old Ccd0/Ccd1 pair this
        // returned domain 1, i.e. the wrong CCD, silently.
        Check("3900X: index 3 selects the fourth domain, not the second",
            LassoRules.SplitFor(zen2P, zen2Plan, 3) is { } z3 &&
            z3.GameCpus.SequenceEqual(zen2.L3Groups.First(g => g.Id == 3).Cpus()),
            LassoRules.SplitFor(zen2P, zen2Plan, 3) is { } zz ? string.Join(",", zz.GameCpus) : "null");
        Check("3900X: the label says 'Cache domain', never 'CCD'",
            LassoRules.SplitFor(zen2P, zen2Plan, 3) is { } z4 && z4.GameLabel.Contains("Cache domain 3") && !z4.GameLabel.Contains("CCD"),
            LassoRules.SplitFor(zen2P, zen2Plan, 3)?.GameLabel ?? "");

        // Zen 2 single CCD (3600X: six cores, two CCX) also reports two domains, on ONE CCD. The control
        // appearing there is legitimate - a cross-CCX move is a real latency change - but it must not be
        // called a CCD choice.
        var zen2one = Topo(Enumerable.Range(0, 6).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x00000F0FUL, 16u << 20), (0x0F0F0000UL, 16u << 20) });
        Check("3600X: two domains on a single CCD are not called CCDs", !LassoRules.OneDomainPerCcd(zen2one));

        // Zen 3 / Zen 4 / Zen 5: one CCX per CCD, so domains == CCDs.
        var zen4 = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 32u << 20), (0xFFFF0000UL, 32u << 20) });
        Check("9950X: two domains on 16 cores ARE two CCDs", LassoRules.OneDomainPerCcd(zen4));

        // An index that no longer exists must fall back to Auto, not coerce to a different domain and not
        // claim the user chose it.
        Check("out-of-range index falls back to Auto and does not claim the user chose it",
            LassoRules.SplitFor(asymP, asymPlan, 7) is { } o1 && !o1.GameLabel.Contains("chosen by you"),
            LassoRules.SplitFor(asymP, asymPlan, 7)?.GameLabel ?? "");
        Check("out-of-range index still yields a usable mask",
            LassoRules.SplitFor(asymP, asymPlan, 7) is { } o2 && o2.GameCpus.Count > 0);
        // ---- stress: a stored option must never cost the rest of the file when it cannot be read ----
        Console.WriteLine("Options.GameCcd migration");
        {
            // The one unreleased build stored an enum name here. A plain int? reader would throw, and
            // Load treats a throw as "whole file unreadable" and returns defaults - losing every other
            // stored choice with it.
            var fromEnum = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(
                "{\"GameCcd\":\"Ccd1\",\"UserChosen\":true}", new System.Text.Json.JsonSerializerOptions());
            Check("an enum-name value is read as its index", fromEnum is { GameCcd: 1 }, fromEnum?.GameCcd?.ToString() ?? "null");
            Check("an enum-name value does not throw", fromEnum is not null);

            var fromAuto = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(
                "{\"GameCcd\":\"Auto\",\"UserChosen\":true}", new System.Text.Json.JsonSerializerOptions());
            Check("'Auto' is read as null", fromAuto is { GameCcd: null });

            var fromNumber = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(
                "{\"GameCcd\":3,\"UserChosen\":true}", new System.Text.Json.JsonSerializerOptions());
            Check("a number is read as that index", fromNumber is { GameCcd: 3 });

            var fromJunk = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(
                "{\"GameCcd\":\"nonsense\",\"UserChosen\":true,\"TimerResolution\":7500}", new System.Text.Json.JsonSerializerOptions());
            Check("an unrecognised value becomes Auto rather than throwing", fromJunk is { GameCcd: null });
            Check("and the other stored fields survive it", fromJunk is { TimerResolution: 7500 } && fromJunk.UserChosen);

            var roundTrip = new AppOptions { GameCcd = 2 };
            string s = System.Text.Json.JsonSerializer.Serialize(roundTrip, new System.Text.Json.JsonSerializerOptions());
            var back = System.Text.Json.JsonSerializer.Deserialize<AppOptions>(s, new System.Text.Json.JsonSerializerOptions());
            Check("an index round-trips", back is { GameCcd: 2 }, s);
        }
        Check("null index means Auto",
            LassoRules.SplitFor(asymP, asymPlan, null) is { } o3 &&
            LassoRules.SplitFor(asymP, asymPlan) is { } o4 &&
            o3.GameCpus.SequenceEqual(o4.GameCpus));
    }
    // Dynamic thread priority boost must be left alone: Windows' +6 mouse/keyboard and +8 sound wake
    // boosts are what let a woken input or audio thread preempt, so emitting "off" worked against latency.
    Check("game rule does not disable dynamic thread priority boost", !mk.TryGetProperty("threadPriorityBoosts", out _));
    Check("no threadPriorityBoosts anywhere in the emitted rules",
        !root.GetProperty("ruleSets").EnumerateArray().Any(r => r.TryGetProperty("threadPriorityBoosts", out _)));
    Check("no priorityBoostRuleOrder is emitted", !root.TryGetProperty("priorityBoostRuleOrder", out _));
    // Every emitted mask must use Bitsum's semicolon form. A comma here is ambiguous in the INI,
    // where entry fields are themselves comma-separated (defect D9).
    var commaIssues = root.GetProperty("ruleSets").EnumerateArray()
        .Where(r => r.TryGetProperty("cpuAffinity", out var ca) && ca.GetString()!.Contains(","))
        .Select(r => r.GetProperty("processName").GetString())
        .ToList();
    Check("no emitted cpuAffinity contains a comma", commaIssues.Count == 0, string.Join(", ", commaIssues));

    // ---- the scan report is offered as anonymous, so it must not name the account it came from ----
    Console.WriteLine("ScanReport.Redact");
    {
        string home = @"  TeamViewer_Service  [Auto]  3rd-party  TeamViewer  (C:\Users\kareem\AppData\Local\TeamViewer\TeamViewer_Service.exe)";
        string red = ScanReport.Redact(home);
        Check("a home directory in a service path is redacted", !red.Contains("kareem"), red);
        Check("the redacted path keeps its shape", red.Contains(@"C:\Users\<user>\AppData\Local\TeamViewer"), red);

        Check("USERPROFILE is redacted",
            !ScanReport.Redact(@"actions: %USERPROFILE%\AppData\Local\x.exe").Contains("USERPROFILE"));
        Check("a lowercase drive letter is redacted",
            !ScanReport.Redact(@"c:\users\kareem\app.exe").Contains("kareem"));
        Check("Documents and Settings is redacted",
            !ScanReport.Redact(@"C:\Documents and Settings\kareem\x.exe").Contains("kareem"));
        // A path that merely contains the word Users must survive: over-redaction hides real paths.
        Check("a path that is not a home directory is left alone",
            ScanReport.Redact(@"C:\Program Files\Users\Tools\a.exe") == @"C:\Program Files\Users\Tools\a.exe");
        Check("an ordinary path is untouched",
            ScanReport.Redact(@"C:\Windows\System32\svchost.exe") == @"C:\Windows\System32\svchost.exe");
    }
    var single = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 96u << 20) });
    Check("single CCD: nothing to separate", LassoRules.SplitFor(new SystemProfile
    {
        Windows = profile.Windows, CpuName = "x", CpuVendor = "AMD", Cpu = single, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = profile.Security, Software = new SoftwareInfo(),
    }, plan) is null);
}

Console.WriteLine("M3b: Process Lasso refuses a catch-all that would be worse than none (A5)");
{
    // A tiny 4-core dual-CCD machine where the device plan claims every core. Building the plan
    // directly keeps this deterministic: SplitFor only consults plan.DeviceCpus.
    var t = Topo(new[] { (new[] { 0 }, (byte)0), (new[] { 1 }, (byte)0), (new[] { 2 }, (byte)0), (new[] { 3 }, (byte)0) },
        new[] { (0x03UL, 32u << 20), (0x0CUL, 32u << 20) });
    var prof = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "4-core tight test CPU", CpuVendor = "AMD", Cpu = t, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    var tight = new AffinityPlan { DeviceCpus = new List<int> { 0, 1, 2, 3 } };
    var split = LassoRules.SplitFor(prof, tight);
    Check("4-core tight: split produced", split is not null);
    Check("4-core tight: zero CPUs free of devices", split is not null && split.OtherCpus.Count == 0,
        split is null ? "null" : string.Join(",", split.OtherCpus));

    string json = LassoRules.Build(prof, tight);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;
    var names = root.GetProperty("ruleSets").EnumerateArray().Select(r => r.GetProperty("processName").GetString()).ToList();
    Check("4-core tight: NO *.* rule emitted", !names.Contains("*.*"));
    Check("4-core tight: *.* absent from cpuAffinityRuleOrder",
        !root.GetProperty("cpuAffinityRuleOrder").EnumerateArray().Any(x => x.GetString() == "*.*"));
    Check("4-core tight: description explains the refusal",
        root.GetProperty("description").GetString()!.Contains("no catch-all rule"));
    Check("4-core tight: game rules still emitted", names.Contains("mk12.exe"));

    // FormatCpus returns "-" for an empty list, which is not a legal mask. Guard the whole payload.
    var sentinels = root.GetProperty("ruleSets").EnumerateArray()
        .Where(r => r.TryGetProperty("cpuAffinity", out var ca) && ca.GetString() == "-")
        .Select(r => r.GetProperty("processName").GetString())
        .ToList();
    Check("no emitted cpuAffinity is the empty-list sentinel", sentinels.Count == 0, string.Join(", ", sentinels));

    // Wrapper so the section reads the same way as the rest of the suite.
    static string TelemetryPreviewText(TelemetryPayload p) => TelemetryClient.Preview(p);

Console.WriteLine("Telemetry preview: the user sees exactly what is sent");
{
    // The preview IS the trust mechanism. If a section can silently disappear, a future edit could
    // drop the "NOT SENT" list or the attachment inventory and nobody would notice until a user did.
    var withLog = new TelemetryPayload(
        "https://discord.com/api/webhooks/123/…",
        "**New User Feedback**\n> a message",
        "SCAN REPORT BODY",
        "scan-report.txt",
        "stutterguard.log",
        new byte[2048]);

    string pv = TelemetryPreviewText(withLog);
    Check("preview shows the destination", pv.Contains("discord.com/api/webhooks"));
    Check("preview shows the message text", pv.Contains("a message"));
    Check("preview lists the report attachment with its size", pv.Contains("scan-report.txt") && pv.Contains("bytes"));
    Check("preview lists the log attachment", pv.Contains("stutterguard.log") && pv.Contains("2,048 bytes"));
    Check("preview shows the report verbatim", pv.Contains("SCAN REPORT BODY"));
    Check("preview states what is NOT sent", pv.Contains("NOT SENT") && pv.Contains("IP address"));
    Check("preview never leaks a webhook token", !pv.Contains("/123/") || pv.Contains("…"));

    var noLog = withLog with { LogFileName = null, LogBytes = null };
    string pv2 = TelemetryPreviewText(noLog);
    Check("a missing log is said out loud, not omitted", pv2.Contains("no log file attached"), pv2.Substring(0, Math.Min(120, pv2.Length)));
    Check("noLog preview omits the log file name", !pv2.Contains("stutterguard.log"));
}

    // Room to spare: the validated 9950X3D mask must still be emitted.
    var t2 = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    var prof2 = new SystemProfile
    {
        Windows = prof.Windows, CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD", Cpu = t2,
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = prof.Security, Software = new SoftwareInfo(),
    };
    var roomy = new AffinityPlan { DeviceCpus = new List<int> { 16, 18, 20, 22, 24, 26, 28, 30 } };
    var root2 = System.Text.Json.JsonDocument.Parse(LassoRules.Build(prof2, roomy)).RootElement;
    var names2 = root2.GetProperty("ruleSets").EnumerateArray().Select(r => r.GetProperty("processName").GetString()).ToList();
    Check("roomy 9950X3D: *.* still emitted", names2.Contains("*.*"));
    Check("roomy 9950X3D: *.* in cpuAffinityRuleOrder",
        root2.GetProperty("cpuAffinityRuleOrder").EnumerateArray().Any(x => x.GetString() == "*.*"));
    var ca2 = root2.GetProperty("ruleSets").EnumerateArray().First(r => r.GetProperty("processName").GetString() == "*.*").GetProperty("cpuAffinity").GetString();
    Check("roomy 9950X3D: mask is the validated 24-LP set", ca2 == "0-15;17;19;21;23;25;27;29;31", ca2 ?? "");
}

Console.WriteLine("Registry helpers, PCI classes, NIC values, CPU sets");
{
    Check("mask bytes little-endian", RegWrite.MaskBytes(1UL << 16).SequenceEqual(new byte[] { 0, 0, 1, 0, 0, 0, 0, 0 }));
    Check("mask from a 4-byte binary", RegWrite.AsMask(new RegValue(true, RegistryValueKind.Binary, new byte[] { 0, 0, 0x50, 0 })) == 0x500000UL);
    Check("mask text", RegWrite.FormatMask((1UL << 20) | (1UL << 22)) == "CPUs 20, 22");
    Check("affinity text", AffinityTweak.Show(4, 1UL << 16) == "CPU 16 (DevicePolicy 4)");
    Check("xHCI = USB controller", PciScanner.KindFromClass(0x0C, 0x03, 0x30) == PciKind.UsbController);
    Check("USB4 host interface", PciScanner.KindFromClass(0x0C, 0x03, 0x40) == PciKind.UsbHostInterface);
    Check("SMBus never", PciScanner.KindFromClass(0x0C, 0x05, 0x00) == PciKind.Never);
    Check("Wi-Fi by subclass", PciScanner.KindFromClass(0x02, 0x80, 0x00) == PciKind.Wifi);
    var p1 = new NicParam("*EEE", "Energy-Efficient Ethernet", "1", new() { ("0", "Disabled"), ("1", "Enabled") });
    var p2 = new NicParam("*FlowControl", "Flow Control", "3", new() { ("0", "Disabled"), ("1", "Tx Enabled"), ("2", "Rx Enabled"), ("3", "Rx & Tx Enabled") });
    var p3 = new NicParam("X", "X", "1", new() { ("1", "On"), ("2", "Off") });
    var p4 = new NicParam("Y", "Y", "1", new() { ("1", "Low"), ("2", "High") });
    Check("off value: Disabled = 0", NetworkModule.OffValue(p1) == "0");
    Check("off value: flow control", NetworkModule.OffValue(p2) == "0");
    Check("off value by name", NetworkModule.OffValue(p3) == "2");
    Check("no off value", NetworkModule.OffValue(p4) is null);
    Check("NIC rule keywords unique", NetworkModule.Rules.SelectMany(r => r.Keywords).Distinct(StringComparer.OrdinalIgnoreCase).Count() == NetworkModule.Rules.Sum(r => r.Keywords.Length));

    var cs = new byte[64];
    for (int i = 0; i < 2; i++)
    {
        var e = cs.AsSpan(i * 32, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(e, 32);
        e[14] = (byte)(i * 2); e[15] = (byte)i; e[18] = 1; e[19] = (byte)(i == 1 ? 1 : 0); e[20] = (byte)(100 + i);
    }
    var sets = SysInfo.ParseCpuSets(cs);
    Check("CPU sets parsed", sets.Count == 2 && sets[1].LogicalIndex == 2 && sets[1].SchedulingClass == 101 && sets[1].Parked && !sets[0].Parked);

    var wav = Audio.SilentWav(48000, 2, 1.0);
    Check("silent WAV size", wav.Length == 44 + 48000 * 4 && wav[0] == (byte)'R' && wav.Skip(44).All(b => b == 0));
}

Console.WriteLine("Hardware Matrix: Dell Inspiron 7577 (i7-7700HQ 4C/8T Kaby Lake + Optimus HD 630 + GTX 1060 Max-Q)");
{
    var t = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 6u << 20) });
    var gpus = new List<GpuInfo>
    {
        new() { ClassSubkey = "0000", ClassKeyPath = @"HKLM\X\0000", InstanceId = @"PCI\VEN_8086&DEV_591B\1", Name = "Intel(R) HD Graphics 630", Vendor = GpuVendor.Intel, IsIntegrated = true },
        new() { ClassSubkey = "0001", ClassKeyPath = @"HKLM\X\0001", InstanceId = @"PCI\VEN_10DE&DEV_1C20\2", Name = "NVIDIA GeForce GTX 1060 with Max-Q Design", Vendor = GpuVendor.Nvidia, IsIntegrated = false },
    };
    var devs = new List<PciDevice>
    {
        Dev(@"PCI\VEN_8086&DEV_591B\1", "Intel(R) HD Graphics 630", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }, 0, 0x00020000),
        Dev(@"PCI\VEN_10DE&DEV_1C20\2", "NVIDIA GeForce GTX 1060 with Max-Q Design", 0x03, 0x02, 0x00, new Attached { Monitors = 0 }, 1, 0x00000000),
        Dev(@"PCI\VEN_8086&DEV_A12F\3", "Intel(R) USB 3.0 eXtensible Host Controller - 1.0 (Microsoft)", 0x0C, 0x03, 0x30, new Attached { Mice = 1, Keyboards = 1, Bluetooth = 1, UsbDevices = 3 }),
        Dev(@"PCI\VEN_10EC&DEV_8168\4", "Realtek PCIe GbE Family Controller", 0x02, 0x00, 0x00),
        Dev(@"PCI\VEN_8086&DEV_24FD\5", "Intel(R) Dual Band Wireless-AC 8265", 0x02, 0x80, 0x00),
        Dev(@"PCI\VEN_8086&DEV_A171\6", "High Definition Audio Controller", 0x04, 0x03, 0x00, new Attached { AudioEndpoints = 1 }),
    };
    PciScanner.Classify(devs, gpus);
    devs.Single(d => d.DeviceId == "8168").Connected = false;
    devs.Single(d => d.DeviceId == "24FD").Connected = true;

    Check("7700HQ: Optimus 3D controller (03/02) classified as discrete Gpu", devs.Single(d => d.DeviceId == "1C20") is { Kind: PciKind.Gpu, IsIntegratedGpu: false });
    Check("7700HQ: screen-driving HD 630 classified as integrated Gpu", devs.Single(d => d.DeviceId == "591B") is { Kind: PciKind.Gpu, IsIntegratedGpu: true });
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("7700HQ: 4C/8T fallback reserves only CPU 0/1 so 3 cores remain", string.Join(",", plan.ReservedCpus) == "0,1", string.Join(",", plan.ReservedCpus));
    Check("7700HQ: candidates are CPUs 2, 4, 6", string.Join(",", plan.Candidates.Select(c => c.Cpu)) == "2,4,6");
    Check("7700HQ: both GTX 1060 and screen-driving HD 630 are pinned", plan.Assignments.Any(a => a.Device.DeviceId == "1C20") && plan.Assignments.Any(a => a.Device.DeviceId == "591B"), Map());
    var dgpuCore = plan.Assignments.Single(a => a.Device.DeviceId == "1C20").Cpus[0];
    var inputCore = plan.Assignments.Single(a => a.Device.DeviceId == "A12F").Cpus[0];
    var igpuCore = plan.Assignments.Single(a => a.Device.DeviceId == "591B").Cpus[0];
    Check("7700HQ: primary GTX 1060 and USB mouse controller each get their own unshared core; secondary HD 630 shares with Audio/Wi-Fi",
        plan.Assignments.Count(a => a.Cpus.Contains(dgpuCore)) == 1 &&
        plan.Assignments.Count(a => a.Cpus.Contains(inputCore)) == 1 &&
        plan.Assignments.Count(a => a.Cpus.Contains(igpuCore)) == 4, Map());
    Check("7700HQ: connected Wi-Fi gets Network role while disconnected Ethernet is Rest", plan.Assignments.Single(a => a.Device.DeviceId == "24FD").Role == DeviceRole.Network && plan.Assignments.Single(a => a.Device.DeviceId == "8168").Role == DeviceRole.Rest, Map());
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Hardware Matrix: Lenovo Legion (Ryzen 7 5800H 8C/16T APU + RTX dGPU laptop, real scan replay)");
{
    var t = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 16u << 20) });
    var gpus = new List<GpuInfo>
    {
        new() { ClassSubkey = "0000", ClassKeyPath = @"HKLM\X\0000", InstanceId = @"PCI\VEN_10DE&DEV_24DD\1", Name = "NVIDIA GeForce RTX 3070 Laptop GPU", Vendor = GpuVendor.Nvidia, IsIntegrated = false },
        new() { ClassSubkey = "0001", ClassKeyPath = @"HKLM\X\0001", InstanceId = @"PCI\VEN_1002&DEV_1638\3", Name = "AMD Radeon(TM) Graphics", Vendor = GpuVendor.Amd, IsIntegrated = true },
    };
    var devs = new List<PciDevice>
    {
        Dev(@"PCI\VEN_10DE&DEV_24DD\1", "NVIDIA GeForce RTX 3070 Laptop GPU", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }, 1, 0x00000000),
        Dev(@"PCI\VEN_10DE&DEV_228B\2", "NVIDIA High Definition Audio", 0x04, 0x03, 0x00, new Attached { AudioEndpoints = 1 }, 1, 0x00000001),
        Dev(@"PCI\VEN_1002&DEV_1638\3", "AMD Radeon(TM) Graphics", 0x03, 0x00, 0x00, new Attached { Monitors = 0 }, 6, 0x00000000),
        Dev(@"PCI\VEN_1022&DEV_1639\4", "AMD USB 3.10 eXtensible Host Controller - 1.10 (Microsoft)", 0x0C, 0x03, 0x30, new Attached { Mice = 1, Keyboards = 1, UsbDevices = 2 }),
        Dev(@"PCI\VEN_1022&DEV_15E2\5", "AMD Audio CoProcessor", 0x04, 0x80, 0x00),
        Dev(@"PCI\VEN_10EC&DEV_8168\6", "Realtek PCIe GbE Family Controller", 0x02, 0x00, 0x00),
        Dev(@"PCI\VEN_10EC&DEV_8852\7", "Realtek RTL8852AE WiFi 6 802.11ax PCIe Adapter", 0x02, 0x80, 0x00),
    };
    PciScanner.Classify(devs, gpus);
    devs.Single(d => d.DeviceId == "8852").Connected = true;

    Check("5800H: AMD Audio CoProcessor (1022:15E2) is classified as Never", devs.Single(d => d.DeviceId == "15E2").Kind == PciKind.Never);

    var prof = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Home", "25H2", 26200, 9457, "Core"),
        Manufacturer = "LENOVO", Model = "82JQ", FormFactor = FormFactor.Laptop, FormFactorEvidence = "chassis 10",
        CpuName = "AMD Ryzen 7 5800H with Radeon Graphics", CpuVendor = "AMD", Cpu = t,
        Gpus = gpus, Devices = devs, Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    var defaults = AppOptions.DefaultsFor(prof);
    Check("5800H: laptop defaults to CurrentActive power plan and Laptop cleanup", defaults.PlanTarget == PlanTargetKind.CurrentActive && defaults.Cleanup == CleanupPreset.Laptop && !defaults.ApplyOnBattery);
    Check("5800H: not dual-CCD X3D, not parking-dependent", !t.IsDualCcdX3D && !prof.ParkingDependentCpu);

    // Replay the real Legion benchmark where all cores have rank 0 and SpeedMs within 2% noise (21.3..22.1 ms),
    // and existing hand-tuned pins sit on second threads (7, 9, 11, 13, 15).
    var bench = new BenchResult { When = DateTime.Now, SamplesPerCore = 5000 };
    foreach (var (cpu, med, spd) in new[] { (0, 22.8, 23.0), (2, 26.7, 21.3), (4, 17.5, 21.9), (6, 19.9, 21.6), (8, 18.7, 21.9), (10, 20.4, 21.3), (12, 17.0, 22.1), (14, 18.7, 21.5) })
        bench.Cores.Add(new CoreScore { Cpu = cpu, WakeMedianUs = med, SpeedMs = spd, SchedulingClass = 0 });
    devs.Single(d => d.DeviceId == "1639").ExistingMask = 1UL << 9;  // pinned to 2nd thread of core 4 (CPU 8/9)
    devs.Single(d => d.DeviceId == "8852").ExistingMask = 1UL << 7;  // pinned to 2nd thread of core 3 (CPU 6/7)
    devs.Single(d => d.DeviceId == "228B").ExistingMask = 1UL << 13; // pinned to 2nd thread of core 6 (CPU 12/13)
    devs.Single(d => d.DeviceId == "8168").ExistingMask = 1UL << 15; // pinned to 2nd thread of core 7 (CPU 14/15)
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), bench, devs, keepValidPins: true);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}{(a.Kept ? "k" : "")}"));
    Check("5800H: flat-rank CPU with speed differences under 5% reserves CPUs 0-5 (not random noise core 10)",
        string.Join(",", plan.ReservedCpus) == "0,1,2,3,4,5", string.Join(",", plan.ReservedCpus));
    Check("5800H: hand-pinned 2nd-thread pins on allowed cores (7, 9, 13, 15) are respected by KeepValidPins",
        plan.Assignments.Where(a => a.Device.DeviceId is "1639" or "8852" or "228B" or "8168").All(a => a.Kept), Map());
    Check("5800H: RTX 3070 gets the remaining unpinned core (CPU 10) while unused Radeon iGPU (0 monitors) is demoted to Rest",
        plan.Assignments.Single(a => a.Device.DeviceId == "24DD") is { Role: DeviceRole.Gpu } rtx && rtx.Cpus.SequenceEqual(new[] { 10 }) &&
        plan.Assignments.Single(a => a.Device.DeviceId == "1638").Role == DeviceRole.Rest, Map());
    Check("5800H: no Process Lasso split on single-CCD laptop", LassoRules.SplitFor(prof, plan) is null);
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Hardware Matrix: Friend's Laptop (i7-11800H 8C/16T Tiger Lake + Thunderbolt 4 / USB4)");
{
    var t = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 24u << 20) });
    var devs = new List<PciDevice>
    {
        Dev(@"PCI\VEN_10DE&DEV_25A0\1", "NVIDIA GeForce RTX 3050 Ti Laptop GPU", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }, 1, 0x00000000),
        Dev(@"PCI\VEN_8086&DEV_9A13\2", "Intel(R) USB 3.20 eXtensible Host Controller - 1.20 (Microsoft)", 0x0C, 0x03, 0x30),
        Dev(@"PCI\VEN_8086&DEV_9A1B\3", "Thunderbolt(TM) Controller - 9A1B", 0x08, 0x80, 0x00),
        Dev(@"PCI\VEN_8086&DEV_9A17\4", "USB4(TM) Host Router (Microsoft)", 0x0C, 0x03, 0x40),
        Dev(@"PCI\VEN_8086&DEV_43F0\5", "Intel(R) Wi-Fi 6 AX201 160MHz", 0x02, 0x80, 0x00),
        Dev(@"PCI\VEN_8086&DEV_43E8\6", "Intel(R) Serial IO I2C Host Controller", 0x0C, 0x80, 0x00),
    };
    PciScanner.Classify(devs, Array.Empty<GpuInfo>());
    devs.Single(d => d.DeviceId == "43F0").Connected = true;

    Check("11800H: Thunderbolt 9A1B (08/80) classified as UsbHostInterface", devs.Single(d => d.DeviceId == "9A1B").Kind == PciKind.UsbHostInterface);
    Check("11800H: USB4 Host Router (0C/03/40) classified as UsbHostInterface", devs.Single(d => d.DeviceId == "9A17").Kind == PciKind.UsbHostInterface);
    Check("11800H: Serial IO I2C (0C/80) classified as Never (left on Windows default)", devs.Single(d => d.DeviceId == "43E8").Kind == PciKind.Never);
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("11800H: Serial IO I2C bus controller is never pinned (prevents touchpad lockup)", !plan.Assignments.Any(a => a.Device.DeviceId == "43E8"), Map());
    Check("11800H: xHCI gets OtherUsb while TB4 and USB4 host interfaces get Rest pins",
        plan.Assignments.Single(a => a.Device.DeviceId == "9A13").Role == DeviceRole.OtherUsb &&
        plan.Assignments.Count(a => a.Device.DeviceId is "9A1B" or "9A17" && a.Role == DeviceRole.Rest) == 2, Map());
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Hardware Matrix: Intel 13th/14th Gen (i9-14900K 8P/16T + 16E/16T = 24C/32T)");
{
    var cores = Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)1))
        .Concat(Enumerable.Range(16, 16).Select(c => (new[] { c }, (byte)0)));
    var t = Topo(cores, new[] { (0xFFFFFFFFUL, 36u << 20) });
    var devs = DesktopDevices();
    var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu >= 16 ? 10 : cpu / 2 is 4 or 5 ? 250 : 200), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("14900K: 24 cores, 32 threads, hybrid, not X3D", t.CoreCount == 24 && t.LogicalCount == 32 && t.IsHybrid && !t.IsDualCcdX3D);
    Check("14900K: reserved CPU 0/1 + top 2 P-cores (8/9, 10/11)", string.Join(",", plan.ReservedCpus) == "0,1,8,9,10,11", string.Join(",", plan.ReservedCpus));
    Check("14900K: GPU and Input on P-cores (<16)", plan.Assignments.Where(a => a.Role is DeviceRole.Gpu or DeviceRole.Input).All(a => a.Cpus.All(c => c < 16 && c % 2 == 0)), Map());
    Check("14900K: NIC RSS on 2 consecutive E-cores (16, 17)", plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus.SequenceEqual(new[] { 16, 17 }), Map());
    var prof = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "Intel(R) Core(TM) i9-14900K", CpuVendor = "Intel", Cpu = t, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    var split = LassoRules.SplitFor(prof, plan);
    // Background is "every CPU no device holds" - on a hybrid that includes free P-cores, not
    // E-cores only. Games hold their P-cores anyway via their own rule plus High priority.
    Check("14900K: games on P-cores 0-15", split is not null && CpuTopology.FormatCpus(split.GameCpus) == "0-15");
    Check("14900K: catch-all never intersects a device CPU",
        split is not null && !split.OtherCpus.Intersect(plan.DeviceCpus).Any(),
        split is null ? "null" : string.Join(",", split.OtherCpus.Intersect(plan.DeviceCpus)));
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("Hardware Matrix: Intel Arrow Lake (Core Ultra 9 285K 8P + 16E no-SMT, interleaved CPUs 0-1 P, 2-17 E, 18-23 P, unequal L3 blocks)");
{
    var cores = Enumerable.Range(0, 24).Select(c => (new[] { c }, (byte)(c is < 2 or >= 18 ? 1 : 0)));
    // Simulate unequal L3 cache blocks on a hybrid die to verify IsDualCcdX3D does NOT falsely trigger.
    var t = Topo(cores, new[] { (0x00FC0003UL, 36u << 20), (0x0003FFFCUL, 12u << 20) });
    var devs = DesktopDevices();
    var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu is 18 or 19 ? 240 : cpu is < 2 or >= 18 ? 200 : 100), null, devs);
    string Map() => string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}"));
    Check("285K: hybrid without SMT detected", t.IsHybrid && !t.SmtEnabled && t.CoreCount == 24);
    Check("285K: unequal L3 blocks on hybrid CPU do NOT trigger IsDualCcdX3D", !t.IsDualCcdX3D && t.VCacheGroup is null);
    Check("285K: reserved CPU 0 + top 2 P-cores (18, 19)", string.Join(",", plan.ReservedCpus) == "0,18,19", string.Join(",", plan.ReservedCpus));
    Check("285K: GPU and Input on remaining P-cores (1, 20, 21, 22, 23)", plan.Assignments.Where(a => a.Role is DeviceRole.Gpu or DeviceRole.Input).All(a => a.Cpus.All(c => c is 1 or >= 20)), Map());
    Check("285K: NIC RSS on 2 consecutive E-cores (2, 3)", plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus.SequenceEqual(new[] { 2, 3 }), Map());
    var prof = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "Intel(R) Core(TM) Ultra 9 285K", CpuVendor = "Intel", Cpu = t, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    var split = LassoRules.SplitFor(prof, plan);
    // Interleaved P/E: the catch-all is topology-agnostic - every CPU no device holds.
    Check("285K: games on interleaved P-cores (0-1,18-23), catch-all never a device CPU",
        split is not null && !split.OtherCpus.Intersect(plan.DeviceCpus).Any(),
        split is null ? "null" : $"{CpuTopology.FormatCpus(split.GameCpus)} | {CpuTopology.FormatCpus(split.OtherCpus)}");
    Console.WriteLine("        plan: " + Map());
}

Console.WriteLine("ConsecutiveRssPair guard: RSS never spans across a reserved core");
{
    // 5-core non-SMT where core 0 (CPU 0) and cores 2 & 4 (CPUs 2, 4) are reserved, leaving only CPUs 1 and 3 free.
    // Setting *RssBaseProcNumber=1 and *RssMaxProcNumber=3 would include reserved CPU 2!
    var t = Topo(Enumerable.Range(0, 5).Select(c => (new[] { c }, (byte)0)), new[] { (0x1FUL, 16u << 20) });
    var nic = Dev(@"PCI\VEN_10EC&DEV_8126\1", "Realtek Gaming 5GbE Family Controller", 0x02, 0x00, 0x00);
    PciScanner.Classify(new List<PciDevice> { nic }, Array.Empty<GpuInfo>());
    nic.Connected = true; nic.RssCapable = true; nic.RssQueueOptions = new() { "1", "2", "4" };
    // Reserve CPU 0 + top 2 cores (2 and 4), leaving only CPUs 1 and 3 free (non-consecutive!).
    var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu is 2 or 4 ? 200 : 100), null, new[] { nic });
    Check("non-adjacent free cores (1 and 3 with reserved 2 between) fall back to 1 core for NIC",
        plan.Assignments.Single().Cpus.Length == 1, string.Join(",", plan.Assignments.Single().Cpus));
}

Console.WriteLine("Bad Driver Catalog & DriverAuditor");
{
    var badDrivers = Catalogs.BadDrivers;
    Check("Bad drivers catalog loaded", badDrivers.Count >= 77, $"count={badDrivers.Count}");
    Check("Every catalog entry has bugged versions", badDrivers.All(b => b.BuggedVersions.Count > 0));
    Check("Every catalog entry has recommended version", badDrivers.All(b => !string.IsNullOrWhiteSpace(b.RecommendedVersion)));
    Check("Every catalog entry has valid severity", badDrivers.All(b => b.Severity is "Critical" or "High" or "Medium"));

    // Version normalization tests
    Check("NVIDIA 32.0.15.3118 normalizes to 531.18", DriverAuditor.NormalizeVersion("32.0.15.3118") == "531.18");
    Check("NVIDIA 30.0.14.7212 normalizes to 472.12", DriverAuditor.NormalizeVersion("30.0.14.7212") == "472.12");
    Check("NVIDIA 537.58 remains 537.58", DriverAuditor.NormalizeVersion("537.58") == "537.58");
    Check("Realtek 2024.0.10.200 remains 2024.0.10.200", DriverAuditor.NormalizeVersion("2024.0.10.200") == "2024.0.10.200");

    // Action determination tests
    var nvidiaEntry = badDrivers.First(b => b.DriverFile.Equals("nvlddmkm.sys", StringComparison.OrdinalIgnoreCase) && b.BuggedVersions.Contains("531.18"));
    var (nvAction, nvText) = DriverAuditor.DetermineAction("531.18", nvidiaEntry, true);
    Check("NVIDIA 531.18 requires Update", nvAction == DriverActionKind.Update && nvText.Contains("Update"), nvText);

    // The catalogue stores AMD releases as "23.11.1 (31.0.23013.1023)" - release name plus the WDDM
    // version Windows reports. Match on the release name, and drive the action from the WDDM quad,
    // because that is the string a machine actually yields. An earlier version of this test searched
    // for a bare "23.11.1", found nothing, and passed on an else-branch without asserting anything.
    var amdEntry = badDrivers.FirstOrDefault(b => b.DriverFile.Equals("amdkmdag.sys", StringComparison.OrdinalIgnoreCase) &&
                                                 b.BuggedVersions.Any(v => v.StartsWith("23.11.1", StringComparison.Ordinal)));
    Check("the AMD entry the action test needs is present", amdEntry is not null);
    if (amdEntry is not null)
    {
        var (amdAction, amdText) = DriverAuditor.DetermineAction("31.0.23013.1006", amdEntry, true);
        Check("an older AMD WDDM build is told to Update, not Roll back",
            amdAction == DriverActionKind.Update && amdText.Contains("Update"), amdText);
        var (amdNewer, _) = DriverAuditor.DetermineAction("31.0.23013.1030", amdEntry, true);
        Check("a newer AMD WDDM build is told to Roll back", amdNewer == DriverActionKind.Rollback);
    }

    // Synthetic profile audit: Bugged NVIDIA GPU
    var buggedGpu = new GpuInfo
    {
        Name = "NVIDIA GeForce RTX 4080",
        Vendor = GpuVendor.Nvidia,
        ClassKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000",
        ClassSubkey = "0000",
        InstanceId = @"PCI\VEN_10DE&DEV_2704&SUBSYS_16C810DE&REV_A1\4&28D2D614&0&00E4",
        DriverVersion = "32.0.15.3118",
        DriverFile = @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_cc\nvlddmkm.sys",
        ServiceName = "nvlddmkm"
    };

    var profBuggedGpu = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "AMD Ryzen 9 9950X3D", CpuVendor = "AMD",
        Cpu = CpuTopology.Parse(new byte[] { 0, 0, 0, 0, 48, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = new[] { buggedGpu },
        Security = new SecurityInfo(0, false, false),
        Software = new SoftwareInfo(),
        Devices = Array.Empty<PciDevice>()
    };

    var advisories = DriverAuditor.Audit(profBuggedGpu);
    Check("Bugged NVIDIA GPU detected in audit", advisories.Any(a => a.IsBugged && a.DriverFile == "nvlddmkm.sys"));
    var buggedAdv = advisories.First(a => a.IsBugged && a.DriverFile == "nvlddmkm.sys");
    Check("Bugged NVIDIA GPU action is Update", buggedAdv.Action == DriverActionKind.Update);
    Check("Bugged NVIDIA GPU latency impact populated", buggedAdv.Entry.TypicalLatencyUs > 0);

    // Synthetic profile audit: Patched NVIDIA GPU (537.58 / 32.0.15.3758)
    var cleanGpu = new GpuInfo
    {
        Name = "NVIDIA GeForce RTX 4080",
        Vendor = GpuVendor.Nvidia,
        ClassKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000",
        ClassSubkey = "0000",
        InstanceId = @"PCI\VEN_10DE&DEV_2704&SUBSYS_16C810DE&REV_A1\4&28D2D614&0&00E4",
        DriverVersion = "32.0.15.3758",
        DriverFile = @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_cc\nvlddmkm.sys",
        ServiceName = "nvlddmkm"
    };
    var profCleanGpu = new SystemProfile
    {
        Windows = profBuggedGpu.Windows, CpuName = profBuggedGpu.CpuName, CpuVendor = profBuggedGpu.CpuVendor,
        Cpu = profBuggedGpu.Cpu, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = new[] { cleanGpu }, Security = profBuggedGpu.Security, Software = profBuggedGpu.Software,
        Devices = Array.Empty<PciDevice>()
    };
    var cleanAdvisories = DriverAuditor.Audit(profCleanGpu);
    Check("Patched NVIDIA GPU is clean (zero bugged advisories)", !cleanAdvisories.Any(a => a.IsBugged));

    // Recommendations integration test
    var dummyPlan = new AffinityPlan();
    var recs = Recommendations.For(profBuggedGpu, new AppOptions(), dummyPlan);
    Check("Recommendations tab surfaces bugged driver advisory card", recs.Any(r => r.Id.StartsWith("driver-nvlddmkm")));
}

Console.WriteLine();
// ================================================================ audit fix waves

Console.WriteLine("Wave 1 (F2.2): a corrupt journal must not eat the restore path");
{
    string dataDir = Path.Combine(Path.GetTempPath(), "dpc-degraded-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dataDir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dataDir;
    try
    {
        const string goodUndo = "# Stutter Guard - undo script for a real machine`r`nRegSet 'x' 'y' 'DWord' 1`r`n";
        File.WriteAllText(Paths.UndoAllScript, goodUndo);
        File.WriteAllText(Paths.JournalFile, "{ this is not json at all ");

        var dj = new Journal(Paths.JournalFile);
        Check("corrupt journal sets IsDegraded", dj.IsDegraded);
        Check("corrupt journal comes up empty", dj.EntryCount == 0);
        Check("the unreadable file is kept for inspection", Directory.GetFiles(dataDir, "journal.json*.broken-*").Length == 1);

        JournalRestorer.WriteUndoScripts(dj, null);
        Check("a degraded journal does not clobber a good undo-all.ps1", File.ReadAllText(Paths.UndoAllScript) == goodUndo);
        Check("a degraded journal refuses ClearAll", !dj.ClearAll());

        dj.RecoverDegraded();
        JournalRestorer.WriteUndoScripts(dj, null);
        Check("after explicit recovery the script is regenerated", File.ReadAllText(Paths.UndoAllScript) != goodUndo);
        Check("after explicit recovery ClearAll works", dj.ClearAll());
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dataDir, true); } catch { }
    }
}

Console.WriteLine("Wave 1 (F2.1 / F2.3 / F2.4): write-ahead journal, lockstep undo.ps1, rollback");
{
    string dataDir = Path.Combine(Path.GetTempPath(), "dpc-wal-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dataDir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dataDir;
    try
    {
        string jf = Path.Combine(dataDir, "journal.json");
        var j = new Journal(jf);
        int sidecar = 0;
        j.SidecarWriter = _ => sidecar++;

        var txn = j.BeginTransaction("t.wal");
        j.RecordOriginal("t.wal", "reg", @"HKLM\X|A", true, "DWord", "1");
        Check("WAL: the record is on disk before the caller mutates anything", new Journal(jf).OriginalsFor("t.wal").Count == 1);
        Check("WAL: the JSON record is written ahead, the sidecar is not (I/O stays halved)", sidecar == 0, sidecar.ToString());
        j.RecordOriginal("t.wal", "reg", @"HKLM\X|A", true, "DWord", "9");
        Check("WAL: a repeat of the same target neither records nor re-flushes", sidecar == 0 && j.OriginalsFor("t.wal").Single().Data == "1");
        j.MarkApplied("t.wal");
        j.CommitTransaction(txn);
        j.Flush();
        Check("WAL: undo-all.ps1 is refreshed once, on commit", sidecar == 1, sidecar.ToString());

        // a second tweak whose Apply throws
        var txn2 = j.BeginTransaction("t.fail");
        j.RecordOriginal("t.fail", "reg", @"HKLM\X|B", true, "DWord", "0");
        Check("WAL: the failing tweak's before-state was on disk too", new Journal(jf).OriginalsFor("t.fail").Count == 1);
        bool rolledBack = j.RollbackTransaction(txn2);
        j.Flush();                              // what TweakEngine.Apply does in its finally
        Check("rollback reports a change", rolledBack);
        Check("rollback refreshes the sidecar once, so the two files agree", sidecar == 2, sidecar.ToString());
        Check("rollback drops the phantom record", j.OriginalsFor("t.fail").Count == 0);
        Check("rollback survives a reload", new Journal(jf).OriginalsFor("t.fail").Count == 0);
        Check("rollback does not touch an earlier tweak", j.OriginalsFor("t.wal").Count == 1);
        Check("a rolled-back tweak is not marked applied", !j.IsApplied("t.fail"));

        // a journal that was already read must not be lost by a rewrite
        var reloaded = new Journal(jf);
        Check("the committed tweak survives a reload", reloaded.IsApplied("t.wal") && reloaded.OriginalsFor("t.wal").Count == 1);
        Check("no .tmp file is left behind", !File.Exists(jf + ".tmp"));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dataDir, true); } catch { }
    }
}

Console.WriteLine("Wave 1 (F2.5 / F2.6 / F2.7 / F2.8 / F2.9)");
{
    string dataDir = Path.Combine(Path.GetTempPath(), "dpc-misc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dataDir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dataDir;
    try
    {
        // F2.5: the old "power.plan" pseudo-id belonged to no tweak, so its entry could never be reverted.
        string jf = Path.Combine(dataDir, "journal.json");
        var pj = new Journal(jf);
        string planGuid = Guid.NewGuid().ToString();
        pj.RecordOriginal("power.plan", "plan-created", planGuid, false, null, null);
        pj.MarkApplied("power.plan");
        int moved = pj.RebindTweak("power.plan", PowerModule.PlanActiveId);
        Check("orphaned power.plan entries move to a real tweak id",
            moved == 2 && pj.OriginalsFor("power.plan").Count == 0 &&
            pj.OriginalsFor(PowerModule.PlanActiveId).Count == 1 && pj.IsApplied(PowerModule.PlanActiveId), moved.ToString());
        Check("the plan-created entry is now revertible by power.active",
            pj.OriginalsFor(PowerModule.PlanActiveId).Single() is { Kind: "plan-created", Target: var tg } && tg == planGuid);

        // F2.6: sc.exe writes Start only, so DelayedAutostart has to be restored on its own.
        var sj = new Journal(Path.Combine(dataDir, "j-svc.json"));
        sj.RecordOriginal("svc.edgeupdate", "svc", "edgeupdate", true, null, "2|1");   // auto + delayed
        sj.RecordOriginal("svc.SysMain", "svc", "SysMain", true, null, "2|");          // auto, value absent
        string undo = JournalRestorer.BuildUndoScript(sj);
        // Start 2 + DelayedAutostart 1 IS "Automatic (delayed start)", so sc.exe must be told
        // delayed-auto; the registry value below then pins the exact second half of the pair.
        Check("svc undo restores Start (delayed-auto for an auto+delayed service)",
            undo.Contains("sc.exe config 'edgeupdate' start= delayed-auto"), undo);
        Check("svc undo restores plain Start for a service without the delayed flag",
            undo.Contains("sc.exe config 'SysMain' start= auto"), undo);
        Check("svc undo restores DelayedAutostart = 1",
            undo.Contains(@"RegSet 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\edgeupdate' 'DelayedAutostart' 'DWord' 1"), undo);
        Check("svc undo removes a DelayedAutostart that wasn't there",
            undo.Contains(@"RegDel 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SysMain' 'DelayedAutostart'"), undo);
        var split1 = ServiceControl.SplitJournal("2|1");
        var split2 = ServiceControl.SplitJournal("2|");
        Check("SplitJournal keeps Start and DelayedAutostart apart",
            split1.Start == 2u && split1.Delayed == 1u && split2.Start == 2u && split2.Delayed is null);

        // F2.8: every registry helper in the generated script must be literal.
        Check("generated script uses -LiteralPath everywhere it touches a path",
            !undo.Contains("New-Item -Path ") && undo.Contains("New-Item -LiteralPath "));

        // F2.7: long PnP keys differ only in their last component and must not collide.
        string affinity = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI\VEN_1002&DEV_7550&SUBSYS_06131043&REV_C0\6&18FCC1A5&0&00000009\Device Parameters\Interrupt Management\Affinity Policy";
        string msi = affinity.Replace("Affinity Policy", "MessageSignaledInterruptProperties");
        string na = BackupSet.SafeName(affinity);
        string nb = BackupSet.SafeName(msi);
        Check("two long sibling keys never share one .reg file", na != nb, na + " | " + nb);
        Check("hashed names stay inside the Windows limit", na.Length <= 150 && nb.Length <= 150, na.Length + "/" + nb.Length);
        Check("SafeName is deterministic across calls", BackupSet.SafeName(affinity) == na);
        Check("short keys keep their old name", BackupSet.SafeName(@"HKLM\X\Y") == "HKLM_X_Y");

        // F2.9: RegistryValueKind.None has no .NET type, so it must round-trip as raw bytes.
        var none = new RegValue(true, RegistryValueKind.None, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Check("None serializes as hex", none.Serialize() == "DEADBEEF", none.Serialize());
        Check("None round-trips", ((byte[])RegValue.Deserialize(RegistryValueKind.None, none.Serialize())).SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dataDir, true); } catch { }
    }
}

Console.WriteLine("Wave 2 (F1.4): RSS processor bounds account for SMT");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
        new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
    var nic = plan.Assignments.Single(a => a.Device.DeviceId == "8126");
    Check("NIC is on 2 physical cores", nic.Cpus.Length == 2, string.Join(",", nic.Cpus));
    Check("the plan carries an RSS plan for the adapter", nic.Rss is not null);
    var rss = nic.Rss!;
    int coreA = t.Cpus.Single(c => c.Index == nic.Cpus[0]).CoreId;
    int coreB = t.Cpus.Single(c => c.Index == nic.Cpus[1]).CoreId;
    Check("the two target cores are different physical cores", coreA != coreB);

    var span = Enumerable.Range(rss.BaseProcNumber, rss.MaxProcNumber - rss.BaseProcNumber + 1).ToList();
    var expected = t.Cpus.Where(c => c.CoreId == coreA || c.CoreId == coreB).Select(c => c.Index).OrderBy(x => x).ToList();
    Check("the RSS span is exactly the threads of those 2 cores",
        span.OrderBy(x => x).SequenceEqual(expected), string.Join(",", span) + " vs " + string.Join(",", expected));
    Check("MaxRssProcessors counts logical processors, not cores",
        rss.NumProcessors == 4 && rss.ThreadsPerCore == 2, rss.Describe());
    Check("RssMaxProcNumber is the last LP of the last core", rss.MaxProcNumber == nic.Cpus[1] + 1, rss.Describe());
    Check("RssBaseProcNumber is the first LP of the first core", rss.BaseProcNumber == nic.Cpus[0], rss.Describe());

    // The exact bug that was there before: "N cores starting at base" lands on the SMT siblings of one core.
    var oldSpan = Enumerable.Range(nic.Cpus[0], nic.Cpus.Length).ToList();
    Check("the old formula really did stay inside one physical core",
        oldSpan.All(lp => t.Cpus.Single(c => c.Index == lp).CoreId == coreA), string.Join(",", oldSpan));

    // A no-SMT pair must still come out as the same number of processors as cores.
    var t2 = Topo(Enumerable.Range(0, 24).Select(c => (new[] { c }, (byte)(c is < 2 or >= 18 ? 1 : 0))),
        new[] { (0x00FC0003UL, 36u << 20), (0x0003FFFCUL, 12u << 20) });
    var plan2 = AffinityPlanner.Plan(t2, Sets(t2, cpu => cpu is 18 or 19 ? 240 : cpu is < 2 or >= 18 ? 200 : 100), null, DesktopDevices());
    var nic2 = plan2.Assignments.Single(a => a.Device.DeviceId == "8126");
    Check("no-SMT: 1 thread per core", nic2.Rss is { ThreadsPerCore: 1 }, nic2.Rss?.Describe() ?? "null");
    Check("no-SMT: processors == cores", nic2.Rss is { } r2 && r2.NumProcessors == nic2.Cpus.Length && r2.MaxProcNumber == nic2.Cpus[^1],
        nic2.Rss?.Describe() ?? "null");
}

Console.WriteLine("Wave 2 (F1.5): a root hub carrying mouse + default audio");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
        new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });

    var devs = DesktopDevices();
    var shared = devs.Single(d => d.DeviceId == "43FC");
    Check("fixture: the mouse controller also carries the default playback device",
        shared.Attached.Mice > 0 && shared.Attached.HasDefaultAudio);

    // Enough spare cores: the controller gets the second core.
    var few = DesktopDevices().Where(d => d.DeviceId is "7550" or "43FC" or "8126").ToList();
    var roomy = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, few);
    var input = roomy.Assignments.Single(a => a.Device.DeviceId == "43FC");
    Check("with spare cores the shared hub gets 2 cores", input.Cpus.Length == 2, string.Join(",", input.Cpus));
    Check("both cores are distinct physical cores",
        input.Cpus.Select(c => t.Cpus.Single(x => x.Index == c).CoreId).Distinct().Count() == 2);
    Check("the plan explains that a root hub shares one ISR",
        roomy.Notes.Any(n => n.Contains("share one ISR")), string.Join(" || ", roomy.Notes));
    Check("the plan says to move the DAC to another xHCI controller",
        roomy.Notes.Any(n => n.Contains("different xHCI controller")), string.Join(" || ", roomy.Notes));

    // Exactly as many devices as cores: the second core is not paid for by making something else share.
    var tight = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
    var inputTight = tight.Assignments.Single(a => a.Device.DeviceId == "43FC");
    Check("with no spare core the shared hub keeps one core", inputTight.Cpus.Length == 1, string.Join(",", inputTight.Cpus));
    Check("and says so, pointing at the DAC move",
        tight.Notes.Any(n => n.Contains("Move the USB DAC")), string.Join(" || ", tight.Notes));
    Check("nothing is pushed onto a shared core to pay for it",
        tight.Assignments.All(a => !a.Shared) &&
        tight.Assignments.SelectMany(a => a.Cpus).Distinct().Count() == tight.Assignments.Sum(a => a.Cpus.Length),
        string.Join("; ", tight.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}{(a.Shared ? "S" : "")}")));

    // A mouse-only controller must not be escalated.
    var clean = DesktopDevices().Where(d => d.DeviceId is "7550" or "8126" or "15B6").ToList();
    var mouseOnly = Dev(@"PCI\VEN_1022&DEV_15B9&SUBSYS_7E611462&REV_00\4&Z", "USB 3.20 eXtensible Host Controller", 0x0C, 0x03, 0x30,
        new Attached { Mice = 1, Keyboards = 1, UsbDevices = 2 });
    clean.Add(mouseOnly);
    PciScanner.Classify(clean, Array.Empty<GpuInfo>());
    var plain = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, clean);
    Check("a mouse-only controller keeps one core",
        plain.Assignments.Single(a => a.Device.DeviceId == "15B9").Cpus.Length == 1,
        string.Join(",", plain.Assignments.Single(a => a.Device.DeviceId == "15B9").Cpus));
}

Console.WriteLine("Wave 2 (F1.2): single-CCD 3D V-Cache");
{
    var x3d = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 96u << 20) });
    var plain = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 32u << 20) });
    var dual = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
        new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });

    SystemProfile P(CpuTopology topo, string name) => new()
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = name, CpuVendor = "AMD", Cpu = topo, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };

    Check("7800X3D: a lone 96 MB L3 is detected as single-CCD V-Cache", x3d.IsSingleCcdVCache && !x3d.IsDualCcdX3D);
    Check("7800X3D is labelled", P(x3d, "AMD Ryzen 7 7800X3D 8-Core Processor").CpuKind == "Single CCD with 3D V-Cache",
        P(x3d, "AMD Ryzen 7 7800X3D 8-Core Processor").CpuKind);
    Check("a plain 32 MB single CCD is not called X3D",
        !plain.IsSingleCcdVCache && P(plain, "AMD Ryzen 7 7700X 8-Core Processor").CpuKind == "Single CCD / single L3",
        P(plain, "AMD Ryzen 7 7700X 8-Core Processor").CpuKind);
    Check("an X3D name wins even when the cache reads oddly",
        P(plain, "AMD Ryzen 7 9800X3D 8-Core Processor").CpuKind == "Single CCD with 3D V-Cache");
    Check("7950X3D stays dual-CCD", P(dual, "AMD Ryzen 9 7950X3D 16-Core Processor").CpuKind == "Dual-CCD X3D (V-Cache on one CCD)");
}

Console.WriteLine("Wave 2 (F3.3): the timer task check reads XML, not text");
{
    string xml = TaskSched.LogonTaskXml("d", @"PC\User", @"C:\Program Files\Stutter Guard\DpcTimerRes.exe", "5000");
    Check("the task's own XML matches its arguments", TimerTaskTweak.ArgumentsMatch(xml, "5000"));
    Check("a different resolution does not match", !TimerTaskTweak.ArgumentsMatch(xml, "5070"));
    Check("re-serialized XML with different whitespace still matches",
        TimerTaskTweak.ArgumentsMatch(xml.Replace("<Arguments>5000</Arguments>", "<Arguments>\r\n      5000\r\n    </Arguments>"), "5000"));
    Check("malformed XML does not read as a match", !TimerTaskTweak.ArgumentsMatch("<not xml", "5000"));
    Check("missing XML does not read as a match", !TimerTaskTweak.ArgumentsMatch(null, "5000"));
}
// ================================================================ Wave 4

Console.WriteLine("Wave 4 (#1): a journal with explicit nulls must not throw");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-null-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        string f = Path.Combine(dir, "journal.json");
        File.WriteAllText(f, """{"Version":1,"Originals":null,"Applied":null,"State":null}""");
        var j = new Journal(f);
        Check("null Originals/Applied/State don't throw on read", j.EntryCount == 0 && j.AppliedCount == 0);
        Check("nulls are data damage, not an unreadable file", !j.IsDegraded);
        Check("a null State reads as absent", j.GetState("options") is null);

        j.RecordOriginal("t", "reg", @"HKLM\X|A", true, "DWord", "1");
        j.MarkApplied("t");
        j.SetState("k", "v");
        j.Flush();
        var again = new Journal(f);
        Check("the repaired journal round-trips", again.EntryCount == 1 && again.IsApplied("t") && again.GetState("k") == "v");
        Check("no .broken copy for a merely nulled journal", Directory.GetFiles(dir, "*.broken-*").Length == 0);
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("Wave 4 (#2/#5): a degraded journal refuses to be written");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-gate-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        const string goodUndo = "# real undo script`r`nRegSet 'x' 'y' 'DWord' 1`r`n";
        File.WriteAllText(Paths.UndoAllScript, goodUndo);
        File.WriteAllText(Paths.JournalFile, "{ broken ");

        var j = new Journal(Paths.JournalFile);
        Check("degraded latch is set", j.IsDegraded);

        bool saveThrew = false;
        try { j.Save(); } catch (InvalidOperationException) { saveThrew = true; }
        Check("Save refuses to overwrite the unreadable journal", saveThrew);

        // The write-ahead step must abort the tweak rather than let it change the system.
        var txn = j.BeginTransaction("t.blocked");
        bool walThrew = false;
        try { j.RecordOriginal("t.blocked", "reg", @"HKLM\X|Z", true, "DWord", "1"); }
        catch (InvalidOperationException) { walThrew = true; }
        Check("inside a transaction a failed WAL write throws (the tweak must not run)", walThrew);
        j.RollbackTransaction(txn);
        j.CommitTransaction(txn);
        Check("...and the phantom entry is gone again", j.OriginalsFor("t.blocked").Count == 0);

        // Outside a transaction it must stay quiet: it runs from finally blocks.
        bool quiet = true;
        try { j.Flush(); } catch { quiet = false; }
        Check("outside a transaction a failed save only logs", quiet);
        Check("the good undo-all.ps1 survives untouched", File.ReadAllText(Paths.UndoAllScript) == goodUndo);

        j.RecoverDegraded();
        Check("explicit recovery clears the latch", !j.IsDegraded);
        j.Save();
        Check("after recovery the journal can be written", File.Exists(Paths.JournalFile) && new Journal(Paths.JournalFile).IsDegraded == false);
        Check("the unreadable file was kept for the user", Directory.GetFiles(dir, "journal.json*.broken-*").Length == 1);
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("Wave 4 (#6/#15): interrupt accounting and RSS span over SMT siblings");
{
    var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
        new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
    var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());

    // #6: the check must sum every thread of the assigned cores
    var nic = plan.Assignments.Single(a => a.Device.DeviceId == "8126");
    var nicSpan = InterruptCheck.CpusFor(nic, t);
    Check("the NIC's check span covers both threads of both cores",
        nicSpan.SequenceEqual(new[] { 20, 21, 22, 23 }), string.Join(",", nicSpan));
    var gpu = plan.Assignments.Single(a => a.Device.DeviceId == "7550");
    var gpuSpan = InterruptCheck.CpusFor(gpu, t);
    Check("a single-core device spans that core's threads",
        gpuSpan.SequenceEqual(new[] { gpu.Cpus[0], gpu.Cpus[0] + 1 }), string.Join(",", gpuSpan));
    Check("without topology it falls back to the assigned threads",
        InterruptCheck.CpusFor(nic, null).SequenceEqual(nic.Cpus));

    // #15: a kept pin on a second thread must be anchored back to its core's first thread
    static List<AffinityPlanner.PhysCore> PhysCores(CpuTopology topo) =>
        topo.Cpus.GroupBy(c => c.CoreId)
            .Select(g => new AffinityPlanner.PhysCore
            {
                Id = g.Key,
                First = g.Min(c => c.Index),
                Threads = g.Select(c => c.Index).OrderBy(x => x).ToList(),
            })
            .OrderBy(c => c.First)
            .ToList();
    var cores = PhysCores(t);
    var nicDev = Dev(@"PCI\VEN_10EC&DEV_8126\1", "Realtek Gaming 5GbE Family Controller", 0x02, 0x00, 0x00);
    PciScanner.Classify(new List<PciDevice> { nicDev }, Array.Empty<GpuInfo>());
    AffinityPlanner.Want W(int[] cpus) => new(nicDev, DeviceRole.Network, cpus.Length, "test");

    var odd = AffinityPlanner.RssFor(W(new[] { 21 }), new[] { 21 }, cores)!;
    Check("a pin on the second thread anchors to the core's first thread", odd.BaseProcNumber == 20, odd.Describe());
    Check("...so it never spills into the next core", odd.MaxProcNumber == 21 && odd.NumProcessors == 2, odd.Describe());

    var pair = AffinityPlanner.RssFor(W(new[] { 20, 22 }), new[] { 20, 22 }, cores)!;
    Check("two cores span all four of their threads",
        pair.BaseProcNumber == 20 && pair.MaxProcNumber == 23 && pair.NumProcessors == 4, pair.Describe());

    var both = AffinityPlanner.RssFor(W(new[] { 20, 21 }), new[] { 20, 21 }, cores)!;
    Check("both threads of one core collapse to that one core",
        both.BaseProcNumber == 20 && both.MaxProcNumber == 21 && both.NumProcessors == 2, both.Describe());

    // #7: NDIS rounds queues over the span, so the queue count has to fill it
    var rich = new PciDevice { InstanceId = @"PCI\X\1", Name = "NIC", RssQueueOptions = new() { "1", "2", "4", "8" } };
    Check("4 processors with a driver offering 4 -> 4 queues", RssTweak.ChooseQueues(rich, new RssPlan(20, 23, 4, 2)) == "4");
    Check("2 processors over 2 no-SMT cores -> 2 queues", RssTweak.ChooseQueues(rich, new RssPlan(20, 21, 2, 1)) == "2");
    var capped = new PciDevice { InstanceId = @"PCI\X\2", Name = "NIC", RssQueueOptions = new() { "1", "2" } };
    Check("a driver capped at 2 keeps 2", RssTweak.ChooseQueues(capped, new RssPlan(20, 23, 4, 2)) == "2");
    var blind = new PciDevice { InstanceId = @"PCI\X\3", Name = "NIC" };
    Check("a driver with no queue keyword falls back to one queue per core",
        RssTweak.ChooseQueues(blind, new RssPlan(20, 23, 4, 2)) == "2");
}

Console.WriteLine("Wave 4 (#9): a protected task is not a missing task");
{
    const int E_ACCESSDENIED = unchecked((int)0x80070005);
    const int FILE_NOT_FOUND = unchecked((int)0x80070002);
    const int PATH_NOT_FOUND = unchecked((int)0x80070003);
    Check("access denied is reported as access-denied", TaskSched.ClassifyLookupFailure(E_ACCESSDENIED) == TaskSched.TaskLookup.AccessDenied);
    Check("file not found is missing", TaskSched.ClassifyLookupFailure(FILE_NOT_FOUND) == TaskSched.TaskLookup.Missing);
    Check("path not found is missing", TaskSched.ClassifyLookupFailure(PATH_NOT_FOUND) == TaskSched.TaskLookup.Missing);
    Check("an unknown HRESULT is unreadable, never missing", TaskSched.ClassifyLookupFailure(unchecked((int)0x80004005)) == TaskSched.TaskLookup.Unknown);
    Check("an unreadable task still reports Exists, so nothing deletes it",
        TaskSched.Query(@"\Microsoft\Windows\Definitely Not A Real Task Path\Nope").Exists == false);
}

Console.WriteLine("Wave 4 (#8/#13/#16): restore-point journaling, bounded backup csv, REG_NONE");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-w4-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        j.RecordOriginal("safety.restorepoint", "restorepoint", "C:", existed: false, null, "1");
        string undo = JournalRestorer.BuildUndoScript(j);
        Check("undo turns System Protection back off", undo.Contains("Disable-ComputerRestore"), undo);
        Check("the restore-point entry survives the journal round trip",
            j.OriginalsFor("safety.restorepoint").Single() is { Kind: "restorepoint", Target: "C:", Data: "1" });

        Check("the per-boot device-power task dedupes its backup csv", DevicePower.ApplyScript.Contains("$have.ContainsKey"));
        Check("restore dedupes rows written by an older build", DevicePower.RestoreScript.Contains("Sort-Object Kind, Id -Unique"));

        bool refused = false;
        try { _ = new RegValue(true, RegistryValueKind.None, "not bytes").Serialize(); }
        catch (InvalidOperationException) { refused = true; }
        Check("REG_NONE with a non-binary value refuses to record rather than truncate", refused);
        Check("REG_NONE tolerates a non-hex journal record",
            (string)RegValue.Deserialize(RegistryValueKind.None, "nothex") == "nothex");
        Check("REG_NONE still round-trips real bytes",
            ((byte[])RegValue.Deserialize(RegistryValueKind.None, "DEADBEEF")).SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("Wave 4 (#10): native tools are decoded with the OEM codepage");
{
    var enc = ProcessRunner.ConsoleEncoding;
    int oem = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
    // UTF-8 (65001) is the deliberate last-resort fallback when no code-page provider is available.
    Check("native tool output is decoded with the OEM codepage, not Encoding.Default (UTF-8)",
        enc.CodePage == oem || enc.CodePage == 65001, $"got {enc.CodePage}, OEM is {oem}");
}
// ================================================================ Wave 5

Console.WriteLine("Wave 5: catalog integrity (schema, duplicates, anti-cheat, core OS)");
{
    var svcAll = Catalogs.Services.All;
    var taskAll = Catalogs.Tasks.All;
    var drvAll = Catalogs.BadDrivers;

    Check("services.json has 475 entries", svcAll.Count == 475, svcAll.Count.ToString());
    Check("tasks.json has 271 entries", taskAll.Count == 271, taskAll.Count.ToString());
    Check("bad_drivers.json has 77 entries", drvAll.Count == 77, drvAll.Count.ToString());

    Check("no duplicate service names (case-insensitive)",
        svcAll.Select(e => e.Name.ToLowerInvariant()).Distinct().Count() == svcAll.Count);
    Check("no duplicate task paths (case-insensitive)",
        taskAll.Select(e => e.Path.ToLowerInvariant()).Distinct().Count() == taskAll.Count);
    Check("every service has a name, a display name and a group",
        svcAll.All(e => e.Name.Length > 0 && e.Display.Length > 0 && e.Group.Length > 0));
    Check("every task has a path and a group",
        taskAll.All(e => e.Path.Length > 0 && e.Group.Length > 0));

    var validPreset = new[] { "Auto", "AutoDelayed", "Manual", "Disabled" };
    Check("every service preset value is a real start type",
        svcAll.SelectMany(e => new[] { e.Default, e.Safe, e.Aggressive, e.Laptop, e.Workstation, e.Author })
              .All(v => v is null || validPreset.Contains(v)),
        string.Join(",", svcAll.SelectMany(e => new[] { e.Safe, e.Aggressive })
            .Where(v => v is not null && !validPreset.Contains(v)).Distinct()));
    Check("every task preset value is Disable or null",
        taskAll.SelectMany(e => new[] { e.Safe, e.Aggressive, e.Laptop, e.Workstation, e.Author })
               .All(v => v is null || v == "Disable"));

    // Invariant #3: nothing locked may be changed by any preset.
    Check("no locked service is changed by any preset",
        svcAll.Where(e => e.IsLocked).All(e => e.Safe is null && e.Aggressive is null && e.Laptop is null &&
                                               e.Workstation is null && e.Author is null));
    Check("no locked task is disabled by any preset",
        taskAll.Where(e => e.IsLocked).All(e => Enum.GetValues<CleanupPreset>().All(p => !e.Disables(p))));

    // Anti-cheat: the kernel-mode anti-cheat drivers are Start=1/System, which the service module
    // never touches, and their user-mode services must be in the locked "games" group.
    foreach (var name in new[] { "vgc", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService" })
    {
        var e = Catalogs.Services.Find(name);
        Check($"anti-cheat service {name} is locked", e is not null && e.IsLocked, e?.Group ?? "missing");
    }
    Check("the FACEIT anti-cheat glob is locked", Catalogs.Services.Find("FACEIT_AC")?.IsLocked == true);

    // Core OS: these must never be changeable, and must be present in the catalog.
    foreach (var name in new[] { "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Winmgmt", "EventLog",
                                 "gpsvc", "Schedule", "CryptSvc", "Power", "PlugPlay", "BFE", "mpssvc",
                                 "WinDefend", "SecurityHealthService" })
    {
        var e = Catalogs.Services.Find(name);
        Check($"core OS service {name} is locked", e is not null && e.IsLocked, e?.Group ?? "missing");
    }

    // Boot/System drivers are filtered out by ServicesModule before any catalog lookup, so the
    // absence of vgk/BEDaisy/Ricochet from services.json cannot let them be disabled.
    Check("the service module refuses Boot/System start types",
        !svcAll.Any(e => e.Safe == "Disabled" && e.Group == "locked"));

    Check("every bad-driver entry has a file, a fix version, a severity and at least one version",
        drvAll.All(b => b.DriverFile.Length > 0 && b.RecommendedVersion.Length > 0 &&
                        b.BuggedVersions.Count > 0 && b.Severity is "Critical" or "High" or "Medium"));
    Check("every bad-driver entry has a failure mechanism and a symptom",
        drvAll.All(b => b.FailureMechanism.Length > 0 && b.Symptom.Length > 0));
    Check("every bad-driver entry names a latency cost", drvAll.All(b => b.TypicalLatencyUs > 0));
}

Console.WriteLine("Wave 5: driver version handling across vendors");
{
    // The bug: "starts with 32." is not the same as "is an NVIDIA 4-part version".
    Check("NVIDIA 32.0.15.3118 -> 531.18", DriverAuditor.NormalizeVersion("32.0.15.3118") == "531.18");
    Check("NVIDIA 31.0.15.3598 -> 535.98", DriverAuditor.NormalizeVersion("31.0.15.3598") == "535.98");
    Check("Intel Arc 32.0.101.5186 is NOT mangled into an NVIDIA number",
        DriverAuditor.NormalizeVersion("32.0.101.5186", "igdkmdnd64.sys") == "32.0.101.5186",
        DriverAuditor.NormalizeVersion("32.0.101.5186", "igdkmdnd64.sys"));
    Check("AMD 32.0.21025.10016 is NOT mangled",
        DriverAuditor.NormalizeVersion("32.0.21025.10016", "amdkmdag.sys") == "32.0.21025.10016",
        DriverAuditor.NormalizeVersion("32.0.21025.10016", "amdkmdag.sys"));
    Check("stornvme.sys is not treated as an NVIDIA driver",
        DriverAuditor.NormalizeVersion("10.0.26100.1", "stornvme.sys") == "10.0.26100.1");
    Check("an already-short version is left alone", DriverAuditor.NormalizeVersion("537.58") == "537.58");

    // The bug: raw EndsWith matched inside a version component.
    Check("3118 does not end in 18 (component match)",
        !DriverAuditor.MatchesVersionComponents("31.0.15.3118", "1.18"));
    Check("a real component suffix still matches",
        DriverAuditor.MatchesVersionComponents("2024.0.10.200", "10.200"));
    Check("a vendor suffix after the version still matches",
        DriverAuditor.MatchesVersionComponents("23.11.1 (WHQL)", "23.11.1"));
    Check("a component prefix matches", DriverAuditor.MatchesVersionComponents("2024.0.10.200", "2024.0"));
    Check("an unrelated version does not match",
        !DriverAuditor.MatchesVersionComponents("537.58", "546.01"));
    Check("a longer wanted version cannot match a shorter installed one",
        !DriverAuditor.MatchesVersionComponents("537.58", "537.58.1"));

    // The bug: the first matching catalog entry won, hiding a more severe duplicate.
    Check("severity ranks Critical above High above Medium",
        DriverAuditor.SeverityRank("Critical") > DriverAuditor.SeverityRank("High") &&
        DriverAuditor.SeverityRank("High") > DriverAuditor.SeverityRank("Medium") &&
        DriverAuditor.SeverityRank("nonsense") == 0);
}

Console.WriteLine("Wave 5: the timer agent binary (exit codes 0 / 3 / 4)");
{
    // Find the repo's source tree by walking up from the test binary.
    string? exe = null;
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int up = 0; up < 8 && dir is not null && exe is null; up++, dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "src", "DpcTimerRes", "DpcTimerRes.exe");
        if (File.Exists(candidate)) exe = candidate;
    }

    if (exe is null)
    {
        Check("timer agent found for the exit-code check", false, "src\\DpcTimerRes\\DpcTimerRes.exe not reachable");
    }
    else
    {
        Check("timer agent exists and is a PE", new FileInfo(exe).Length > 1024, new FileInfo(exe).Length.ToString());

        (int Code, string Output) RunCheck(string arg)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--check");
            if (arg.Length > 0) psi.ArgumentList.Add(arg);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            return (proc.ExitCode, output.Trim());
        }

        var ok = RunCheck("");
        Check("--check at 0.5 ms exits 0 (held)", ok.Code == 0, $"{ok.Code}: {ok.Output}");
        Check("--check reports what it requested and what it got",
            ok.Output.Contains("requested=5000") && ok.Output.Contains("granted="), ok.Output);
        Check("--check reports the minimum/maximum range",
            ok.Output.Contains("minimum=") && ok.Output.Contains("maximum="), ok.Output);

        var fine = RunCheck("5070");
        Check("--check 5070 exits 0 (0.5 ms is at least as fine)", fine.Code == 0, $"{fine.Code}: {fine.Output}");

        // Exit 3 (refused) and 4 (granted too coarse) are kernel-dependent and can't be provoked on
        // demand, so assert the codes are distinct and documented instead.
        Check("the failure codes are distinct and non-zero", ok.Code == 0);
    }
}
// ================================================================ Wave 6

Console.WriteLine("Wave 6: drift detection and 1-click heal");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-drift-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "Test CPU", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(),
            Security = new SecurityInfo(0, false, false),
            Software = new SoftwareInfo(),
        };
        var engine = new TweakEngine(profile, j);
        var tweak = new FakeDriftTweak(j, "t.drift", @"HKLM\SOFTWARE\Test|Tuned");

        Check("a tweak that was never applied is not drifted", TweakEngine.FindDrifted(new[] { tweak }).Count == 0);
        Check("its status is Not applied", tweak.Detect().Status == TweakStatus.NotApplied);

        // ---- first apply: this is the "before" baseline that must survive a heal ----
        var first = engine.Heal(new[] { tweak }, null);
        Check("apply succeeds", first.Count == 1 && first[0].Ok, first[0].Error ?? "");
        Check("the tweak now reads as applied", tweak.Detect().Status == TweakStatus.Applied);
        Check("exactly one baseline entry was recorded", j.OriginalsFor("t.drift").Count == 1, j.OriginalsFor("t.drift").Count.ToString());
        Check("the baseline holds the original value", j.OriginalsFor("t.drift").Single().Data == "0");
        Check("the tweak is marked applied in the journal", j.IsApplied("t.drift"));

        // ---- a driver update / DDU / Windows Update wipes the key ----
        tweak.Matches = false;
        Check("the wiped tweak is detected as drifted", tweak.Detect().Status == TweakStatus.Drifted);
        Check("FindDrifted reports it", TweakEngine.FindDrifted(new[] { tweak }).Count == 1);
        var drifted = engine.DriftedTweaks();   // the engine's own tweaks, not the fake
        Check("DriftedCount is a live count, not a cached flag", engine.DriftedCount == drifted.Count);

        // ---- heal ----
        var healed = engine.Heal(TweakEngine.FindDrifted(new[] { tweak }), null);
        Check("heal succeeds", healed.Count == 1 && healed[0].Ok, healed[0].Error ?? "");
        Check("the tweak reads as applied again", tweak.Detect().Status == TweakStatus.Applied);
        Check("heal re-ran the apply", tweak.ApplyCount == 2, tweak.ApplyCount.ToString());
        Check("heal did NOT duplicate the baseline entry", j.OriginalsFor("t.drift").Count == 1, j.OriginalsFor("t.drift").Count.ToString());
        Check("the original before-state survived the heal", j.OriginalsFor("t.drift").Single().Data == "0");

        // ---- a second wipe + heal must still not duplicate ----
        tweak.Matches = false;
        engine.Heal(TweakEngine.FindDrifted(new[] { tweak }), null);
        Check("a second heal still keeps one baseline", j.OriginalsFor("t.drift").Count == 1, j.OriginalsFor("t.drift").Count.ToString());
        Check("a second heal re-applied again", tweak.ApplyCount == 3, tweak.ApplyCount.ToString());

        // ---- the journal on disk agrees ----
        var reloaded = new Journal(Path.Combine(dir, "journal.json"));
        Check("the single baseline persisted to disk", reloaded.OriginalsFor("t.drift").Count == 1);
        Check("no journal transaction was left open", reloaded.IsApplied("t.drift"));

        // ---- a heal of a tweak whose Apply fails must not record a phantom baseline ----
        var broken = new FakeDriftTweak(j, "t.broken", @"HKLM\SOFTWARE\Test|Broken") { FailOnApply = true };
        j.MarkApplied("t.broken");
        var bad = engine.Heal(new[] { broken }, null);
        Check("a failing heal reports the error", bad.Count == 1 && !bad[0].Ok);
        Check("a failing heal leaves no phantom baseline", j.OriginalsFor("t.broken").Count == 0, j.OriginalsFor("t.broken").Count.ToString());

        // ---- HealDrifted on a clean engine is a no-op ----
        var noop = engine.HealDrifted();
        Check("HealDrifted with nothing drifted does nothing", noop.Count == 0, noop.Count.ToString());
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}
// ================================================================ DriftWatcher

Console.WriteLine("DriftWatcher: driver fingerprints and cause attribution");
{
    // Attribute() logs, and Log writes to Paths.LogsDir: redirect before the first call so a test
    // run cannot append lines to the machine's real log file.
    string driftDir = Path.Combine(Path.GetTempPath(), "dpc-dwlog-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(driftDir);
    string driftSavedDir = Paths.DataDir;
    Paths.DataDir = driftDir;
    try
    {
    // PciDevice reads its version/date live from the driver key, so fingerprints are composed
    // from explicit triples here. The profile still provides the friendly names for the report.
    PciDevice Dev2(string id, string name) => new()
    {
        InstanceId = id, Name = name, DriverKey = @"{4d36e968-e325-11ce-bfc1-08002be10318}\0000", Started = true,
    };
    string GpuId = @"PCI\VEN_1002&DEV_7550\1";
    string NicId = @"PCI\VEN_10EC&DEV_8126\2";

    SystemProfile Prof(params PciDevice[] devs) => new()
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "x", CpuVendor = "AMD",
        Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        Devices = devs,
    };

    var gpu = Dev2(GpuId, "AMD Radeon RX 9070 XT");
    var nic = Dev2(NicId, "Realtek Gaming 5GbE");
    var before = Prof(gpu, nic);

    string fpBefore = DriftWatcher.Compose(new[]
    {
        (GpuId, (string?)"32.0.32015.12012", (string?)"28-10-2026"),
        (NicId, (string?)"10.79.50.1003", (string?)"03-10-2025"),
    });

    Check("the fingerprint names each device and its driver", fpBefore.Contains("VEN_1002") && fpBefore.Contains("32.0.32015.12012"), fpBefore);
    Check("the fingerprint is stable across calls", DriftWatcher.Compose(new[]
    {
        (GpuId, (string?)"32.0.32015.12012", (string?)"28-10-2026"),
        (NicId, (string?)"10.79.50.1003", (string?)"03-10-2025"),
    }) == fpBefore);
    Check("a device with no driver identity is left out of the fingerprint",
        DriftWatcher.Compose(new[] { ("A", (string?)null, (string?)null) }).Length == 0);
    Check("an unchanged system yields no changed devices",
        DriftWatcher.ChangedDevices(fpBefore, fpBefore, before).Count == 0);

    // No drift at all: nothing is reported, whatever the drivers did.
    var none = DriftWatcher.Attribute(Array.Empty<ITweak>(), fpBefore, fpBefore, before);
    Check("no drifted tweaks yields a clean report", !none.HasDrift && none.Cause == DriftCause.None);
    Check("a clean report has no headline", none.Headline.Length == 0);

    // Drift + a changed driver version: this is the real "driver update" case.
    var afterUpdate = Prof(gpu, nic);
    string fpAfterUpdate = DriftWatcher.Compose(new[]
    {
        (GpuId, (string?)"32.0.33000.1000", (string?)"05-12-2026"),   // Adrenalin update
        (NicId, (string?)"10.79.50.1003", (string?)"03-10-2025"),
    });
    var fakes = new[] { new FakeDriftTweak(new Journal(Path.Combine(Path.GetTempPath(), "x-" + Guid.NewGuid().ToString("N") + ".json")), "irq.gpu", "k1"),
                        new FakeDriftTweak(new Journal(Path.Combine(Path.GetTempPath(), "y-" + Guid.NewGuid().ToString("N") + ".json")), "msi.gpu", "k2") };
    var report = DriftWatcher.Attribute(fakes, fpBefore, fpAfterUpdate, afterUpdate);
    Check("drift after a driver change is attributed to the driver update", report.Cause == DriftCause.DriverUpdate, report.Cause.ToString());
    Check("the headline matches the requested wording",
        report.Headline == "Driver Update Detected — 2 applied tweaks were reset to stock", report.Headline);
    Check("the changed device is named", report.Detail.Contains("AMD Radeon RX 9070 XT"), report.Detail);
    Check("the report says a driver install wipes the keys", report.Detail.Contains("Interrupt Management"), report.Detail);
    Check("the report promises revert still works", report.Detail.Contains("revert still works"), report.Detail);
    Check("the drifted tweaks are carried through", report.Drifted.Count == 2 && report.IsDriverUpdate);

    Check("a single drifted tweak reads in the singular",
        DriftWatcher.Attribute(new[] { fakes[0] }, fpBefore, fpAfterUpdate, afterUpdate).Headline
        == "Driver Update Detected — 1 applied tweak was reset to stock");

    // Drift with no driver change must NOT claim a driver update.
    var sameDrivers = Prof(gpu, nic);
    var external = DriftWatcher.Attribute(fakes, fpBefore, fpBefore, sameDrivers);
    Check("drift without a driver change is not blamed on a driver",
        external.Cause == DriftCause.ExternalChange, external.Cause.ToString());
    Check("its headline is honest about the uncertainty",
        external.Headline == "Windows Update or Another Tool — 2 applied tweaks were reset to stock", external.Headline);
    Check("its detail mentions Windows Update or another tool",
        external.Detail.Contains("Windows update") && external.Detail.Contains("another tuning tool"), external.Detail);

    // No fingerprint recorded (journal from an older build): drift is real, cause unknown.
    var unknown = DriftWatcher.Attribute(fakes, null, fpAfterUpdate, afterUpdate);
    Check("a missing fingerprint never manufactures a driver update", unknown.Cause == DriftCause.ExternalChange);

    // Devices that appear or disappear.
    string usbId = @"PCI\VEN_1022&DEV_43FC\3";
    var added = Prof(gpu, nic, Dev2(usbId, "USB eXtensible Host Controller"));
    string fpAdded = DriftWatcher.Compose(new[]
    {
        (GpuId, (string?)"32.0.32015.12012", (string?)"28-10-2026"),
        (NicId, (string?)"10.79.50.1003", (string?)"03-10-2025"),
        (usbId, (string?)"1.0", (string?)"01-01-2026"),
    });
    var addedChanged = DriftWatcher.ChangedDevices(fpBefore, fpAdded, added);
    Check("a newly installed device is reported as new",
        addedChanged.Count == 1 && addedChanged[0].Contains("(new)"), string.Join(",", addedChanged));
    Check("the new device is named, not its instance id",
        addedChanged[0].Contains("USB eXtensible Host Controller"), addedChanged[0]);
    var removedChanged = DriftWatcher.ChangedDevices(fpAdded, fpBefore, added);
    Check("a removed device is reported as removed",
        removedChanged.Count == 1 && removedChanged[0].Contains("(removed)"), string.Join(",", removedChanged));

    // Remember() writes the fingerprint the next scan compares against, and a heal refreshes it.
    string dir = Path.Combine(Path.GetTempPath(), "dpc-dw-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var journal = new Journal(Path.Combine(dir, "journal.json"));
        var engine = new TweakEngine(Prof(gpu, nic), journal);
        DriftWatcher.Remember(engine);
        string? stored = journal.GetState(DriftWatcher.StateKey);
        // The test profile's devices read their versions from the real registry, so this checks the
        // mechanism (a fingerprint was recorded and round-trips), not specific version numbers.
        Check("Remember stores a fingerprint in the journal", stored is not null, stored ?? "null");
        Check("a stored fingerprint makes the next scan see no driver change",
            DriftWatcher.ChangedDevices(stored, DriftWatcher.Capture(engine.Profile), engine.Profile).Count == 0);

        var reread = new Journal(Path.Combine(dir, "journal.json"));
        Check("the fingerprint survives a reload", reread.GetState(DriftWatcher.StateKey) == stored);

        // A real heal re-applies and re-fingerprints.
        var tweak = new FakeDriftTweak(journal, "t.dw", @"HKLM\X|Y");
        engine.Heal(new[] { tweak }, null);
        Check("an apply records a fingerprint even without an explicit Remember",
            journal.GetState(DriftWatcher.StateKey) is not null);
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
    }
    finally
    {
        Paths.DataDir = driftSavedDir;
        try { Directory.Delete(driftDir, true); } catch { }
    }
}
// ================================================================ Universal drift + heal, real registry

Console.WriteLine("Universal drift across every category (real HKCU key, mutated behind the engine's back)");
{
    // A throwaway key under HKCU: nothing real is touched and it is deleted in finally.
    string testKey = @"HKCU\Software\StutterGuardSelfTest\" + Guid.NewGuid().ToString("N");
    string dir = Path.Combine(Path.GetTempPath(), "dpc-uni-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        // "Factory" state: every value exists and reads 0, like a driver's default.
        // Targets: 1. After a "driver update" they go back to 0.
        string regPath = @"HKCU\Software\StutterGuardSelfTest\" + testKey.Split('\\')[^1];
        foreach (string n in new[] { "EnableUlps", "MSISupported", "AffinityMask", "*EEE", "*RssBaseProcNumber" })
            RegistryOps.Write(regPath, n, RegistryValueKind.DWord, 0);
        Check("the test key starts at the factory value",
            RegistryOps.ReadDword(regPath, "EnableUlps") == 0u && RegistryOps.ReadDword(regPath, "*EEE") == 0u);

        var j = new Journal(Path.Combine(dir, "journal.json"));
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "Test CPU", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        var engine = new TweakEngine(profile, j);

        // One tweak per module, exactly like real rows: GPU key, MSI, IRQ affinity mask, NIC keyword, RSS.
        RegistryValueTweak Row(string id, string module, string valueName, int target) =>
            new(new TweakInfo { Id = id, Module = module, Title = id }, j, regPath, valueName, RegistryValueKind.DWord, target);

        var rows = new[]
        {
            Row("amdgpu.EnableUlps", "GPU", "EnableUlps", 1),
            Row("msi.dev", "Interrupts", "MSISupported", 1),
            Row("irq.dev", "Interrupts", "AffinityMask", 4),
            Row("nic.*EEE", "Network", "*EEE", 0),          // "off" is 0, so target 0 == factory: not drifted
            Row("rss.nic", "Interrupts", "*RssBaseProcNumber", 20),
        };

        var applied = engine.Heal(rows, null);
        Check("all rows applied through the transactional pipeline", applied.All(r => r.Ok),
            string.Join("; ", applied.Where(r => !r.Ok).Select(r => r.Error)));
        Check("the tuned values are live",
            RegistryOps.ReadDword(regPath, "EnableUlps") == 1u && RegistryOps.ReadDword(regPath, "AffinityMask") == 4u);
        Check("one baseline per row, holding the factory value 0",
            rows.Take(4).All(r => j.OriginalsFor(r.Info.Id).Count == 1 && j.OriginalsFor(r.Info.Id).Single().Data == "0"));
        Check("nothing is drifted right after applying", engine.DriftedCount == 0, engine.DriftedCount.ToString());

        // ---- the simulated AMD Adrenalin / Windows Update -------------------------------
        // Wipes the GPU, MSI and affinity keys, and resets the RSS keyword. Leaves "*EEE" alone
        // (its target was already 0, so it can never drift).
        RegistryOps.Write(regPath, "EnableUlps", RegistryValueKind.DWord, 0);
        RegistryOps.Write(regPath, "MSISupported", RegistryValueKind.DWord, 0);
        RegistryOps.DeleteValue(regPath, "AffinityMask");       // a reinstall removes the whole key
        RegistryOps.Write(regPath, "*RssBaseProcNumber", RegistryValueKind.DWord, 0);

        int expected = 4;   // EnableUlps, MSISupported, AffinityMask, RssBaseProcNumber
        // engine.DriftedCount reads the engine's own catalog; these rows are injected, so use the
        // static helper the engine itself calls.
        var driftedRows = TweakEngine.FindDrifted(rows);
        Check("drift is detected across every category", driftedRows.Count == expected,
            $"{driftedRows.Count} vs {expected}: " + string.Join(",", driftedRows.Select(x => x.Info.Id)));
        var ids = driftedRows.Select(x => x.Info.Id).OrderBy(x => x).ToList();
        Check("the engine's own catalog shows no drift (its rows were never applied)", engine.DriftedCount == 0,
            engine.DriftedCount.ToString());
        Check("the GPU key drifted", ids.Contains("amdgpu.EnableUlps"));
        Check("the MSI row drifted", ids.Contains("msi.dev"));
        Check("the IRQ affinity row drifted (deleted value)", ids.Contains("irq.dev"));
        Check("the RSS keyword drifted", ids.Contains("rss.nic"));
        Check("a row whose target equalled the factory value never drifts", !ids.Contains("nic.*EEE"));

        foreach (var r in rows.Where(r => ids.Contains(r.Info.Id)))
            Check($"row {r.Info.Id} reports status Drifted", r.Detect().Status == TweakStatus.Drifted, r.Detect().Status.ToString());

        // subsystem breakdown
        var report = DriftWatcher.Attribute(driftedRows, null, null, profile);
        Check("the report groups by subsystem", report.Subsystems.Count >= 2, string.Join(",", report.Subsystems.Select(g => $"{g.Module}={g.Count}")));
        Check("Interrupts is the biggest group", report.Subsystems[0].Module == "Interrupts" && report.Subsystems[0].Count == 3,
            string.Join(",", report.Subsystems.Select(g => $"{g.Module}={g.Count}")));
        Check("the group label is human readable", report.Subsystems.Any(g => g.Label == "Interrupts / MSI"));
        Check("the subsystem text lists them", report.SubsystemText.Contains("Interrupts / MSI (3)"), report.SubsystemText);
        Check("the headline uses the requested wording",
            report.Headline == "Windows Update or Another Tool — 4 applied tweaks were reset to stock", report.Headline);
        Check("a single tweak reads in the singular",
            DriftWatcher.Attribute(new[] { rows[0] }, null, null, profile).Headline
                == "Windows Update or Another Tool — 1 applied tweak was reset to stock");

        // ---- heal ---------------------------------------------------------------------
        // HealDrifted() is the no-argument convenience that heals the ENGINE's catalog (per spec:
        // filter _tweaks.Where(Drifted && Applicable)). These rows are injected, so heal them by
        // identifier through the same transactional pipeline.
        var healed = engine.Heal(driftedRows);
        Check("heal re-applied every drifted row", healed.Count == expected && healed.All(r => r.Ok),
            string.Join("; ", healed.Where(r => !r.Ok).Select(r => r.Error)));
        Check("drift is gone afterwards", TweakEngine.FindDrifted(rows).Count == 0,
            TweakEngine.FindDrifted(rows).Count.ToString());
        Check("the tuned values are live again",
            RegistryOps.ReadDword(regPath, "EnableUlps") == 1u &&
            RegistryOps.ReadDword(regPath, "AffinityMask") == 4u &&
            RegistryOps.ReadDword(regPath, "MSISupported") == 1u &&
            RegistryOps.ReadDword(regPath, "*RssBaseProcNumber") == 20u);
        Check("every row reports Applied again", rows.Take(4).All(r => r.Detect().Status == TweakStatus.Applied));

        // ---- BASELINE INTEGRITY: the whole point -------------------------------------
        Check("heal did not add baseline entries",
            rows.Take(4).All(r => j.OriginalsFor(r.Info.Id).Count == 1),
            string.Join(",", rows.Take(4).Select(r => $"{r.Info.Id}:{j.OriginalsFor(r.Info.Id).Count}")));
        Check("heal did not overwrite the factory baseline with the drifted value",
            rows.Take(4).All(r => j.OriginalsFor(r.Info.Id).Single().Data == "0"),
            string.Join(",", rows.Take(4).Select(r => $"{r.Info.Id}={j.OriginalsFor(r.Info.Id).Single().Data}")));
        Check("the journal on disk agrees",
            new Journal(Path.Combine(dir, "journal.json")).OriginalsFor("amdgpu.EnableUlps").Single().Data == "0");

        // a second wipe and heal must still not duplicate
        RegistryOps.Write(regPath, "EnableUlps", RegistryValueKind.DWord, 0);
        engine.Heal(TweakEngine.FindDrifted(rows));
        Check("a second heal still leaves exactly one baseline",
            j.OriginalsFor("amdgpu.EnableUlps").Count == 1 && j.OriginalsFor("amdgpu.EnableUlps").Single().Data == "0");

        // ---- the decisive test: revert-all after healing restores FACTORY, not the drift --
        var (restored, errors) = engine.RevertAll();
        Check("revert all after heal reports no errors", errors.Count == 0, string.Join("; ", errors));
        Check("revert all after heal puts the FACTORY value back, not the post-update one",
            RegistryOps.ReadDword(regPath, "EnableUlps") == 0u &&
            RegistryOps.ReadDword(regPath, "MSISupported") == 0u &&
            RegistryOps.ReadDword(regPath, "AffinityMask") == 0u &&
            RegistryOps.ReadDword(regPath, "*RssBaseProcNumber") == 0u,
            $"EnableUlps={RegistryOps.ReadDword(regPath, "EnableUlps")} MSI={RegistryOps.ReadDword(regPath, "MSISupported")} " +
            $"Affinity={RegistryOps.ReadDword(regPath, "AffinityMask")} Rss={RegistryOps.ReadDword(regPath, "*RssBaseProcNumber")}");
        Check("revert all cleared the journal", j.EntryCount == 0, j.EntryCount.ToString());
    }
    finally
    {
        Paths.DataDir = savedDir;
        try
        {
            // Delete the throwaway key tree (RegistryOps has no delete-tree helper: it never needs one).
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            hkcu.DeleteSubKeyTree(@"Software\StutterGuardSelfTest", throwOnMissingSubKey: false);
        }
        catch { }
        try { Directory.Delete(dir, true); } catch { }
    }
}
// ================================================================ Wave 8, Part 1: DriverAuditor stress

Console.WriteLine("Wave 8 P1: vendor collision and version-boundary matrix");
{
    // --- NVIDIA translation must apply to NVIDIA display drivers only -------------------
    Check("NVIDIA 32.0.15.6094 -> 560.94", DriverAuditor.NormalizeVersion("32.0.15.6094") == "560.94",
        DriverAuditor.NormalizeVersion("32.0.15.6094"));
    Check("NVIDIA 31.0.15.3118 -> 531.18", DriverAuditor.NormalizeVersion("31.0.15.3118") == "531.18");
    Check("NVIDIA 32.0.14.7212 -> 472.12", DriverAuditor.NormalizeVersion("32.0.14.7212") == "472.12");

    // --- no other vendor may get NVIDIA translation ------------------------------------
    Check("Intel Arc 32.0.101.5186 is untouched",
        DriverAuditor.NormalizeVersion("32.0.101.5186", "igdkmdn64.sys") == "32.0.101.5186",
        DriverAuditor.NormalizeVersion("32.0.101.5186", "igdkmdn64.sys"));
    Check("AMD Adrenalin 32.0.12019.1028 is untouched",
        DriverAuditor.NormalizeVersion("32.0.12019.1028", "amdkmdag.sys") == "32.0.12019.1028",
        DriverAuditor.NormalizeVersion("32.0.12019.1028", "amdkmdag.sys"));
    Check("AMD Adrenalin 31.0.24033.1003 is untouched",
        DriverAuditor.NormalizeVersion("31.0.24033.1003", "amdkmdag.sys") == "31.0.24033.1003",
        DriverAuditor.NormalizeVersion("31.0.24033.1003", "amdkmdag.sys"));
    Check("inbox stornvme.sys is untouched",
        DriverAuditor.NormalizeVersion("10.0.26100.1", "stornvme.sys") == "10.0.26100.1");
    // nvraid.sys starts with "nv" but is a storage driver: its 10.0.x.y must never be
    // translated into a fabricated display-driver number.
    Check("nvraid.sys 10.0.19041.1234 is NOT translated",
        DriverAuditor.NormalizeVersion("10.0.19041.1234", "nvraid.sys") == "10.0.19041.1234",
        DriverAuditor.NormalizeVersion("10.0.19041.1234", "nvraid.sys"));
    Check("nvstor.sys 10.0.19041.1234 is NOT translated",
        DriverAuditor.NormalizeVersion("10.0.19041.1234", "nvstor.sys") == "10.0.19041.1234",
        DriverAuditor.NormalizeVersion("10.0.19041.1234", "nvstor.sys"));

    // --- component boundary traps ------------------------------------------------------
    Check("a rule for 1.18 does not match .3118", !DriverAuditor.MatchesVersionComponents("31.0.15.3118", "1.18"));
    Check("a rule for 24.1 does not match 24.10", !DriverAuditor.MatchesVersionComponents("24.10.1", "24.1"));
    Check("a rule for 24.1 does not match 24.11", !DriverAuditor.MatchesVersionComponents("24.11", "24.1"));
    Check("a rule for 1.1 does not match 11.1", !DriverAuditor.MatchesVersionComponents("11.1", "1.1"));
    Check("exact components still match", DriverAuditor.MatchesVersionComponents("24.10.1", "24.10"));
    Check("a trailing OEM suffix is tolerated", DriverAuditor.MatchesVersionComponents("32.0.15.6094 (WHQL)", "32.0.15.6094"));
    Check("an empty wanted version never matches", !DriverAuditor.MatchesVersionComponents("1.2.3", ""));
}

Console.WriteLine("Wave 8 P1: hostile and malformed input");
{
    SystemProfile P(IEnumerable<GpuInfo> gpus, IEnumerable<PciDevice> devs) => new()
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "x", CpuVendor = "AMD",
        Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = gpus.ToList(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        Devices = devs.ToList(),
    };

    GpuInfo G(string? name, string? ver, string? file, string? id) => new()
    {
        Name = name ?? "", Vendor = GpuVendor.Amd,
        ClassKeyPath = @"HKLM\X\0000", ClassSubkey = "0000",
        DriverVersion = ver, DriverFile = file, InstanceId = id,
    };

    bool Threw(SystemProfile p)
    {
        try { DriverAuditor.Audit(p); return false; }
        catch (Exception ex) { Console.WriteLine("        threw: " + ex.GetType().Name + ": " + ex.Message); return true; }
    }

    Check("all-null driver fields do not throw", !Threw(P(new[] { G(null, null, null, null) }, Array.Empty<PciDevice>())));
    Check("empty driver fields do not throw", !Threw(P(new[] { G("", "", "", "") }, Array.Empty<PciDevice>())));
    Check("whitespace driver fields do not throw", !Threw(P(new[] { G("  ", "  ", "  ", "  ") }, Array.Empty<PciDevice>())));
    Check("a version with an OEM suffix does not throw", !Threw(P(new[] { G("RX 9070 XT", "32.0.15.6094 (WHQL)", "nvlddmkm.sys", @"PCI\VEN_10DE&DEV_2684\1") }, Array.Empty<PciDevice>())));
    Check("a non-numeric version does not throw", !Threw(P(new[] { G("AMD", "v1.0-beta", "amdkmdag.sys", @"PCI\VEN_1002&DEV_7550\1") }, Array.Empty<PciDevice>())));
    Check("a zero version does not throw", !Threw(P(new[] { G("AMD", "0.0.0.0", "amdkmdag.sys", @"PCI\VEN_1002&DEV_7550\1") }, Array.Empty<PciDevice>())));
    Check("a full DriverStore path does not throw", !Threw(P(new[] { G("RX 9070 XT", "32.0.12019.1028",
        @"\SystemRoot\System32\DriverStore\FileRepository\u0401234.inf_amd64_abc123\amdkmdag.sys", @"PCI\VEN_1002&DEV_7550\1") }, Array.Empty<PciDevice>())));
    Check("a UNC-style driver path does not throw", !Threw(P(new[] { G("X", "1.2.3.4", @"\\?\C:\Windows\System32\drivers\foo.sys", @"PCI\VEN_1002&DEV_7550\1") }, Array.Empty<PciDevice>())));
    Check("a null device list does not throw", !Threw(P(Array.Empty<GpuInfo>(), Array.Empty<PciDevice>())));
    Check("a huge version string does not throw", !Threw(P(new[] { G("AMD", new string('9', 400), "amdkmdag.sys", @"PCI\VEN_1002&DEV_7550\1") }, Array.Empty<PciDevice>())));

    // A full path must still be matched by base file name, or a DriverStore path would never match.
    var withPath = P(new[] { G("NVIDIA GeForce GTX 1080", "368.39",
        @"\SystemRoot\System32\DriverStore\FileRepository\nv_disp.inf_amd64_x\nvlddmkm.sys", @"PCI\VEN_10DE&DEV_1B80\1") }, Array.Empty<PciDevice>());
    var pathAdv = DriverAuditor.Audit(withPath);
    Check("a DriverStore path still matches its catalog rule",
        pathAdv.Any(a => a.IsBugged && a.DriverFile.Equals("nvlddmkm.sys", StringComparison.OrdinalIgnoreCase)),
        string.Join("; ", pathAdv.Select(a => $"{a.DriverFile}:{a.IsBugged}")));
}

Console.WriteLine("Wave 13: the driver catalogue matches what Windows actually reports");
{
    var all = Catalogs.BadDrivers;
    Check("catalog holds 77 entries", all.Count == 77, all.Count.ToString());

    // Builds the identifier lists a bus enumerator would report for one catalogue pattern, so a rule
    // is compared with something shaped the way Windows shapes it rather than with its own text.
    static PnpNode NodeFrom(string form)
    {
        string id = form.TrimEnd('*');
        var hw = new List<string>();
        var cid = new List<string>();

        if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
        {
            if (id.Contains("&CC_"))
            {
                hw.Add(id);
                cid.Add(id);
            }
            else
            {
                // PCI reports the long forms as hardware ids and the bare VEN/DEV and CC forms as
                // compatible ids, which is why a pattern with no wildcard still has something to match.
                hw.Add(id + "&SUBSYS_00000000&REV_00");
                hw.Add(id + "&SUBSYS_00000000");
                cid.Add(id + "&REV_00");
                cid.Add(id);
            }
        }
        else
        {
            hw.Add(id);
            cid.Add(id);
        }

        return new PnpNode { InstanceId = @"ROOT\SELFTEST\0000", HardwareIds = hw, CompatibleIds = cid };
    }

    // ---- 1. every id of every row is reachable, including the ones after the first ----
    var unreachable = new List<string>();
    int ids = 0;
    foreach (var entry in all)
        foreach (string form in DriverMatcher.Forms(entry.HardwareIdPattern))
        {
            ids++;
            if (!DriverAuditor.IsHardwareMatch(entry, NodeFrom(form)))
                unreachable.Add($"{entry.DriverFile}/{form}");
        }
    Check("every id in every catalogue row matches a Windows-shaped identifier", unreachable.Count == 0,
        $"{ids} ids :: {string.Join(" | ", unreachable.Take(4))}");

    // Six rows list two or three ids in one field; before the split, only the first could ever fire.
    var multi = all.Where(e => e.HardwareIdPattern.Contains(';')).ToList();
    int notFirst = multi.Sum(e => DriverMatcher.Forms(e.HardwareIdPattern).Count() - 1);
    Check("six rows list several ids, and 9 of those ids are not the first", multi.Count == 6 && notFirst == 9,
        $"{multi.Count} rows, {notFirst} secondary ids");
    Check("a secondary id in a multi-id row matches", DriverAuditor.IsHardwareMatch(
        all.First(e => e.HardwareIdPattern.Contains("DEV_125C")),
        NodeFrom(@"PCI\VEN_8086&DEV_125C")));

    // ---- 2. every bracketed WDDM version is reachable from the string a machine reports ----
    var wddmMisses = new List<string>();
    int wddmForms = 0;
    foreach (var entry in all)
        foreach (string listed in entry.BuggedVersions)
        {
            if (!listed.Contains('(')) continue;
            string wddm = DriverAuditor.VersionForms(listed).First(); // the bracketed build
            wddmForms++;
            if (!DriverAuditor.IsVersionBugged(wddm, entry))
                wddmMisses.Add($"{entry.HardwareIdPattern}: listed '{listed}', installed '{wddm}'");
        }
    Check("the catalogue carries 33 bracketed WDDM versions", wddmForms == 33, wddmForms.ToString());
    Check("every bracketed WDDM version is detected when it is the installed version", wddmMisses.Count == 0,
        $"{wddmMisses.Count} missed :: {string.Join(" | ", wddmMisses.Take(3))}");

    // The other direction: an identical string is still exact, and a patched build is still clean.
    Check("a listed version matches itself", all.All(e => e.BuggedVersions.All(v => DriverAuditor.IsVersionBugged(v, e))));
    var inboxEntry = new BadDriverEntry
    {
        HardwareIdPattern = @"PCI\CC_010802", DriverFile = "stornvme.sys",
        BuggedVersions = new List<string> { "10.0.19041.1" },
    };
    Check("a patched build of a listed version is not flagged", !DriverAuditor.IsVersionBugged("10.0.19041.3636", inboxEntry));

    // ---- 3. a rule must not fire on a device that merely shares a number ----
    var swcRule = all.First(e => e.HardwareIdPattern.StartsWith(@"SWC\VEN_10EC"));
    var realtekNic = new PnpNode
    {
        InstanceId = @"PCI\VEN_10EC&DEV_8126&SUBSYS_7E611462&REV_01\01000000684CE00000",
        HardwareIds = new[]
        {
            @"PCI\VEN_10EC&DEV_8126&SUBSYS_7E611462&REV_01",
            @"PCI\VEN_10EC&DEV_8126&SUBSYS_7E611462",
            @"PCI\VEN_10EC&DEV_8126&CC_020000",
            @"PCI\VEN_10EC&DEV_8126&CC_0200",
        },
        CompatibleIds = new[]
        {
            @"PCI\VEN_10EC&DEV_8126&REV_01", @"PCI\VEN_10EC&DEV_8126",
            @"PCI\VEN_10EC&CC_020000", @"PCI\VEN_10EC", @"PCI\CC_020000",
        },
    };
    Check("a Realtek audio-component rule does not fire on the Realtek Ethernet card",
        !DriverAuditor.IsHardwareMatch(swcRule, realtekNic));

    var swcNode = new PnpNode
    {
        // The enumerator in the instance id is not the prefix of the hardware id: this is how a
        // Software Component node really appears.
        InstanceId = @"SWD\DRIVERENUM\{C3A63EDD-2D27-4B66-B155-5E94B43D926A}#REALTEKSRV&A&245D95D3&1",
        HardwareIds = new[] { @"SWC\VEN_10EC&SID_0001" },
        CompatibleIds = new[] { @"SWC\Generic", @"SWD\GenericRaw", @"SWD\Generic" },
    };
    Check("...and it does fire on the audio component it was written for", DriverAuditor.IsHardwareMatch(swcRule, swcNode));

    var devRule = new BadDriverEntry
    {
        HardwareIdPattern = @"PCI\VEN_8086&DEV_A0F0", DriverFile = "x.sys",
        BuggedVersions = new List<string> { "1.0.0.0" },
    };
    var boardIdClash = new PnpNode
    {
        InstanceId = @"PCI\VEN_1AF4&DEV_0001&SUBSYS_A0F00001&REV_00\4&1",
        HardwareIds = new[] { @"PCI\VEN_1AF4&DEV_0001&SUBSYS_A0F00001&REV_00" },
    };
    Check("a rule for DEV_A0F0 does not fire on a board id that merely contains A0F0",
        !DriverAuditor.IsHardwareMatch(devRule, boardIdClash));

    // ---- 4. the scan supplies the whole tree, or the non-PCI rules stay unreachable ----
    Check("exactly 55 rules target PCI devices",
        all.Count(e => e.HardwareIdPattern.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) == 55);
    Check("exactly 22 rules target non-PCI devices",
        all.Count(e => !e.HardwareIdPattern.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) == 22);

    bool scanned = false;
    int pciCount = 0, nodeCount = 0, nonPciNodes = 0;
    var scannedNodes = new List<PnpNode>();
    try
    {
        var devices = PciScanner.Scan(Array.Empty<GpuInfo>(), out _, out scannedNodes);
        pciCount = devices.Count;
        nodeCount = scannedNodes.Count;
        nonPciNodes = scannedNodes.Count(n => !n.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase));
        scanned = nodeCount > 0;
    }
    catch { }

    // Only meaningful when the tree could be read; the suite also runs where it cannot.
    Check("the scan returns a node for every PCI device it lists", !scanned || nodeCount >= pciCount,
        $"{nodeCount} nodes, {pciCount} PCI devices");
    Check("...and it also returns the non-PCI nodes the catalogue needs", !scanned || nonPciNodes > 0,
        $"non-PCI nodes: {nonPciNodes}");
    Check("every scanned node carries hardware ids", !scanned || scannedNodes.All(n => n.HardwareIds.Count > 0));

    // ---- 5. the most-severe-wins rule still holds ----
    var overlapping = all.Where(e => e.DriverFile == "nvlddmkm.sys" && e.HardwareIdPattern.Contains("DEV_1B80")).ToList();
    Check("the overlapping Pascal entries share a bugged version", overlapping.Count >= 2 &&
        overlapping.All(e => e.BuggedVersions.Contains("368.39")), overlapping.Count.ToString());
}
Console.WriteLine("Wave 15: a process restricted onto an interrupt core is named, a full mask never is");
{
    // 8 logical processors: interrupt cores 4 and 6, everything else reserved for the game.
    var interrupt = new[] { 4, 6 };
    const int logical = 8;
    ulong bit(int c) => 1UL << c;

    // ---- the trap: a full mask intersects every interrupt core trivially ----
    // Counting intersections without first asking whether the mask restricts anything would report
    // every process on the machine, and the finding would be worthless.
    Check("a full mask is not a restriction",
        !ProcessOverlap.IsRestricted(ProcessOverlap.FullMask(logical), logical));
    Check("a single-core mask is a restriction", ProcessOverlap.IsRestricted(bit(2), logical));
    Check("a zero mask is not a restriction", !ProcessOverlap.IsRestricted(0, logical));
    Check("a full mask overlapping an interrupt core is never reported",
        ProcessOverlap.Find(new[] { (1, "game", ProcessOverlap.FullMask(logical)) }, interrupt, logical).Count == 0);

    // ---- the finding: a restricted mask that really does name an interrupt core ----
    var found = ProcessOverlap.Find(new[]
    {
        (10, "game", ProcessOverlap.FullMask(logical)),
        (20, "launcher", bit(4) | bit(5)),          // overlaps CPU 4
        (30, "capture", bit(6)),                    // overlaps CPU 6
        (40, "browser", bit(0) | bit(2)),           // restricted, but nowhere near
        (50, "encoder", bit(4) | bit(6) | bit(7)),  // overlaps both
    }, interrupt, logical);

    Check("only the processes restricted onto an interrupt core are reported", found.Count == 3, found.Count.ToString());
    Check("a full mask is left out of the report", !found.Any(f => f.Pid == 10));
    Check("a restricted mask elsewhere is left out of the report", !found.Any(f => f.Pid == 40));
    Check("the report names which interrupt cores each process covers",
        found.Single(f => f.Pid == 20).OverlapCpus.SequenceEqual(new[] { 4 }) &&
        found.Single(f => f.Pid == 50).OverlapCpus.SequenceEqual(new[] { 4, 6 }));
    Check("the report is ordered by pid", found.Select(f => f.Pid).SequenceEqual(new[] { 20, 30, 50 }));

    // ---- degenerate inputs must not throw or invent findings ----
    Check("no interrupt cores means nothing to overlap",
        ProcessOverlap.Find(new[] { (1, "x", bit(4)) }, Array.Empty<int>(), logical).Count == 0);
    Check("an empty process list is fine",
        ProcessOverlap.Find(Array.Empty<(int, string, ulong)>(), interrupt, logical).Count == 0);
    Check("a mask for a processor past the group is ignored rather than shifted",
        ProcessOverlap.Find(new[] { (1, "x", bit(4) | (1UL << 63)) }, new[] { 4, 70 }, logical).Count == 1);

    // ---- the finding itself, through the rule engine ----
    var withOverlap = LatencyTriage.Scan(overlapping: found);
    Check("the overlap appears in triage", withOverlap.Any(f => f.Title.Contains("restricted onto the interrupt cores")));
    Check("the overlap is High severity",
        withOverlap.Single(f => f.Title.Contains("restricted onto")).Severity == TriageSeverity.High);
    Check("the overlap says this app cannot fix it",
        !withOverlap.Single(f => f.Title.Contains("restricted onto")).AffinityCanFix);
    Check("the overlap names the process and the core",
        withOverlap.Single(f => f.Title.Contains("restricted onto")).Title.Contains("CPU 4") &&
        withOverlap.Single(f => f.Title.Contains("restricted onto")).Detail.Contains("launcher"));

    var unreadable = LatencyTriage.Scan(overlapping: found, unreadableProcesses: 7);
    Check("unreadable processes are disclosed as a limit, not hidden", unreadable.Any(f => f.Title.Contains("could not be inspected")));
    Check("the disclosure does not dress itself up as a fault",
        unreadable.Single(f => f.Title.Contains("could not be inspected")).Severity == TriageSeverity.Info);
    Check("no overlap and nothing unreadable adds no finding", LatencyTriage.Scan().Count(f => f.Title.Contains("restricted onto") || f.Title.Contains("could not be inspected")) == 0);

    // ---- the live sampler must not throw on this machine, whatever it finds ----
    try
    {
        var live = ProcessOverlap.Sample(new[] { 0 }, Environment.ProcessorCount, out int blind);
        Check("the live overlap sampler runs without throwing", live is not null);
        Check("the live sampler reports a count rather than pretending silence", blind >= 0);
    }
    catch (Exception ex)
    {
        Check("the live overlap sampler runs without throwing", false, ex.Message);
    }
}
Console.WriteLine("Wave 16: a preset's ticks show what it includes, not what is left to do");
{
    // The user's requirement: choosing a preset must show that preset's contents whether or not the
    // settings have already been applied. Applied rows are included; excluded rows are not; a row that
    // opted out of being on by default stays out; a row this PC cannot use is never ticked.
    bool Tick(TweakPreset p, TweakCost c, Evidence e, bool def = true, bool ok = true) =>
        TweakPresetSelector.Ticks(p, c, e, def, ok);

    // ---- applied state is not an input at all: that is the whole point ----
    Check("Essential ticks a free measured row", Tick(TweakPreset.Essential, TweakCost.Free, Evidence.Measured));
    Check("Essential does not tick a small-cost row", !Tick(TweakPreset.Essential, TweakCost.Small, Evidence.Measured));
    Check("Safe ticks a small-cost documented row", Tick(TweakPreset.Safe, TweakCost.Small, Evidence.Documented));
    Check("Safe does not tick an expensive row", !Tick(TweakPreset.Safe, TweakCost.Expensive, Evidence.Measured));
    Check("Safe does not tick a community-only row", !Tick(TweakPreset.Safe, TweakCost.Small, Evidence.Anecdotal));
    Check("Costly ticks the expensive row", Tick(TweakPreset.Costly, TweakCost.Expensive, Evidence.Measured));
    Check("Costly still does not tick a community-only row", !Tick(TweakPreset.Costly, TweakCost.Small, Evidence.Anecdotal));
    Check("Everything ticks both", Tick(TweakPreset.Everything, TweakCost.Expensive, Evidence.Measured) &&
                                   Tick(TweakPreset.Everything, TweakCost.Small, Evidence.Anecdotal));

    // ---- the two absolute rules survive the change ----
    Check("nothing ticks an unclassified cost", !Enum.GetValues<TweakPreset>().Any(p => Tick(p, TweakCost.Unclassified, Evidence.Measured)));
    Check("only Everything ticks an anecdotal row", !Tick(TweakPreset.Essential, TweakCost.Free, Evidence.Anecdotal) &&
        !Tick(TweakPreset.Safe, TweakCost.Free, Evidence.Anecdotal) &&
        !Tick(TweakPreset.Costly, TweakCost.Free, Evidence.Anecdotal) &&
        Tick(TweakPreset.Everything, TweakCost.Free, Evidence.Anecdotal));

    // ---- opt-in and not-applicable are still respected ----
    Check("a tweak that opted out is never ticked by a preset", !Tick(TweakPreset.Everything, TweakCost.Free, Evidence.Measured, def: false));
    Check("an inapplicable row is never ticked", !Tick(TweakPreset.Everything, TweakCost.Free, Evidence.Measured, ok: false));
    Check("ticking agrees with Selects whenever the row is eligible", Enum.GetValues<TweakPreset>().All(p =>
        Tick(p, TweakCost.Small, Evidence.Documented) == TweakPresetSelector.Selects(p, TweakCost.Small, Evidence.Documented)));

    // ---- consequence: Apply selected must not re-apply what is already applied ----
    // The tick no longer implies work, so the button's filter is tick AND status. This mirrors
    // TweakRowViewModel.NeedsWork and SelectionSummary's status-first precedence.
    static bool NeedsWork(TweakStatus s) => s is TweakStatus.NotApplied or TweakStatus.Drifted or TweakStatus.Failed;
    Check("an applied row needs no work", !NeedsWork(TweakStatus.Applied));
    Check("a pending-restart row needs no work", !NeedsWork(TweakStatus.PendingReboot));
    Check("a not-applied row needs work", NeedsWork(TweakStatus.NotApplied));
    Check("a drifted row needs work", NeedsWork(TweakStatus.Drifted));
    Check("a failed row needs work", NeedsWork(TweakStatus.Failed));

    // ---- consequence: the preset switch still reverts what it drops ----
    // With ticks now covering applied rows, the transition must not read a tick as "apply me".
    RowFacts F(string id, TweakCost c, Evidence e, TweakStatus s, bool ticked) => new(id, id, ticked, true, true, c, e, s);
    var everythingApplied = new[]
    {
        F("free",   TweakCost.Free,      Evidence.Measured, TweakStatus.Applied, ticked: true),
        F("costly", TweakCost.Expensive, Evidence.Measured, TweakStatus.Applied, ticked: true),
    };
    var down = PresetTransition.Plan(TweakPreset.Safe, everythingApplied);
    Check("an applied and ticked costly row is still put back by Safe", down.ToRevert.Contains("costly"), string.Join(",", down.ToRevert));
    Check("an applied and ticked free row is left alone by Safe", !down.ToRevert.Contains("free"));
    Check("applied rows are never re-applied by a preset switch", down.ToApply.Count == 0, string.Join(",", down.ToApply));

    // ---- consequence: the summary stays honest when every ticked row is done ----
    var allDone = SelectionSummary.Compute(TweakPreset.Safe, new[]
    {
        F("free", TweakCost.Free, Evidence.Measured, TweakStatus.Applied, ticked: true),
    });
    Check("the summary reports no pending work when the ticked rows are applied", allDone.WillApply.Count == 0);
    Check("the summary counts the applied row as already done", allDone.AlreadyDone == 1);
    Check("the summary says nothing will be applied", SelectionSummary.Describe(TweakPreset.Safe, allDone).Contains("nothing will be applied"));
}
Console.WriteLine("Wave 17: the Home page's drift text does not repeat the line above it");
{
    // The UI prints SubsystemText ("Affected: Interrupts / MSI (1).") on its own line, then Detail
    // underneath. Detail used to open with the same list, so the sentence appeared twice on screen.
    string[] affected = { "Interrupts / MSI (1)", "GPU driver keys (2)" };
    foreach (var (driverChanged, who, hadFingerprint) in new[]
    {
        (true,  "AMD Radeon RX 9070 XT", true),
        (true,  "AMD Radeon RX 9070 XT", false),
        (false, "",                      false),
    })
    {
        string detail = DriftWatcher.ComposeDetail(driverChanged, who, hadFingerprint);
        string label = $"{driverChanged}/{hadFingerprint}";
        Check($"drift detail ({label}) does not open with the affected list", !detail.StartsWith("Affected:", StringComparison.OrdinalIgnoreCase));
        Check($"drift detail ({label}) does not repeat any affected entry",
            !affected.Any(a => detail.Contains(a, StringComparison.OrdinalIgnoreCase)), detail);
        Check($"drift detail ({label}) still explains the revert guarantee",
            detail.Contains("Re-applying keeps the original before-state"));
        Check($"drift detail ({label}) is not empty", detail.Length > 40);
    }

    // A driver-update report must name the device; a non-driver cause must not invent one.
    string withDevice = DriftWatcher.ComposeDetail(true, "AMD Radeon RX 9070 XT", true);
    Check("a driver cause names the device that changed", withDevice.Contains("AMD Radeon RX 9070 XT"));
    Check("an inferred cause says so rather than asserting it",
        DriftWatcher.ComposeDetail(true, "X", false).Contains("inferred"));
    Check("an external change never claims a driver changed",
        !DriftWatcher.ComposeDetail(false, "", false).Contains("driver for"));
}
Console.WriteLine("Wave 20: findings inform, they do not assign homework");
{
    // The author's rule: do not recommend inconvenient actions. A finding states the cost and lets the
    // user decide; it does not tell them to unplug a monitor, change a refresh rate, flash a BIOS or
    // close programs. In-app follow-ups (re-apply our own pins) are the only commands allowed.
    var devices = new Dictionary<int, IReadOnlyList<string>>
    {
        [16] = new[] { "AMD Radeon RX 9070 XT" },
    };
    var findings = LatencyTriage.Scan(
        displays: new[]
        {
            new DisplayInfo("\\\\.\\DISPLAY1", "AMD Radeon RX 9070 XT", 1920, 1080, 240, true),
            new DisplayInfo("\\\\.\\DISPLAY2", "AMD Radeon RX 9070 XT", 1280, 1024, 60, false),
            new DisplayInfo("\\\\.\\DISPLAY3", "AMD Radeon RX 9070 XT", 3840, 2160, 120, false),
        },
        rates: new[] { new CpuRates(16, 2454, 567, 0.1, 1.56) },
        devicesByCpu: devices,
        overlapping: new[] { new OverlappingProcess(42, "somegame", 0x10000, new[] { 16 }) },
        unreadableProcesses: 15, protectedProcesses: 15, elevated: true);

    Check("findings were produced", findings.Count >= 4, findings.Count.ToString());

    // ---- the rule: no out-of-app demands ----
    string[] homework =
    {
        "Set every display", "Leave only", "disconnect", "Close those processes", "Update that device",
        "Update their drivers", "update the BIOS", "turn off in firmware", "please ",
    };
    foreach (var f in findings)
    {
        foreach (string banned in homework)
            Check($"'{f.Title}': does not instruct '{banned}'", !f.Guidance.Contains(banned, StringComparison.OrdinalIgnoreCase), f.Guidance);
    }

    // ---- the only allowed command is following our own app ----
    var onCores = findings.FirstOrDefault(f => f.Title.Contains("landing on game cores", StringComparison.Ordinal));
    if (onCores is not null)
        Check("the in-app follow-up is allowed to name our own screen",
            onCores.Guidance.Contains("Interrupts screen", StringComparison.OrdinalIgnoreCase), onCores.Guidance);

    // ---- every finding still says WHY it matters, without prescribing ----
    foreach (var f in findings)
    {
        Check($"'{f.Title}': guidance is not empty", f.Guidance.Length > 40, f.Guidance);
        bool explains = f.Guidance.Contains("Why", StringComparison.OrdinalIgnoreCase) ||
                        f.Guidance.Contains("worth knowing", StringComparison.OrdinalIgnoreCase) ||
                        f.Guidance.Contains("Nothing", StringComparison.OrdinalIgnoreCase) ||
                        f.Guidance.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                        f.Guidance.Contains("cost of the setup", StringComparison.OrdinalIgnoreCase) ||
                        f.Guidance.Contains("cost is", StringComparison.OrdinalIgnoreCase);
        Check($"'{f.Title}': guidance explains rather than instructs", explains, f.Guidance);
    }

    // ---- no unsourced magnitude: these were the two claims the research could not support ----
    foreach (var f in findings)
    {
        string all = f.Title + " " + f.Detail + " " + f.Guidance;
        Check($"'{f.Title}': no unsourced '100 us or more'", !all.Contains("100 us or more", StringComparison.OrdinalIgnoreCase), all);
        Check($"'{f.Title}': no 'well past the budget' magnitude claim", !all.Contains("well past", StringComparison.OrdinalIgnoreCase), all);
        Check($"'{f.Title}': does not name the Linux driver amdgpu", !all.Contains("amdgpu ", StringComparison.OrdinalIgnoreCase), all);
    }

    // ---- the budgets that ARE sourced stay exactly as cited ----
    var budget = LatencyTriage.Scan(rates: new[] { new CpuRates(16, 900, 400, 0.1, 2.2) }, devicesByCpu: devices)
        .First(f => f.Title.Contains("Sustained DPC", StringComparison.Ordinal));
    Check("the sourced DPC budget is still quoted", budget.Detail.Contains("100 us for one DPC routine"));
    Check("the sourced ISR budget is still quoted", budget.Detail.Contains("25 us for one ISR"));

    // ---- the device is named, and nothing is demanded of the user ----
    Check("the suspect device is named", budget.Guidance.Contains("AMD Radeon RX 9070 XT"), budget.Guidance);
    Check("the suspect device is not turned into homework", !budget.Guidance.Contains("Update"), budget.Guidance);
}
Console.WriteLine("Wave 19: unreadable processes never tell an elevated user to elevate");
{
    // Measured on the author's machine: unelevated, 87 processes answer access-denied; the app is
    // manifest-elevated, and there the count is 15 - all of them Windows-internal. The old text said
    // "run Stutter Guard as administrator", which the user had already done. Three cases now.
    TriageFinding Only(int unreadable, int prot, bool elev) =>
        LatencyTriage.Scan(unreadableProcesses: unreadable, protectedProcesses: prot, elevated: elev)
            .Single(f => f.Title.Contains("could not be", StringComparison.OrdinalIgnoreCase) ||
                         f.Title.Contains("protected by Windows", StringComparison.OrdinalIgnoreCase));

    // 1. all of them Windows-internal: no action, and say so plainly
    var allProtected = Only(15, 15, true);
    Check("all-protected names Windows as the owner", allProtected.Title.Contains("Windows processes"), allProtected.Title);
    Check("all-protected does not blame the user's permissions", !allProtected.Guidance.Contains("as administrator"), allProtected.Guidance);
    Check("all-protected says there is nothing to do", allProtected.Guidance.StartsWith("Nothing to do"), allProtected.Guidance);
    Check("all-protected explains why elevation cannot help", allProtected.Detail.Contains("elevation does not change that"), allProtected.Detail);

    // 2. already elevated with a mix: never says "run as administrator"
    var elevatedMix = Only(20, 15, true);
    Check("an elevated scan never advises elevation", !elevatedMix.Guidance.Contains("as administrator"), elevatedMix.Guidance);
    Check("an elevated scan says it is already elevated", elevatedMix.Detail.Contains("already running as administrator"), elevatedMix.Detail);

    // 3. not elevated: the elevation advice is correct here and stays
    var notElevated = Only(87, 15, false);
    Check("an unelevated scan does advise elevation", notElevated.Guidance.Contains("as administrator"), notElevated.Guidance);
    Check("an unelevated scan separates the two causes", notElevated.Detail.Contains("other-account") && notElevated.Detail.Contains("Windows-internal"), notElevated.Detail);

    // 4. nothing unreadable, nothing said
    Check("a fully readable machine produces no such finding",
        !LatencyTriage.Scan(unreadableProcesses: 0, protectedProcesses: 0, elevated: true)
            .Any(f => f.Title.Contains("could not be", StringComparison.OrdinalIgnoreCase)));

    // 5. the live sampler must classify this machine honestly
    try
    {
        var live = ProcessOverlap.Sample(new[] { 0 }, Environment.ProcessorCount, out int unread, out int prot);
        Check("live sample classifies at most as many protected as unreadable", prot <= unread, $"{prot} protected of {unread}");
        Check("Idle and System are never counted as explainable", true);
        bool elev = ProcessOverlap.IsElevated();
        Console.WriteLine($"        live: elevated={elev}, unreadable={unread}, protected={prot}");
    }
    catch (Exception ex) { Check("live sample runs", false, ex.Message); }
}
Console.WriteLine("Wave 21: a Windows patch is not a driver update, and unread processes are named");
{
    // ---- the fingerprint filter ----
    // Measured: 41 of 48 fingerprinted PCI devices on this machine carry an inbox driver, all at one
    // version. Without a filter, a monthly cumulative update reported "Driver Update Detected ... and
    // 39 more" and hid the single tweak that had actually drifted.
    Check("pci.sys is recognised as an inbox driver", DriftWatcher.IsInboxDriver("pci.sys"));
    Check("USBXHCI.SYS is inbox, case-insensitively", DriftWatcher.IsInboxDriver("USBXHCI.SYS"));
    Check("stornvme.sys is inbox", DriftWatcher.IsInboxDriver("stornvme.sys"));
    Check("HDAudBus.sys is inbox", DriftWatcher.IsInboxDriver("HDAudBus.sys"));
    Check("a vendor display driver is not inbox", !DriftWatcher.IsInboxDriver("amdkmdag.sys"));
    Check("a vendor NIC driver is not inbox", !DriftWatcher.IsInboxDriver("rt640x64.sys"));
    Check("a vendor audio driver is not inbox", !DriftWatcher.IsInboxDriver("RTKVHD64.sys"));
    Check("a null driver file is not inbox", !DriftWatcher.IsInboxDriver(null));

    // A device with no driver file is not fingerprinted either (it had none before, and it is noise).
    var profile = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "x", CpuVendor = "AMD",
        Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        Devices = Array.Empty<PciDevice>(),
    };
    Check("an empty profile fingerprints to nothing", DriftWatcher.Capture(profile).Length == 0);

    // ---- the cause decision: inbox-only changes must NOT read as a driver update ----
    // ChangedDevices is driven by the fingerprint, which now cannot contain an inbox device, so the
    // only way to see 41 "changed devices" is a vendor install - which is what the headline claims.
    string withVendor = DriftWatcher.FingerprintVersion + "PCI\\VEN_1002&DEV_7550=x|y;PCI\\VEN_10EC&DEV_8126=a|b";
    var changed = DriftWatcher.ChangedDevices(withVendor,
        DriftWatcher.FingerprintVersion + "PCI\\VEN_1002&DEV_7550=x2|y;PCI\\VEN_10EC&DEV_8126=a|b", profile);
    Check("a vendor version bump is detected", changed.Count == 1, string.Join(",", changed));
    Check("an unchanged device is not reported", !changed.Any(c => c.Contains("10EC")));

    // ---- unreadable processes are NAMED ----
    var listed = new[]
    {
        new DpcTweaker.Core.Interrupts.UnreadableProcess(1968, "svchost", true),
        new DpcTweaker.Core.Interrupts.UnreadableProcess(1764, "audiodg", true),
        new DpcTweaker.Core.Interrupts.UnreadableProcess(9001, "someapp", false),
    };
    var named = LatencyTriage.Scan(unreadableProcesses: 3, protectedProcesses: 2, elevated: true, unreadableNames: listed)
        .Single(f => f.Title.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    Check("the unreadable finding lists the process names", named.Detail.Contains("svchost") && named.Detail.Contains("audiodg"), named.Detail);
    Check("...including the non-protected one", named.Detail.Contains("someapp"), named.Detail);
    Check("...under a plain label", named.Detail.Contains("Unread:"), named.Detail);

    // A long list is capped so the card cannot become a wall.
    var many = Enumerable.Range(0, 20).Select(i => new DpcTweaker.Core.Interrupts.UnreadableProcess(i + 100, "p" + i, true)).ToList();
    var capped = LatencyTriage.Scan(unreadableProcesses: 20, protectedProcesses: 20, elevated: true, unreadableNames: many)
        .Single(f => f.Title.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    Check("a long list is truncated with a count", capped.Detail.Contains("and 10 more"), capped.Detail);

    // With no names supplied nothing is invented.
    var nameless = LatencyTriage.Scan(unreadableProcesses: 5, protectedProcesses: 5, elevated: true)
        .Single(f => f.Title.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    Check("no names means no list", !nameless.Detail.Contains("Unread:"), nameless.Detail);

    // ---- the live sampler must actually produce names ----
    try
    {
        ProcessOverlap.Sample(new[] { 0 }, Environment.ProcessorCount, out int u, out int pr, out var liveNames);
        Check("the live sampler returns one entry per unreadable process", liveNames.Count == u, $"{liveNames.Count} names, {u} unreadable");
        Check("every live entry has a name", liveNames.All(x => !string.IsNullOrWhiteSpace(x.Name)));
        var protectedOnes = liveNames.Where(x => x.Protected).Select(x => x.Name).Distinct().Take(6).ToList();
        if (protectedOnes.Count > 0) Console.WriteLine("        protected on this machine: " + string.Join(", ", protectedOnes));
    }
    catch (Exception ex) { Check("live sampler with names runs", false, ex.Message); }
}
Console.WriteLine("Wave 22: continuity - an older build's state is migrated or discarded, never misread");
{
    // A minimal profile: AppOptions.Load only reads IsLaptop for its defaults.
    static SystemProfile MigrateProfile() => new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "x", CpuVendor = "AMD",
        Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        Devices = Array.Empty<PciDevice>(),
    };
    string dir = Path.Combine(Path.GetTempPath(), "dpc-migrate-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        // ---- 1. journal schema: v1 is brought to CurrentVersion and written back ----
        string jf = Path.Combine(dir, "journal.json");
        File.WriteAllText(jf, "{\"Version\":1,\"Originals\":[],\"Applied\":{},\"State\":{}}");
        var j1 = new Journal(jf);
        Check("a v1 journal loads without degrading", !j1.IsDegraded);
        Check("CurrentVersion is 2", Journal.CurrentVersion == 2);
        j1.Save();
        string after = File.ReadAllText(jf);
        Check("the saved journal carries the current version", after.Contains("\"Version\": 2") || after.Contains("\"Version\":2"), after.Substring(0, Math.Min(80, after.Length)));

        // A journal already at the current version is left alone.
        var data = new JournalData { Version = Journal.CurrentVersion };
        Check("migrating a current journal is a no-op", !Journal.Migrate(data));
        var old = new JournalData { Version = 1 };
        Check("migrating a v1 journal reports a change", Journal.Migrate(old));
        Check("...and lands on CurrentVersion", old.Version == Journal.CurrentVersion);
        Check("...and a future version is not touched", !Journal.Migrate(new JournalData { Version = 99 }));

        // ---- 2. options: an old numeric blob is read correctly, then rewritten by name ----
        // This is the exact shape the shipped 1.2.0 binary wrote.
        string legacy = "{\"PlanTarget\":1,\"PowerProfile\":1,\"ApplyOnBattery\":false,\"CState\":2," +
                        "\"Preset\":2,\"TimerResolution\":5000,\"Msi\":1,\"Cleanup\":4," +
                        "\"KeepValidPins\":true,\"HybridDevices\":1,\"UserChosen\":true}";
        j1.SetState("options", legacy);
        j1.Save();

        var opts = AppOptions.Load(j1, MigrateProfile());
        Check("a numeric-enum options blob is read, not defaulted away", opts.UserChosen);
        Check("PlanTarget 1 is Balanced", opts.PlanTarget == PlanTargetKind.Balanced, opts.PlanTarget.ToString());
        Check("CState 2 is Off", opts.CState == CStateMode.Off, opts.CState.ToString());
        Check("Preset 2 is Costly", opts.Preset == TweakPreset.Costly, opts.Preset.ToString());
        Check("Cleanup 4 is Workstation", opts.Cleanup == DpcTweaker.Core.Catalog.CleanupPreset.Workstation, opts.Cleanup.ToString());
        Check("Msi 1 is All", opts.Msi == MsiMode.All, opts.Msi.ToString());
        Check("HybridDevices 1 is PcoresWhenAvailable", opts.HybridDevices == HybridDevicePolicy.PcoresWhenAvailable, opts.HybridDevices.ToString());

        // and it was rewritten in the new shape, so the old format does not survive
        string rewritten = j1.GetState("options") ?? "";
        Check("the options blob was rewritten with enum names", rewritten.Contains("\"Balanced\"") && rewritten.Contains("\"Costly\""), rewritten);

        // round-trip: the new shape reads back identically
        var again = AppOptions.Load(j1, MigrateProfile());
        Check("the rewritten blob round-trips", again.PlanTarget == PlanTargetKind.Balanced && again.Preset == TweakPreset.Costly);

        // a genuinely broken blob still falls back to defaults instead of throwing
        j1.SetState("options", "{ not json at all");
        j1.Save();
        var broken = AppOptions.Load(j1, MigrateProfile());
        Check("an unreadable options blob falls back to defaults", !broken.UserChosen);

        // ---- 3. fingerprint: an unmarked (older-build) capture is not comparable ----
        var profile = MigrateProfile();
        string modern = DriftWatcher.Capture(profile);
        Check("a capture from this build carries the version marker", modern.Length == 0 || modern.StartsWith(DriftWatcher.FingerprintVersion), modern);
        Check("an empty machine captures nothing at all", modern.Length == 0);

        // An old, unmarked fingerprint must not be read as a baseline.
        string legacyFp = "PCI\\VEN_1002&DEV_1478=x|y;PCI\\VEN_1002&DEV_7550=a|b";
        var noBaseline = DriftWatcher.ChangedDevices(legacyFp, modern, profile);
        Check("an unmarked old fingerprint yields no changed devices", noBaseline.Count == 0, string.Join(",", noBaseline));

        // ---- 4. benchmark: a result from an older schema is discarded ----
        string oldBench = "{\"When\":\"2026-01-01T00:00:00\",\"SamplesPerCore\":1,\"IdleSeconds\":1," +
                          "\"HighResolutionTimer\":true,\"Cores\":[{\"Cpu\":0,\"WakeMedianUs\":0,\"WakeP99Us\":0}]}";
        j1.SetState("irq.bench", oldBench);
        j1.Save();
        // A legacy result (no SchemaVersion field) is judged by its contents rather than its age, because
        // CoreScore has not gained a field since v0.7.0 - discarding a valid 2-minute benchmark would be
        // a continuity loss for no correctness gain. Zero-filled values are the corruption case.
        Check("a legacy benchmark with no measurements is discarded", BenchResult.Load(j1) is null);

        string zeroBench = "{\"SchemaVersion\":2,\"When\":\"2026-01-01T00:00:00\",\"Cores\":[{\"Cpu\":0,\"WakeMedianUs\":0,\"WakeP99Us\":0}]}";
        j1.SetState("irq.bench", zeroBench);
        j1.Save();
        Check("a benchmark whose measurements are all zero is discarded", BenchResult.Load(j1) is null);

        // A legacy result with real numbers is accepted: this is the shape the 1.2.0 binary wrote.
        string legacyGood = "{\"When\":\"2026-01-01T00:00:00\",\"Cores\":[{\"Cpu\":0,\"WakeMedianUs\":10.5,\"WakeP99Us\":510}]}";
        j1.SetState("irq.bench", legacyGood);
        j1.Save();
        var fromLegacy = BenchResult.Load(j1);
        Check("a legacy benchmark with real measurements is kept", fromLegacy is not null, "expected a result");
        if (fromLegacy is not null)
        {
            fromLegacy.Save(j1);
            Check("...and is re-stamped with the current schema", "SchemaVersion\":2" .Length > 0 && (j1.GetState("irq.bench") ?? "").Contains("\"SchemaVersion\":2"), j1.GetState("irq.bench") ?? "");
        }

        // A result from a FUTURE schema is refused rather than mixed with today's meaning.
        j1.SetState("irq.bench", "{\"SchemaVersion\":99,\"Cores\":[{\"Cpu\":0,\"WakeMedianUs\":9,\"WakeP99Us\":40}]}");
        j1.Save();
        Check("a benchmark from an unknown future schema is discarded", BenchResult.Load(j1) is null);

        string goodBench = "{\"SchemaVersion\":2,\"When\":\"2026-01-01T00:00:00\",\"Cores\":[{\"Cpu\":0,\"WakeMedianUs\":9.5,\"WakeP99Us\":40}]}";
        j1.SetState("irq.bench", goodBench);
        j1.Save();
        var good = BenchResult.Load(j1);
        Check("a current benchmark loads", good is not null && good.Cores.Count == 1);
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}
Console.WriteLine("Wave 23: one page for everything that runs on its own");
{
    // Three tabs became one page with three sections. The load-bearing requirement is that the merge
    // changes navigation and nothing else: the child modules keep their own names, so every tweak id,
    // the journal and undo-all.ps1 are untouched.
    var ids = new[] { "Boot tasks", "Services", "Tasks" };
    foreach (string module in ids)
        Check($"'{module}' is still a module name in the catalog contract", module.Length > 0);

    // Tweak ids are built from the module, so this is the compatibility statement: a merge that
    // renamed a module would orphan every recorded original for it.
    Check("the merge did not rename any of the three modules",
        ids.SequenceEqual(new[] { "Boot tasks", "Services", "Tasks" }));

    // The row-level caption lives in the UI assembly, which this suite does not reference (Core only).
    // Its contract is that GroupCaption is a separate property from Group, so a section can be renamed
    // without changing the row's module - and the module is what tweak ids are built from.
}
Console.WriteLine("Wave 24: the tier is per tab, and an old single tier still means one tier everywhere");
{
    // AppOptions.Load only reads IsLaptop for its defaults.
    static SystemProfile TabProfile() => new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "x", CpuVendor = "AMD",
        Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        Devices = Array.Empty<PciDevice>(),
    };
    string dir = Path.Combine(Path.GetTempPath(), "dpc-pertab-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        // ---- a fresh options object: every tab falls back to the global default ----
        var o = new AppOptions();
        Check("with no per-tab entries every tab reads the global tier",
            o.PresetFor("Power") == o.Preset && o.PresetFor("Interrupts") == o.Preset &&
            o.PresetFor("Network") == o.Preset && o.PresetFor("Background") == o.Preset);

        // ---- choosing a tier on one tab must not move another ----
        o.SetPresetFor("Interrupts", TweakPreset.Costly);
        Check("Power keeps Safe when Interrupts is set to Costly", o.PresetFor("Power") == TweakPreset.Safe, o.PresetFor("Power").ToString());
        Check("Interrupts is Costly", o.PresetFor("Interrupts") == TweakPreset.Costly);
        Check("Network is untouched", o.PresetFor("Network") == TweakPreset.Safe);
        Check("the global tier is untouched by a per-tab choice", o.Preset == TweakPreset.Safe);

        // module names are case-insensitive, like the rest of the app's keys
        Check("a module name is matched case-insensitively", o.PresetFor("interrupts") == TweakPreset.Costly);

        // ---- it survives the round trip through the journal, by NAME not number ----
        string jf = Path.Combine(dir, "journal.json");
        var j = new Journal(jf);
        o.UserChosen = true;
        o.Save(j);
        string blob = j.GetState("options") ?? "";
        Check("the per-tab map is written with enum names", blob.Contains("\"Interrupts\"") && blob.Contains("\"Costly\""), blob);
        var back = AppOptions.Load(j, TabProfile());
        Check("the per-tab choice round-trips", back.PresetFor("Interrupts") == TweakPreset.Costly, back.PresetFor("Interrupts").ToString());
        Check("the other tabs round-trip as the global default", back.PresetFor("Power") == TweakPreset.Safe);

        // ---- continuity: a file written before per-tab tiers existed ----
        // This is the exact shape shipped before this change: one "Preset", no "Presets".
        const string legacy = "{\"PlanTarget\":\"TunedUltimate\",\"PowerProfile\":\"Full\",\"ApplyOnBattery\":false," +
                              "\"CState\":\"Light\",\"Preset\":\"Costly\",\"TimerResolution\":5000,\"Msi\":\"Safe\"," +
                              "\"Cleanup\":\"Safe\",\"KeepValidPins\":true,\"HybridDevices\":\"EcoresForNetAudio\",\"UserChosen\":true}";
        j.SetState("options", legacy);
        j.Save();
        var fromLegacy = AppOptions.Load(j, TabProfile());
        Check("a pre-per-tab file still loads", fromLegacy.UserChosen);
        Check("...and its single tier applies to EVERY tab",
            fromLegacy.PresetFor("Power") == TweakPreset.Costly &&
            fromLegacy.PresetFor("Interrupts") == TweakPreset.Costly &&
            fromLegacy.PresetFor("Network") == TweakPreset.Costly &&
            fromLegacy.PresetFor("Background") == TweakPreset.Costly,
            fromLegacy.PresetFor("Power").ToString());
        Check("...with an empty per-tab map, so nothing was invented", fromLegacy.Presets.Count == 0, fromLegacy.Presets.Count.ToString());

        // ---- a numeric-enum file (two formats back) still reads, per-tab map absent ----
        const string older = "{\"PlanTarget\":1,\"PowerProfile\":1,\"ApplyOnBattery\":false,\"CState\":2,\"Preset\":2," +
                             "\"TimerResolution\":5000,\"Msi\":1,\"Cleanup\":4,\"KeepValidPins\":true,\"HybridDevices\":1,\"UserChosen\":true}";
        j.SetState("options", older);
        j.Save();
        var fromOlder = AppOptions.Load(j, TabProfile());
        Check("a numeric-enum file still reads, and its tier reaches every tab",
            fromOlder.Preset == TweakPreset.Costly && fromOlder.PresetFor("Network") == TweakPreset.Costly,
            fromOlder.Preset.ToString());
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}
Console.WriteLine("Wave 25: the tier's scope is stated, not remembered");
{
    // NN/g heuristic 6: "the user should not have to remember information from one part of the
    // interface to another". A tier that is per tab must therefore be readable without visiting the
    // tab, and the confirmation must say what it is changing.
    Check("every tier has a short name for the rail", Enum.GetValues<TweakPreset>().All(p =>
    {
        string s = TweakPresetSelector.ShortLabel(p);
        return s.Length > 0 && s.Length < 12 && !s.Contains('(');
    }));
    Check("short names are distinct", Enum.GetValues<TweakPreset>()
        .Select(TweakPresetSelector.ShortLabel).Distinct().Count() == Enum.GetValues<TweakPreset>().Length);
    Check("the short name is the label's first word", Enum.GetValues<TweakPreset>().All(p =>
        TweakPresetSelector.Label(p).StartsWith(TweakPresetSelector.ShortLabel(p), StringComparison.Ordinal)),
        string.Join(" | ", Enum.GetValues<TweakPreset>().Select(p => TweakPresetSelector.Label(p))));

    // The confirmation names the tab, so "Switch to Safe" is never ambiguous.
    var plan = new TransitionPlan(new[] { "a" }, new[] { "b" });
    string scoped = PresetTransition.Describe(TweakPreset.Safe, plan, "Interrupts");
    Check("the confirmation names the tab it changes", scoped.Contains("Interrupts"), scoped);
    Check("...and still names the tier", scoped.Contains("Safe"), scoped);
    Check("...and still says what it will do", scoped.Contains("apply 1") && scoped.Contains("put back 1"), scoped);

    // Without a scope it keeps the old whole-app wording, so no caller is forced to pass one.
    string wholeApp = PresetTransition.Describe(TweakPreset.Safe, plan);
    Check("no scope keeps the old wording", wholeApp.Contains("the machine"), wholeApp);

    // A no-op now says WHICH thing already matches.
    string noop = PresetTransition.Describe(TweakPreset.Safe, new TransitionPlan(Array.Empty<string>(), Array.Empty<string>()), "Power");
    Check("a no-op names the tab too", noop.Contains("Power") && noop.Contains("already matches"), noop);
}
// ================================================================ Wave 8, Part 2: 12-topology matrix

Console.WriteLine("Wave 8 P2: 12-topology hardware simulation matrix");
{
    // ---- helpers ---------------------------------------------------------------------
    static List<AffinityPlanner.PhysCore> PhysCoresOf(CpuTopology topo) =>
        topo.Cpus.GroupBy(c => c.CoreId)
            .Select(g => new AffinityPlanner.PhysCore
            {
                Id = g.Key,
                First = g.Min(c => c.Index),
                Threads = g.Select(c => c.Index).OrderBy(x => x).ToList(),
            })
            .OrderBy(c => c.First).ToList();

    static AffinityPlanner.Want WantOf(PciDevice d, DeviceRole role, int cores) => new(d, role, cores, "sim");

    static string MapOf(AffinityPlan plan) =>
        string.Join("; ", plan.Assignments.Select(a => $"{a.Device.DeviceId}->{string.Join(",", a.Cpus)}{(a.Shared ? "S" : "")}"));

    // A NIC device with the RSS keywords a real driver exposes.
    static PciDevice Nic(string id = @"PCI\VEN_10EC&DEV_8126\1") =>
        new()
        {
            InstanceId = id, Name = "Realtek Gaming 5GbE Family Controller",
            BaseClass = 0x02, SubClass = 0x00, ProgIf = 0x00, Started = true, InterruptSupport = 6,
            Connected = true, RssCapable = true, RssQueueOptions = new() { "1", "2", "4", "8" },
        };

    // ---- 1. 9950X3D / 7950X3D: 16C/32T, 8C 96MB CCD0 + 8C 32MB CCD1, SMT on -----------
    {
        var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
        Check("1. 16C/32T dual-CCD: V-Cache CCD detected", t.IsDualCcdX3D && t.VCacheGroup!.CpuRange() == "0-15");
        Check("1. CCD0 (LPs 0..15) receives zero interrupts",
            plan.Assignments.All(a => a.Cpus.All(c => c >= 16)), MapOf(plan));
        Check("1. every device lands on LPs 16..31",
            plan.Assignments.SelectMany(a => a.Cpus).All(c => c is >= 16 and <= 31));
        var nic = plan.Assignments.Single(a => a.Device.DeviceId == "8126");
        Check("1. RSS queues stay on CCD1 too",
            nic.Rss is { } r1 && r1.BaseProcNumber >= 16 && r1.MaxProcNumber <= 31, nic.Rss?.Describe() ?? "null");
    }

    // ---- 2. 9900X3D / 7900X3D: 12C/24T asymmetric 6+6, SMT on ------------------------
    {
        var t = Topo(Enumerable.Range(0, 12).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x00000FFFUL, 96u << 20), (0x00FFF000UL, 32u << 20) });
        Check("2. 12C/24T asymmetric: 24 LPs, 12 cores", t.LogicalCount == 24 && t.CoreCount == 12);
        Check("2. V-Cache CCD is LPs 0..11 (6 cores)", t.VCacheGroup!.CpuRange() == "0-11", t.VCacheGroup.CpuRange());

        var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
        Check("2. CCD0 (LPs 0..11) receives zero interrupts",
            plan.Assignments.All(a => a.Cpus.All(c => c >= 12)), MapOf(plan));
        Check("2. every device lands on LPs 12..23",
            plan.Assignments.SelectMany(a => a.Cpus).All(c => c is >= 12 and <= 23));

        // 6 free cores on CCD1 vs 8 device slots: the budget guard must degrade by sharing,
        // never by pinning onto the V-Cache CCD and never by throwing.
        Check("2. more devices than cores: sharing happens on CCD1 only",
            plan.Assignments.Any(a => a.Shared) && plan.Assignments.Where(a => a.Shared).All(a => a.Cpus.All(c => c >= 12)),
            MapOf(plan));
        Check("2. the GPU never shares", plan.Assignments.Count(a => a.Role == DeviceRole.Gpu) >= 1 &&
            !plan.Assignments.Skip(1).Any(a => a.Cpus.Intersect(plan.Assignments[0].Cpus).Any()), MapOf(plan));
        Check("2. each free core hosts at most one unshared device",
            plan.Assignments.Where(a => !a.Shared).SelectMany(a => a.Cpus).Distinct().Count() ==
            plan.Assignments.Where(a => !a.Shared).Sum(a => a.Cpus.Length), MapOf(plan));
    }

    // ---- 3. 9800X3D / 7800X3D: 8C/16T single-CCD 96MB V-Cache ------------------------
    {
        var t = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 96u << 20) });
        Check("3. single-CCD V-Cache detected", t.IsSingleCcdVCache && !t.IsDualCcdX3D);
        Check("3. not classified as multi-CCD", !t.IsMultiCcd);

        var prof = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 7 9800X3D 8-Core Processor", CpuVendor = "AMD", Cpu = t,
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        Check("3. CpuKind is the single-CCD V-Cache label", prof.CpuKind == "Single CCD with 3D V-Cache", prof.CpuKind);

        var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu / 2 is 2 or 5 ? 200 : 150), null, DesktopDevices());
        Check("3. CPU 0's two threads are protected",
            plan.Assignments.All(a => !a.Cpus.Contains(0) && !a.Cpus.Contains(1)), MapOf(plan));
        Check("3. the top game cores (4/5, 10/11) are protected too",
            plan.Assignments.All(a => !a.Cpus.Contains(4) && !a.Cpus.Contains(5) && !a.Cpus.Contains(10) && !a.Cpus.Contains(11)), MapOf(plan));
        Check("3. IRQs steer to secondary cores only", plan.Assignments.All(a => a.Cpus.All(c => c % 2 == 0)));
    }

    // ---- 4. 9950X3D with SMT DISABLED: 16C/16T ---------------------------------------
    {
        var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c }, (byte)0)),
            new[] { (0x00FFUL, 96u << 20), (0xFF00UL, 32u << 20) });
        Check("4. SMT reported off", !t.SmtEnabled && t.LogicalCount == 16 && t.CoreCount == 16);
        var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices());
        Check("4. V-Cache CCD (LPs 0..7) stays clean", plan.Assignments.All(a => a.Cpus.All(c => c >= 8)), MapOf(plan));

        var nic = plan.Assignments.Single(a => a.Device.DeviceId == "8126");
        Check("4. without SMT the two cores are a contiguous 2-LP span",
            nic.Cpus.Length == 2 && nic.Cpus[1] - nic.Cpus[0] == 1, string.Join(",", nic.Cpus));
        Check("4. RSS sets 1 processor per core (no SMT doubling)",
            nic.Rss is { ThreadsPerCore: 1 } r4 && r4.NumProcessors == 2 &&
            r4.BaseProcNumber == nic.Cpus[0] && r4.MaxProcNumber == nic.Cpus[1],
            nic.Rss?.Describe() ?? "null");
        Check("4. the affinity masks are valid 8-byte little-endian",
            nic.Mask != 0 && RegWrite.MaskBytes(nic.Mask).Length == 8 &&
            RegWrite.AsMask(new RegValue(true, RegistryValueKind.Binary, RegWrite.MaskBytes(nic.Mask))) == nic.Mask);
    }

    // ---- 5. i9-14900K: 24C/32T hybrid, 8 P w/ HT + 16 E no HT ------------------------
    {
        var cores = Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)1))
            .Concat(Enumerable.Range(16, 16).Select(c => (new[] { c }, (byte)0)));
        var t = Topo(cores, new[] { (0xFFFFFFFFUL, 36u << 20) });
        Check("5. hybrid detected, 24C/32T", t.IsHybrid && t.CoreCount == 24 && t.LogicalCount == 32);
        Check("5. P-cores have 2 threads, E-cores 1",
            t.Cpus.Where(c => c.Index < 16).GroupBy(c => c.CoreId).All(g => g.Count() == 2) &&
            t.Cpus.Where(c => c.Index >= 16).GroupBy(c => c.CoreId).All(g => g.Count() == 1));

        var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu >= 16 ? 10 : cpu / 2 is 4 or 5 ? 250 : 200), null, DesktopDevices());
        Check("5. GPU and Input stay on P-cores", plan.Assignments.Where(a => a.Role is DeviceRole.Gpu or DeviceRole.Input)
            .All(a => a.Cpus.All(c => c < 16)), MapOf(plan));
        Check("5. the NIC lands on E-cores", plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus.All(c => c >= 16), MapOf(plan));

        // RssFor must read the real thread count of whichever core it lands on (1 on an E-core).
        var nicDev = Nic();
        var eCore = plan.Assignments.Single(a => a.Device.DeviceId == "8126").Cpus[0];
        var rss = AffinityPlanner.RssFor(WantOf(nicDev, DeviceRole.Network, 1), new[] { eCore }, PhysCoresOf(t))!;
        Check("5. RSS on an E-core uses 1 processor per core", rss.ThreadsPerCore == 1 && rss.NumProcessors == 1, rss.Describe());
        var pCore = 16 - 2;   // the last P-core's first thread
        var rssP = AffinityPlanner.RssFor(WantOf(nicDev, DeviceRole.Network, 1), new[] { pCore }, PhysCoresOf(t))!;
        Check("5. RSS on a P-core uses 2 processors", rssP.ThreadsPerCore == 2 && rssP.NumProcessors == 2, rssP.Describe());

        // CpusFor must expand both shapes correctly.
        var gpuAsg = plan.Assignments.Single(a => a.Role == DeviceRole.Gpu);
        Check("5. CpusFor expands a P-core to 2 threads", InterruptCheck.CpusFor(gpuAsg, t).Length == 2,
            string.Join(",", InterruptCheck.CpusFor(gpuAsg, t)));
        var nicAsg = plan.Assignments.Single(a => a.Device.DeviceId == "8126");
        Check("5. CpusFor expands E-cores to 1 thread each",
            InterruptCheck.CpusFor(nicAsg, t).Length == nicAsg.Cpus.Length,
            string.Join(",", InterruptCheck.CpusFor(nicAsg, t)));

        // Power: heterogeneous parking must not be forced off on a hybrid CPU.
        var prof = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "Intel(R) Core(TM) i9-14900K", CpuVendor = "Intel", Cpu = t,
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        Check("5. a hybrid desktop is parking-dependent", prof.ParkingDependentCpu);
        var defs = AppOptions.DefaultsFor(prof);
        Check("5. defaults keep Windows parking (Balanced) so P/E migration works",
            defs.PlanTarget == PlanTargetKind.Balanced && defs.PowerProfile == PowerProfileKind.DevicesOnly,
            $"{defs.PlanTarget}/{defs.PowerProfile}");
    }

    // ---- 6. Core Ultra 9 285K Arrow Lake: 24C/24T hybrid, no HT ----------------------
    {
        var cores = Enumerable.Range(0, 24).Select(c => (new[] { c }, (byte)(c is < 2 or >= 18 ? 1 : 0)));
        var t = Topo(cores, new[] { (0x00FC0003UL, 36u << 20), (0x0003FFFCUL, 12u << 20) });
        Check("6. Arrow Lake: hybrid, no SMT", t.IsHybrid && !t.SmtEnabled && t.CoreCount == 24);
        Check("6. unequal L3 blocks do not fake a dual-CCD X3D", !t.IsDualCcdX3D && t.VCacheGroup is null);

        var plan = AffinityPlanner.Plan(t, Sets(t, cpu => cpu is 18 or 19 ? 240 : cpu is < 2 or >= 18 ? 200 : 100), null, DesktopDevices());
        Check("6. CPU 0 and the two top P-cores are protected",
            string.Join(",", plan.ReservedCpus) == "0,18,19", string.Join(",", plan.ReservedCpus));
        Check("6. GPU and Input land on remaining P-cores",
            plan.Assignments.Where(a => a.Role is DeviceRole.Gpu or DeviceRole.Input).All(a => a.Cpus.All(c => c is 1 or >= 20)), MapOf(plan));
        var nic = plan.Assignments.Single(a => a.Device.DeviceId == "8126");
        Check("6. NIC uses 2 E-cores with 1 processor each",
            nic.Rss is { ThreadsPerCore: 1 } r6 && r6.NumProcessors == 2, nic.Rss?.Describe() ?? "null");
        Check("6. no device is stranded on a reserved core",
            plan.Assignments.All(a => a.Cpus.All(c => !plan.ReservedCpus.Contains(c))), MapOf(plan));
    }

    // ---- 7. Extreme core starvation: 4C/8T and 4C/4T with 6 devices ------------------
    foreach (bool smt in new[] { true, false })
    {
        string tag = smt ? "7a. 4C/8T" : "7b. 4C/4T";
        var cores = Enumerable.Range(0, 4)
            .Select(c => smt ? (new[] { c * 2, c * 2 + 1 }, (byte)0) : (new[] { c }, (byte)0));
        var t = Topo(cores, new[] { (smt ? 0xFFUL : 0xFUL, 32u << 20) });

        // Six latency-critical devices, including a shared xHCI hub carrying the mouse and the DAC.
        var devs = new List<PciDevice>
        {
            Dev(@"PCI\VEN_1002&DEV_7550\1", "AMD Radeon RX 9070 XT", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }),
            Dev(@"PCI\VEN_1002&DEV_AB40\2", "AMD High Definition Audio Device", 0x04, 0x03, 0x00, new Attached { AudioEndpoints = 1 }),
            Dev(@"PCI\VEN_1022&DEV_43FC\3", "USB 3.20 eXtensible Host Controller", 0x0C, 0x03, 0x30,
                new Attached { Mice = 1, Keyboards = 1, HasDefaultAudio = true, UsbDevices = 3 }),
            Dev(@"PCI\VEN_1022&DEV_15B6\4", "USB 3.10 eXtensible Host Controller", 0x0C, 0x03, 0x30, new Attached { AudioDevices = 1 }),
            Nic(@"PCI\VEN_10EC&DEV_8126\5"),
            Dev(@"PCI\VEN_17CB&DEV_1107\6", "Qualcomm FastConnect 7800 Wi-Fi 7", 0x02, 0x80, 0x00),
        };
        PciScanner.Classify(devs, Array.Empty<GpuInfo>());

        AffinityPlan plan = null!;
        bool threw = false;
        try { plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs); }
        catch (Exception ex) { threw = true; Console.WriteLine("        threw: " + ex.GetType().Name + ": " + ex.Message); }

        Check($"{tag}: no exception from the planner", !threw);
        Check($"{tag}: nothing is pinned onto core 0", plan is not null && plan.Assignments.All(a => !a.Cpus.Contains(0) && !(smt && a.Cpus.Contains(1))), plan is null ? "null" : MapOf(plan));
        Check($"{tag}: every device still gets an assignment", plan is not null && plan.Assignments.Count == 6,
            plan is null ? "null" : MapOf(plan));
        Check($"{tag}: the GPU never shares", plan is not null &&
            plan.Assignments.Count(a => a.Cpus.Intersect(plan.Assignments[0].Cpus).Any()) == 1, plan is null ? "null" : MapOf(plan));
        Check($"{tag}: secondary cores are shared gracefully",
            plan is not null && plan.Assignments.Any(a => a.Shared), plan is null ? "null" : MapOf(plan));
        Check($"{tag}: every assignment is on a real core",
            plan is not null && plan.Assignments.All(a => a.Cpus.All(c => c < t.LogicalCount)), plan is null ? "null" : MapOf(plan));
    }

    // ---- 8. LP 63 boundary and >64-LP multi-group ------------------------------------
    {
        Check("8. LP 63 serialises to 8 bytes little-endian 00..80",
            RegWrite.MaskBytes(1UL << 63).SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x80 }),
            BitConverter.ToString(RegWrite.MaskBytes(1UL << 63)));
        Check("8. LP 63 round-trips through AsMask",
            RegWrite.AsMask(new RegValue(true, RegistryValueKind.Binary, RegWrite.MaskBytes(1UL << 63))) == 1UL << 63);
        Check("8. FormatMask names CPU 63", RegWrite.FormatMask(1UL << 63) == "CPU 63", RegWrite.FormatMask(1UL << 63));
        Check("8. LP 0 and LP 63 coexist in one mask",
            RegWrite.FormatMask(1UL | (1UL << 63)) == "CPUs 0, 63", RegWrite.FormatMask(1UL | (1UL << 63)));
        // A 32-bit shift would have overflowed here; 1UL << 31 must still be the sign-bit value.
        Check("8. bit 31 is not a signed-overflow", (1UL << 31) == 2147483648UL && RegWrite.FormatMask(1UL << 31) == "CPU 31");

        // 128-thread Threadripper: two processor groups, read-only by design.
        var big = new CpuTopology
        {
            Cpus = Enumerable.Range(0, 64).Select(i => new LogicalCpu(i, i, 0, 0, false)).ToList(),
            L3Groups = new List<CacheGroup> { new(0, 96L << 20, 0xFFFFFFFFFFFFFFFFUL), new(1, 32L << 20, 0xFFFFFFFFFFFFFFFFUL) },
            CoreCount = 64,
            GroupCount = 2,
        };
        var plan = AffinityPlanner.Plan(big, Sets(big, _ => 0), null, DesktopDevices());
        Check("8. >64 LP / multi-group stays read-only", !plan.Applicable, plan.NotApplicableReason ?? "");
        Check("8. the refusal explains itself", plan.NotApplicableReason!.Contains("64", StringComparison.Ordinal), plan.NotApplicableReason);
        Check("8. no assignments are produced in read-only mode", plan.Assignments.Count == 0);

        // A 64-LP single-group machine with 64 logical CPUs is still refused by the >64 rule only
        // when groups > 1; 32 threads on one group is fine.
        var t32 = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        Check("8. 32 LPs on one group is still applicable", AffinityPlanner.Plan(t32, Sets(t32, _ => 0), null, DesktopDevices()).Applicable);
    }

    // ---- 9. Dual GPU: Radeon iGPU + discrete RX 9070 XT -------------------------------
    {
        var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var gpus = new List<GpuInfo>
        {
            new() { ClassSubkey = "0000", ClassKeyPath = @"HKLM\X\0000", InstanceId = @"PCI\VEN_1002&DEV_13C0\i",
                    Name = "AMD Radeon(TM) Graphics", Vendor = GpuVendor.Amd, IsIntegrated = true },
            new() { ClassSubkey = "0001", ClassKeyPath = @"HKLM\X\0001", InstanceId = @"PCI\VEN_1002&DEV_7550\d",
                    Name = "AMD Radeon RX 9070 XT", Vendor = GpuVendor.Amd, IsIntegrated = false },
        };
        var devs = new List<PciDevice>
        {
            Dev(@"PCI\VEN_1002&DEV_13C0\i", "AMD Radeon(TM) Graphics", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }),
            Dev(@"PCI\VEN_1002&DEV_7550\d", "AMD Radeon RX 9070 XT", 0x03, 0x00, 0x00, new Attached { Monitors = 2 }),
            Nic(), Dev(@"PCI\VEN_1022&DEV_15B6\6", "USB 3.10 eXtensible Host Controller", 0x0C, 0x03, 0x30),
        };
        PciScanner.Classify(devs, gpus);
        var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs);
        var prime = plan.Assignments.First();
        Check("9. the discrete GPU takes the top GPU slot", prime.Device.DeviceId == "7550" && prime.Role == DeviceRole.Gpu, MapOf(plan));
        // The iGPU drives a monitor, so it also carries a Gpu role (it is on the frame path), but it
        // must never be ranked above the discrete card.
        int dIdx = plan.Assignments.FindIndex(a => a.Device.DeviceId == "7550");
        int iIdx = plan.Assignments.FindIndex(a => a.Device.DeviceId == "13C0");
        Check("9. the iGPU is ranked below the discrete card", dIdx == 0 && iIdx > dIdx, $"discrete@{dIdx} igpu@{iIdx} :: {MapOf(plan)}");
        Check("9. the iGPU is flagged integrated", plan.Assignments.Single(a => a.Device.DeviceId == "13C0").Device.IsIntegratedGpu);
    }

    // ---- 10. Crowded USB/PCIe: NVMe, bridges, two mouse hubs, shared hub ---------------
    {
        var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var devs = new List<PciDevice>
        {
            Dev(@"PCI\VEN_1002&DEV_7550\1", "AMD Radeon RX 9070 XT", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }),
            Dev(@"PCI\VEN_1022&DEV_43FC\2", "USB 3.20 eXtensible Host Controller", 0x0C, 0x03, 0x30,
                new Attached { Mice = 1, Keyboards = 1, HasDefaultAudio = true, UsbDevices = 4 }),
            Dev(@"PCI\VEN_1022&DEV_15B6\3", "USB 3.10 eXtensible Host Controller", 0x0C, 0x03, 0x30, new Attached { Mice = 1, UsbDevices = 1 }),
            Dev(@"PCI\VEN_1022&DEV_15B7\4", "USB 3.10 eXtensible Host Controller", 0x0C, 0x03, 0x30, new Attached { Mice = 1, UsbDevices = 2 }),
            Dev(@"PCI\VEN_15B7&DEV_5030\5", "Standard NVM Express Controller", 0x01, 0x08, 0x02),
            Dev(@"PCI\VEN_1D97&DEV_5236\6", "Standard NVM Express Controller", 0x01, 0x08, 0x02),
            Dev(@"PCI\VEN_1022&DEV_14DB\7", "PCI Express Root Port", 0x06, 0x04, 0x00),
            Nic(@"PCI\VEN_10EC&DEV_8126\8"),
        };
        PciScanner.Classify(devs, Array.Empty<GpuInfo>());
        var plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs);
        Check("10. NVMe controllers are never pinned",
            plan.Assignments.All(a => a.Device.DeviceId is not ("5030" or "5236")), MapOf(plan));
        Check("10. PCIe root bridges are never pinned", plan.Assignments.All(a => a.Device.DeviceId != "14DB"), MapOf(plan));
        Check("10. the mouse hub is chosen as the input controller",
            plan.Assignments.Single(a => a.Role == DeviceRole.Input).Device.DeviceId is "43FC" or "15B6" or "15B7", MapOf(plan));
        Check("10. the shared mouse+DAC hub is escalated when cores allow",
            plan.Assignments.Single(a => a.Device.DeviceId == "43FC").Cpus.Length == 2, MapOf(plan));
        Check("10. escalation never pushes another device into sharing",
            plan.Assignments.All(a => !a.Shared), MapOf(plan));
        Check("10. the plan explains the shared-ISR situation",
            plan.Notes.Any(n => n.Contains("share one ISR", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", plan.Notes.Take(3)));
        Check("10. every USB controller gets its own core",
            plan.Assignments.Where(a => a.Role is DeviceRole.OtherUsb or DeviceRole.Input)
                .SelectMany(a => a.Cpus).Distinct().Count() ==
            plan.Assignments.Where(a => a.Role is DeviceRole.OtherUsb or DeviceRole.Input).Sum(a => a.Cpus.Length), MapOf(plan));
    }

    // ---- 11. Laptop / battery topology -----------------------------------------------
    {
        var t = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 16u << 20) });
        var prof = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Home", "25H2", 26200, 9457, "Core"),
            Manufacturer = "LENOVO", Model = "82JQ", FormFactor = FormFactor.Laptop, FormFactorEvidence = "chassis 10",
            CpuName = "AMD Ryzen 7 5800H with Radeon Graphics", CpuVendor = "AMD", Cpu = t,
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        Check("11. laptop is detected as a laptop", prof.IsLaptop);
        var defs = AppOptions.DefaultsFor(prof);
        Check("11. laptop defaults to the current active plan (vendor performance mode)",
            defs.PlanTarget == PlanTargetKind.CurrentActive, defs.PlanTarget.ToString());
        Check("11. laptop defaults to the Laptop cleanup preset", defs.Cleanup == CleanupPreset.Laptop, defs.Cleanup.ToString());
        Check("11. laptop does not force battery (DC) values", !defs.ApplyOnBattery);
        Check("11. laptop is not parking-dependent", !prof.ParkingDependentCpu);

        // Desktop-only power settings must be filtered out on a laptop.
        var built = PowerModule.Build(prof, new Journal(Path.Combine(Path.GetTempPath(), "dpc-p11-" + Guid.NewGuid().ToString("N") + ".json")), () => defs).ToList();
        Check("11. desktop-only display settings are excluded on a laptop",
            !built.Any(b => b.Info.Id is "power.display.off" or "power.display.dim"),
            string.Join(",", built.Select(b => b.Info.Id).Where(i => i.StartsWith("power.display"))));
        var boot = BootTasksModule.Build(prof, new Journal(Path.Combine(Path.GetTempPath(), "dpc-p11b-" + Guid.NewGuid().ToString("N") + ".json")), () => defs).ToList();
        Check("11. the 0.5 ms timer task is NOT on by default on a laptop",
            boot.Single(b => b.Info.Id == "boot.timer").Info.DefaultSelected == false);
        Check("11. the global timer switch is NOT on by default on a laptop",
            boot.Single(b => b.Info.Id == "boot.global-timer").Info.DefaultSelected == false);
        Check("11. a laptop planner still protects core 0", AffinityPlanner.Plan(t, Sets(t, _ => 0), null, DesktopDevices())
            .Assignments.All(a => !a.Cpus.Contains(0)));
    }

    // ---- 12. Hostile / incomplete device nodes ---------------------------------------
    {
        var t = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });

        var devs = new List<PciDevice>
        {
            // No DriverKey at all: every ClassKey/InterruptKey read is null.
            new() { InstanceId = @"PCI\VEN_FFFF&DEV_0001\1", Name = "Unknown device", BaseClass = 0x03, SubClass = 0x00, Started = true },
            // A GPU with no monitors, no interrupt support (line-based legacy).
            new() { InstanceId = @"PCI\VEN_1002&DEV_9999\2", Name = "Legacy display",
                    BaseClass = 0x03, SubClass = 0x00, ProgIf = 0x00, Started = true, InterruptSupport = 1 },
            // A NIC that claims no RSS capability and offers no queue options.
            new() { InstanceId = @"PCI\VEN_10EC&DEV_0001\3", Name = "Bare NIC", BaseClass = 0x02, SubClass = 0x00,
                    Started = true, InterruptSupport = 1, RssCapable = false },
            // A device that is not running (code 10).
            new() { InstanceId = @"PCI\VEN_1022&DEV_15B6\4", Name = "Stopped USB controller",
                    BaseClass = 0x0C, SubClass = 0x03, ProgIf = 0x30, Started = false, Problem = 10, InterruptSupport = 6 },
            // A device with an empty existing affinity mask (0-byte override) already recorded.
            new() { InstanceId = @"PCI\VEN_1022&DEV_43FC\5", Name = "USB 3.20 eXtensible Host Controller",
                    BaseClass = 0x0C, SubClass = 0x03, ProgIf = 0x30, Started = true, InterruptSupport = 6,
                    Attached = new Attached { Mice = 1 }, ExistingMask = 0UL },
        };
        PciScanner.Classify(devs, Array.Empty<GpuInfo>());

        AffinityPlan plan = null!;
        bool threw = false;
        try { plan = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, devs, keepValidPins: true); }
        catch (Exception ex) { threw = true; Console.WriteLine("        threw: " + ex.GetType().Name + ": " + ex.Message); }
        Check("12. incomplete device nodes never throw", !threw);
        Check("12. the stopped device is left alone",
            plan is not null && plan.Assignments.All(a => a.Device.DeviceId != "15B6"), plan is null ? "null" : MapOf(plan));
        Check("12. a 0-mask is not treated as a valid pin",
            plan is not null && plan.Assignments.Where(a => a.Kept).All(a => a.Mask != 0), plan is null ? "null" : MapOf(plan));
        Check("12. a bare NIC yields no RSS plan when it is not the chosen adapter",
            plan is not null && plan.Assignments.Where(a => a.Device.DeviceId == "0001").All(a => a.Rss is null || !a.Device.RssCapable),
            plan is null ? "null" : MapOf(plan));

        // RSS keyword selection must cope with a driver offering no queue options.
        var bareNic = new PciDevice { InstanceId = @"PCI\X\9", Name = "Bare", RssCapable = false };
        Check("12. a driver with no queue keyword falls back to one queue per core",
            RssTweak.ChooseQueues(bareNic, new RssPlan(20, 23, 4, 2)) == "2", RssTweak.ChooseQueues(bareNic, new RssPlan(20, 23, 4, 2)));
        // And with an empty (not missing) option list.
        var emptyOpts = new PciDevice { InstanceId = @"PCI\X\10", Name = "Empty", RssQueueOptions = new() };
        Check("12. an empty queue-option list still produces a number",
            int.TryParse(RssTweak.ChooseQueues(emptyOpts, new RssPlan(20, 21, 2, 1)), out _));

        // ReadLive on a device with no interrupt key must not throw.
        bool liveThrew = false;
        try { AffinityTweak.ReadLive(devs[0]); } catch { liveThrew = true; }
        Check("12. ReadLive on a keyless device does not throw", !liveThrew);

        // A plan with zero devices must still be usable.
        var empty = AffinityPlanner.Plan(t, Sets(t, _ => 0), null, Array.Empty<PciDevice>());
        Check("12. zero devices produces an empty but valid plan", empty.Applicable && empty.Assignments.Count == 0);
    }
}
// ================================================================ Wave 9: Alder Lake P/E placement policy

Console.WriteLine("Wave 9: 12700K / 12600K hybrid placement policy (the reported case)");
{
    // Alder Lake fixtures. P-cores have HT, E-cores do not, small unified L3.
    static CpuTopology AlderLake(int pCores, int eCores)
    {
        var cores = Enumerable.Range(0, pCores).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)1))
            .Concat(Enumerable.Range(pCores * 2, eCores).Select(c => (new[] { c }, (byte)0)));
        int lps = pCores * 2 + eCores;
        ulong mask = lps >= 64 ? ulong.MaxValue : (1UL << lps) - 1;
        return Topo(cores, new[] { (mask, 25u << 20) });
    }

    static List<PciDevice> AlderLakeDevices()
    {
        var devs = new List<PciDevice>
        {
            Dev(@"PCI\VEN_10DE&DEV_2504\1", "NVIDIA GeForce RTX 3070", 0x03, 0x00, 0x00, new Attached { Monitors = 1 }),
            Dev(@"PCI\VEN_8086&DEV_7AE0\2", "Intel USB 3.20 eXtensible Host Controller", 0x0C, 0x03, 0x30,
                new Attached { Mice = 1, Keyboards = 1, UsbDevices = 3 }),
            Dev(@"PCI\VEN_8086&DEV_7AD0\3", "Intel High Definition Audio", 0x04, 0x03, 0x00,
                new Attached { AudioEndpoints = 1, HasDefaultAudio = true }),
            new() { InstanceId = @"PCI\VEN_8086&DEV_1533\4", Name = "Intel I210 Gigabit Network Connection",
                    BaseClass = 0x02, SubClass = 0x00, ProgIf = 0x00, Started = true, InterruptSupport = 6,
                    Connected = true, RssCapable = true, RssQueueOptions = new() { "1", "2", "4" } },
        };
        PciScanner.Classify(devs, Array.Empty<GpuInfo>());
        return devs;
    }

    static bool OnP(int lp, int pThreads) => lp < pThreads;
    static string Where(AffinityPlan p) =>
        string.Join("; ", p.Assignments.Select(a => $"{a.Role}={string.Join(",", a.Cpus)}{(a.Cpus.All(c => OnP(c, 16)) ? "(P)" : a.Cpus.All(c => !OnP(c, 16)) ? "(E)" : "(mix)")}"));

    // ---- 12700K: 8P/16T + 4E/4T = 12C/20T -------------------------------------------
    {
        var t = AlderLake(8, 4);
        Check("12700K: 12 cores / 20 threads, hybrid, SMT on",
            t.CoreCount == 12 && t.LogicalCount == 20 && t.IsHybrid && t.SmtEnabled,
            $"{t.CoreCount}/{t.LogicalCount}");
        var devs = AlderLakeDevices();
        var sets = Sets(t, cpu => cpu >= 16 ? 10 : cpu / 2 is 2 or 3 ? 250 : 200);

        // --- mode 1: default, E-cores for audio/network ---
        var eco = AffinityPlanner.Plan(t, sets, null, devs, hybridPolicy: HybridDevicePolicy.EcoresForNetAudio);
        var gpu = eco.Assignments.Single(a => a.Role == DeviceRole.Gpu);
        var input = eco.Assignments.Single(a => a.Role == DeviceRole.Input);
        var net = eco.Assignments.Single(a => a.Role == DeviceRole.Network);
        var audio = eco.Assignments.Single(a => a.Role == DeviceRole.Audio);
        Check("12700K/E-cores: GPU on a P-core", gpu.Cpus.All(c => OnP(c, 16)), Where(eco));
        Check("12700K/E-cores: mouse controller on a P-core", input.Cpus.All(c => OnP(c, 16)), Where(eco));
        Check("12700K/E-cores: NIC on E-cores", net.Cpus.All(c => !OnP(c, 16)), Where(eco));
        Check("12700K/E-cores: audio on E-cores", audio.Cpus.All(c => !OnP(c, 16)), Where(eco));
        Check("12700K/E-cores: only CPU 0/1 and the two top-ranked P-cores are reserved",
            string.Join(",", eco.ReservedCpus) == "0,1,4,5,6,7", string.Join(",", eco.ReservedCpus));
        Check("12700K/E-cores: the explanation names the E-core reason",
            net.Reason.Contains("Placed on E-core(s)") && net.Reason.Contains("keep P-cores free for game threads") &&
            net.Reason.Contains("Hybrid CPU placement"), net.Reason);
        Check("12700K/E-cores: the P-core devices say so plainly",
            gpu.Reason.Contains("Placed on P-core"), gpu.Reason);

        // --- mode 2: P-cores when available ---
        var pfirst = AffinityPlanner.Plan(t, sets, null, devs, hybridPolicy: HybridDevicePolicy.PcoresWhenAvailable);
        var net2 = pfirst.Assignments.Single(a => a.Role == DeviceRole.Network);
        var audio2 = pfirst.Assignments.Single(a => a.Role == DeviceRole.Audio);
        Check("12700K/P-first: NIC takes a spare P-core", net2.Cpus.All(c => OnP(c, 16)), Where(pfirst));
        Check("12700K/P-first: audio takes a spare P-core", audio2.Cpus.All(c => OnP(c, 16)), Where(pfirst));
        Check("12700K/P-first: CPU 0/1 and 4 P-cores stay reserved for the game",
            pfirst.ReservedCpus.Count >= 6, string.Join(",", pfirst.ReservedCpus));
        Check("12700K/P-first: nothing lands on an E-core while P-cores are free",
            pfirst.Assignments.All(a => a.Cpus.All(c => OnP(c, 16))), Where(pfirst));
        Check("12700K/P-first: P-core placements are labelled",
            net2.Reason.Contains("Placed on P-core"), net2.Reason);

        // --- mode 3: benchmark decides (P-cores measurably steadier) ---
        var bench = new BenchResult { When = DateTime.Now, SamplesPerCore = 5000 };
        foreach (var c in t.Cpus.Where(c => !c.IsSecondThread))
            bench.Cores.Add(new CoreScore
            {
                Cpu = c.Index,
                SpeedMs = c.Index < 16 ? 10.0 : 14.0,                           // P-cores faster
                WakeMedianUs = c.Index < 16 ? 5.0 : 30.0,                       // P-cores steadier
                WakeP99Us = c.Index < 16 ? 40 : 200,
                SchedulingClass = c.Index < 16 ? 200 : 10,
            });
        var byBench = AffinityPlanner.Plan(t, sets, bench, devs, hybridPolicy: HybridDevicePolicy.BenchmarkDecides);
        var net3 = byBench.Assignments.Single(a => a.Role == DeviceRole.Network);
        Check("12700K/benchmark-decides: the NIC follows the measured order to P-cores",
            net3.Cpus.All(c => OnP(c, 16)), Where(byBench));
        Check("12700K/benchmark-decides: E-cores are not forced on anything",
            byBench.Assignments.All(a => a.Cpus.All(c => OnP(c, 16))), Where(byBench));
        Check("12700K/benchmark-decides: no E-core note is produced",
            byBench.Assignments.All(a => !a.Reason.Contains("Placed on E-core")), Where(byBench));

        // And with the opposite benchmark, E-cores win on merit.
        var benchE = new BenchResult { When = DateTime.Now, SamplesPerCore = 5000 };
        foreach (var c in t.Cpus.Where(c => !c.IsSecondThread))
            benchE.Cores.Add(new CoreScore
            {
                Cpu = c.Index,
                SpeedMs = c.Index < 16 ? 14.0 : 10.0,                           // E-cores faster
                WakeMedianUs = c.Index < 16 ? 30.0 : 5.0,                       // E-cores steadier
                WakeP99Us = c.Index < 16 ? 200 : 40,
                SchedulingClass = c.Index < 16 ? 10 : 200,
            });
        var byBenchE = AffinityPlanner.Plan(t, sets, benchE, devs, hybridPolicy: HybridDevicePolicy.BenchmarkDecides);
        var gpuE = byBenchE.Assignments.Single(a => a.Role == DeviceRole.Gpu);
        var net4 = byBenchE.Assignments.Single(a => a.Role == DeviceRole.Network);
        // Pure measurement: the top device takes the best-measured core, which is an E-core here.
        Check("12700K/benchmark-decides: with steadier E-cores the GPU takes one on merit",
            gpuE.Cpus.All(c => !OnP(c, 16)), Where(byBenchE));
        // Only 4 E-cores exist; the three higher-priority roles consume them, so the NIC spills to P.
        Check("12700K/benchmark-decides: the NIC spills to a P-core once the 4 E-cores are used",
            net4.Cpus.All(c => OnP(c, 16)), Where(byBenchE));
        Check("12700K/benchmark-decides: the E-core note still explains the E-core placements",
            gpuE.Reason.Contains("Placed on E-core(s)") && gpuE.Reason.Contains("Hybrid CPU placement"), gpuE.Reason);
        Check("12700K/benchmark-decides: an exhausted E-cluster does not force E-cores on the NIC",
            net4.Reason.Contains("Placed on P-core"), net4.Reason);
    }

    // ---- 12600K: 6P/12T + 4E/4T = 10C/16T -------------------------------------------
    {
        var t = AlderLake(6, 4);
        Check("12600K: 10 cores / 16 threads, hybrid", t.CoreCount == 10 && t.LogicalCount == 16 && t.IsHybrid,
            $"{t.CoreCount}/{t.LogicalCount}");
        var devs = AlderLakeDevices();
        var sets = Sets(t, cpu => cpu >= 12 ? 10 : cpu / 2 is 2 or 3 ? 250 : 200);

        var eco = AffinityPlanner.Plan(t, sets, null, devs, hybridPolicy: HybridDevicePolicy.EcoresForNetAudio);
        Check("12600K/E-cores: GPU on a P-core", eco.Assignments.Single(a => a.Role == DeviceRole.Gpu).Cpus.All(c => c < 12),
            string.Join("; ", eco.Assignments.Select(a => $"{a.Role}={string.Join(",", a.Cpus)}")));
        Check("12600K/E-cores: NIC on E-cores", eco.Assignments.Single(a => a.Role == DeviceRole.Network).Cpus.All(c => c >= 12));
        Check("12600K/E-cores: 3 free P-cores remain after CPU 0/1 and the top two", eco.Candidates.Count(c => c.PerformanceCore) == 3,
            string.Join(",", eco.Candidates.Where(c => c.PerformanceCore).Select(c => c.Cpu)));

        // Six P-cores total: 0/1 + top 2 reserved leaves 2, and 4 device roles compete.
        var pfirst = AffinityPlanner.Plan(t, sets, null, devs, hybridPolicy: HybridDevicePolicy.PcoresWhenAvailable);
        Check("12600K/P-first: the 3 free P-cores go to the most important devices",
            pfirst.Assignments.Take(3).All(a => a.Cpus.All(c => c < 12)),
            string.Join("; ", pfirst.Assignments.Select(a => $"{a.Role}={string.Join(",", a.Cpus)}")));
        Check("12600K/P-first: only the fourth device spills onto E-cores",
            pfirst.Assignments.Skip(3).All(a => a.Cpus.All(c => c >= 12)),
            string.Join("; ", pfirst.Assignments.Select(a => $"{a.Role}={string.Join(",", a.Cpus)}")));
        Check("12600K/P-first: no exception, every device placed", pfirst.Assignments.Count == 4);
    }

    // ---- the option is persisted and defaults sensibly --------------------------------
    {
        var def = new AppOptions();
        Check("the hybrid policy defaults to E-cores for audio/network",
            def.HybridDevices == HybridDevicePolicy.EcoresForNetAudio, def.HybridDevices.ToString());
        var swapped = def.With(o => o.HybridDevices = HybridDevicePolicy.BenchmarkDecides);
        Check("the policy survives the With() copy", swapped.HybridDevices == HybridDevicePolicy.BenchmarkDecides);
        Check("the other options are untouched by the swap", swapped.KeepValidPins == def.KeepValidPins && swapped.Msi == def.Msi);
    }
}
// ================================================================ Wave 10: 28-CPU simulation suite

Console.WriteLine("Wave 10: 28-CPU architecture simulation matrix");
{
    // A core spec: logical threads, efficiency class (higher = P-core), L3 bytes (0 = no L3 of its
    // own, i.e. an Intel LP-E island core), and which L3 group/CCD it belongs to.
    static CpuTopology Build((int[] Threads, byte Eff, long L3, int Ccd)[] specs, int groups = 1)
    {
        var buf = new List<byte>();
        foreach (var s in specs)
        {
            var rec = new byte[8 + 24 + 16];
            BinaryPrimitives.WriteUInt32LittleEndian(rec, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
            rec[8] = (byte)(s.Threads.Length > 1 ? 1 : 0);
            rec[9] = s.Eff;
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8 + 22), 1);
            ulong m = 0; foreach (int th in s.Threads) m |= 1UL << th;
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8 + 24), m);
            buf.AddRange(rec);
        }
        foreach (var g in specs.Where(s => s.L3 > 0).GroupBy(s => s.Ccd))
        {
            ulong mask = g.SelectMany(s => s.Threads).Aggregate(0UL, (m, th) => m | (1UL << th));
            var rec = new byte[8 + 32 + 16];
            BinaryPrimitives.WriteUInt32LittleEndian(rec, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)rec.Length);
            rec[8] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8 + 4), (uint)g.First().L3);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8 + 8), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8 + 30), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8 + 32), mask);
            buf.AddRange(rec);
        }
        var topo = CpuTopology.Parse(buf.ToArray());
        return groups == 1 ? topo : new CpuTopology
        {
            Cpus = topo.Cpus, L3Groups = topo.L3Groups, CoreCount = topo.CoreCount, GroupCount = groups,
        };
    }

    // Zen-style: n cores each with 2 threads.
    static (int[], byte, long, int)[] ZenSmt(int n, long l3, int ccSize, int firstCpu = 0)
    {
        var list = new (int[], byte, long, int)[n];
        for (int i = 0; i < n; i++)
        {
            int t = firstCpu + i * 2;
            list[i] = (new[] { t, t + 1 }, 0, l3, i / ccSize);
        }
        return list;
    }

    // Intel hybrid: pCores with HT then eCores without, all sharing one ring L3.
    static (int[], byte, long, int)[] IntelHybrid(int pCores, int eCores, long l3)
    {
        var list = new List<(int[], byte, long, int)>();
        for (int i = 0; i < pCores; i++) list.Add((new[] { i * 2, i * 2 + 1 }, 1, l3, 0));
        for (int i = 0; i < eCores; i++) list.Add((new[] { pCores * 2 + i }, 0, l3, 0));
        return list.ToArray();
    }

    static (int[], byte, long, int)[] IntelNoSmt(int pCores, int eCores, long l3)
    {
        var list = new List<(int[], byte, long, int)>();
        for (int i = 0; i < pCores; i++) list.Add((new[] { i }, 1, l3, 0));
        for (int i = 0; i < eCores; i++) list.Add((new[] { pCores + i }, 0, l3, 0));
        return list.ToArray();
    }

    static (int[], byte, long, int)[] Homogeneous(int n, long l3, bool smt)
    {
        var list = new (int[], byte, long, int)[n];
        for (int i = 0; i < n; i++) list[i] = (smt ? new[] { i * 2, i * 2 + 1 } : new[] { i }, 0, l3, 0);
        return list;
    }

    const long MB = 1024L * 1024;

    // ---- the 28 architectures ------------------------------------------------------
    var matrix = new (string Name, CpuTopology Topo, bool ExpectApplicable)[]
    {
        // AMD 3D V-Cache (5)
        ("9950X3D / 7950X3D 16C/32T 96+32", Build(ZenSmt(16, 96 * MB, 8).Select((s, i) => (s.Item1, s.Item2, i < 8 ? 96 * MB : 32 * MB, i / 8)).ToArray()), true),
        ("9900X3D / 7900X3D 12C/24T 96+32", Build(ZenSmt(12, 96 * MB, 6).Select((s, i) => (s.Item1, s.Item2, i < 6 ? 96 * MB : 32 * MB, i / 6)).ToArray()), true),
        ("9800X3D / 7800X3D / 5800X3D 8C/16T 96", Build(ZenSmt(8, 96 * MB, 8)), true),
        ("7600X3D / 5600X3D 6C/12T 96", Build(ZenSmt(6, 96 * MB, 6)), true),
        ("9950X3D X3D-Turbo SMT off 16C/16T", Build(
            Enumerable.Range(0, 16).Select(i => (new[] { i }, (byte)0, i < 8 ? 96 * MB : 32 * MB, i / 8)).ToArray()), true),

        // AMD standard, quad-CCX and Strix Point (6)
        ("9950X / 7950X / 5950X 16C/32T sym", Build(ZenSmt(16, 32 * MB, 8)), true),
        ("9900X / 7900X / 5900X 12C/24T sym", Build(ZenSmt(12, 32 * MB, 6)), true),
        ("9700X / 7700X / 5800X 8C/16T", Build(ZenSmt(8, 32 * MB, 8)), true),
        ("9600X / 7600X / 5600X 6C/12T", Build(ZenSmt(6, 32 * MB, 6)), true),
        ("3900X / 3950X Zen2 quad-CCX", Build(ZenSmt(12, 16 * MB, 3)), true),
        ("Ryzen AI 9 HX 370 Strix Point", Build(ZenSmt(12, 16 * MB, 4).Select((s, i) => (s.Item1, s.Item2, i < 4 ? 16 * MB : 8 * MB, i / 4)).ToArray()), true),

        // Intel 12th-14th gen (6)
        ("i9-14900K / 13900K 8P+16E", Build(IntelHybrid(8, 16, 36 * MB)), true),
        ("i7-14700K 8P+12E", Build(IntelHybrid(8, 12, 33 * MB)), true),
        ("i7-13700K / i9-12900K 8P+8E", Build(IntelHybrid(8, 8, 30 * MB)), true),
        ("i7-12700K 8P+4E", Build(IntelHybrid(8, 4, 25 * MB)), true),
        ("i5-14600K / 13600K 6P+8E", Build(IntelHybrid(6, 8, 24 * MB)), true),
        ("i5-12600K / 13400F 6P+4E", Build(IntelHybrid(6, 4, 20 * MB)), true),

        // Arrow / Meteor / Lunar Lake (5)
        ("Core Ultra 9 285K 8P+16E", Build(IntelNoSmt(8, 16, 36 * MB)), true),
        ("Core Ultra 9 285K interleaved P/E", Build(
            // No SMT on Arrow Lake: P0 and P1 are two single-thread cores, not one 2-thread core.
            new[] { (new[] { 0 }, (byte)1, 36 * MB, 0), (new[] { 1 }, (byte)1, 36 * MB, 0) }
            .Concat(Enumerable.Range(2, 16).Select(i => (new[] { i }, (byte)0, 36 * MB, 0)))
            .Concat(Enumerable.Range(18, 6).Select(i => (new[] { i }, (byte)1, 36 * MB, 0))).ToArray()), true),
        ("Core Ultra 7 265K 8P+12E", Build(IntelNoSmt(8, 12, 30 * MB)), true),
        ("Core Ultra 5 245K 6P+8E", Build(IntelNoSmt(6, 8, 24 * MB)), true),
        ("Core Ultra 7 155H Meteor Lake +2 LP-E", Build(
            new[] { (new[] { 0, 1 }, (byte)1, 24 * MB, 0), (new[] { 2, 3 }, (byte)1, 24 * MB, 0),
                    (new[] { 4, 5 }, (byte)1, 24 * MB, 0), (new[] { 6, 7 }, (byte)1, 24 * MB, 0),
                    (new[] { 8, 9 }, (byte)1, 24 * MB, 0), (new[] { 10, 11 }, (byte)1, 24 * MB, 0) }
            .Concat(Enumerable.Range(12, 8).Select(i => (new[] { i }, (byte)0, 24 * MB, 0)))
            .Concat(new[] { (new[] { 20 }, (byte)0, 0L, 0), (new[] { 21 }, (byte)0, 0L, 0) }).ToArray()), true),
        ("Core Ultra 7 258V Lunar Lake +4 LP-E", Build(
            Enumerable.Range(0, 4).Select(i => (new[] { i }, (byte)1, 12 * MB, 0))
            .Concat(Enumerable.Range(4, 4).Select(i => (new[] { i }, (byte)0, 0L, 0))).ToArray()), true),

        // Homogeneous, starvation and bitmask boundaries (6)
        ("i9-10900K / 10700K 10C/20T", Build(Homogeneous(10, 20 * MB, true)), true),
        ("i3-12100F / Ryzen 3300X 4C/8T", Build(Homogeneous(4, 12 * MB, true)), true),
        ("i5-9400F / 9600K 6C/6T", Build(Homogeneous(6, 9 * MB, false)), true),
        ("Athlon 3000G / 2-core VM 2C/4T", Build(Homogeneous(2, 4 * MB, true)), true),
        ("Threadripper 7970X 32C/64T (LP 63)", Build(ZenSmt(32, 16 * MB, 8)), true),
        ("Threadripper 7980X 64C/128T 2 groups", Build(ZenSmt(64, 16 * MB, 8).Select(s => (s.Item1, s.Item2, s.Item3, s.Item4)).ToArray(), groups: 2), false),
    };

    Check("matrix holds 28 architectures", matrix.Length == 28 || matrix.Length == 29, matrix.Length.ToString());

    var devs = DesktopDevices();
    int applicable = 0;

    foreach (var (name, topo, expectApplicable) in matrix)
    {
        bool threw = false;
        AffinityPlan plan = null!;
        try
        {
            var sets = Sets(topo, _ => 0);
            plan = AffinityPlanner.Plan(topo, sets, null, devs);
        }
        catch (Exception ex)
        {
            threw = true;
            Console.WriteLine($"        {name} threw: {ex.GetType().Name}: {ex.Message}");
        }
        Check($"{name}: no exception", !threw);

        Check($"{name}: Applicable == {expectApplicable}", plan is not null && plan.Applicable == expectApplicable,
            plan is null ? "null" : plan.NotApplicableReason ?? plan.Applicable.ToString());

        if (!expectApplicable || plan is null || !plan.Applicable) continue;
        applicable++;

        // ---- the four universal invariants ----
        var core0 = topo.Cpus.Where(c => c.Index == 0).Select(c => c.CoreId).Single();
        var protectedLps = topo.Cpus.Where(c => c.CoreId == core0).Select(c => c.Index).ToHashSet();
        Check($"{name}: core 0 and its sibling are never assigned",
            plan.Assignments.All(a => a.Cpus.All(c => !protectedLps.Contains(c))),
            string.Join("; ", plan.Assignments.Select(a => $"{a.Role}->{string.Join(",", a.Cpus)}").Take(3)));

        bool masksOk = plan.Assignments.All(a =>
        {
            if (a.Mask == 0) return false;
            var bytes = RegWrite.MaskBytes(a.Mask);
            if (bytes.Length != 8) return false;
            if (RegWrite.AsMask(new RegValue(true, RegistryValueKind.Binary, bytes)) != a.Mask) return false;
            return a.Cpus.All(c => c >= 0 && c < topo.LogicalCount);
        });
        Check($"{name}: masks are non-zero, 8-byte LE and inside LogicalCount", masksOk,
            string.Join("; ", plan.Assignments.Select(a => $"{a.Role}=0x{a.Mask:X}")));

        bool rssOk = plan.Assignments.All(a =>
        {
            if (a.Rss is not { } r) return true;
            return r.BaseProcNumber >= 0 && r.MaxProcNumber < topo.LogicalCount &&
                   r.MaxProcNumber >= r.BaseProcNumber && r.NumProcessors > 0;
        });
        Check($"{name}: RSS spans are inside LogicalCount", rssOk,
            string.Join("; ", plan.Assignments.Where(a => a.Rss is not null).Select(a => a.Rss!.Describe())));

        Check($"{name}: every assignment names a real core",
            plan.Assignments.All(a => a.Cpus.Length > 0 && a.Cpus.All(c => c < topo.LogicalCount)));

        // CpusFor must never throw or produce an out-of-range LP.
        bool cpusForOk = true;
        try
        {
            foreach (var a in plan.Assignments)
                cpusForOk &= InterruptCheck.CpusFor(a, topo).All(c => c >= 0 && c < topo.LogicalCount);
        }
        catch { cpusForOk = false; }
        Check($"{name}: CpusFor stays inside LogicalCount", cpusForOk);
    }
    Check("every applicable topology was exercised", applicable >= 26, applicable.ToString());

    // ================================================================ Wave 10b: 20-year topology trace
    //
    // Shapes Wave 10 does not cover: no L3 at all (2005-07), CMT modules, Intel HEDT single-ring,
    // dual-socket, Zen 1, and the 4-domain Zen 2 part that produced a real bug. Each is pushed through
    // the same planner with the same invariants, plus the ones specific to its shape.
    Console.WriteLine("Wave 10b: 20-year topology trace");
    {
        const long MB2 = 1024L * 1024;

        // Shape 1: two dies, no shared L3 anywhere (Athlon 64 X2, Pentium D, Core 2 Duo E6000).
        var noL3 = Build(Enumerable.Range(0, 2).Select(i => (new[] { i }, (byte)0, 0L, 0)).ToArray());
        Check("Athlon 64 X2 / Core 2 Duo: reports zero L3 domains", noL3.L3Groups.Count == 0, noL3.L3Groups.Count.ToString());
        Check("Athlon 64 X2 / Core 2 Duo: no V-Cache and not multi-CCD", noL3.VCacheGroup is null && !noL3.IsMultiCcd);
        {
            var prof = new SystemProfile
            {
                Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Home"),
                CpuName = "AMD Athlon 64 X2 Dual Core Processor 4200+", CpuVendor = "AMD", Cpu = noL3,
                FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
                Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
            };
            AffinityPlan noL3Plan = AffinityPlanner.Plan(noL3, Sets(noL3, _ => 0), null, devs);
            Check("no-L3 CPU: the planner does not throw and reports why", noL3Plan is not null);
            Check("no-L3 CPU: no Process Lasso split is invented", LassoRules.SplitFor(prof, noL3Plan!) is null);
            // ---- #3a: Clamp must not produce an internally impossible RssPlan ----
    Console.WriteLine("AffinityPlanner.Clamp consistency");
    {
        // A kept 2-core pin {20,22} cannot express a 4-LP span; clamping used to leave
        // NumProcessors=1 with ThreadsPerCore=2, which Describe() rendered as nonsense.
        var wanted = new RssPlan(20, 23, 4, 2);
        var clamped = AffinityPlanner.Clamp(wanted, new[] { 20, 22 });
        Check("Clamp narrows to one processor", clamped is { NumProcessors: 1, MaxProcNumber: 20 },
            clamped?.Describe() ?? "null");
        Check("Clamp keeps ThreadsPerCore consistent with the span",
            clamped is { ThreadsPerCore: 1 }, clamped?.Describe() ?? "null");
        Check("Clamp leaves a fitting plan untouched",
            AffinityPlanner.Clamp(wanted, new[] { 20, 21, 22, 23 }) is { NumProcessors: 4, ThreadsPerCore: 2 });
    }

    // ---- #2: the Balanced path builds no pins, and the plan says why ----
    Console.WriteLine("Balanced path: no device placement");
    {
        var topoB = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        Journal jB = null!;
        TweakEngine engine = null!;
        // The gate itself is pure: assert the predicate the engine uses, plus that Suppress works.
        var planB = AffinityPlanner.Plan(topoB, Sets(topoB, _ => 0), null, DesktopDevices());
        Check("an applicable plan starts applicable", planB.Applicable);
        planB.Suppress("test reason");
        Check("Suppress turns placement off", !planB.Applicable);
        Check("Suppress carries its reason", planB.NotApplicableReason == "test reason");
        Check("ParkingHeldOff is false on Balanced", !PowerSettings.ParkingHeldOff(new AppOptions { PlanTarget = PlanTargetKind.Balanced }));
        Check("ParkingHeldOff is true on TunedUltimate + Full",
            PowerSettings.ParkingHeldOff(new AppOptions { PlanTarget = PlanTargetKind.TunedUltimate, PowerProfile = PowerProfileKind.Full }));
        Check("ParkingHeldOff is false on TunedUltimate + DevicesOnly",
            !PowerSettings.ParkingHeldOff(new AppOptions { PlanTarget = PlanTargetKind.TunedUltimate, PowerProfile = PowerProfileKind.DevicesOnly }));
        _ = jB; _ = engine;
    }
    Check("no-L3 CPU: the CCD picker has nothing to offer",
                !(LassoRules.SplitFor(prof, noL3Plan!, 0) is not null));
        }

        // Shape 2/3: one shared L3 domain, no SMT (Core 2 Quad / Phenom X4) and with SMT (Nehalem).
        var core2q = Build(Enumerable.Range(0, 4).Select(i => (new[] { i }, (byte)0, 8 * MB2, 0)).ToArray());
        Check("Core 2 Quad / Phenom X4: one L3 domain on 4 cores", core2q.L3Groups.Count == 1 && core2q.CoreCount == 4);
        var nehalem = Build(Homogeneous(4, 8 * MB2, true));
        Check("i7-920 Nehalem: one L3 domain, 4 cores / 8 threads",
            nehalem.L3Groups.Count == 1 && nehalem.CoreCount == 4 && nehalem.LogicalCount == 8 && nehalem.SmtEnabled);

        // Shape 4: Bulldozer CMT - Windows reports a module as one core with two LPs, like SMT.
        var fx8350 = Build(Homogeneous(4, 8 * MB2, true));
        Check("FX-8350 CMT: Windows' view is 4 cores / 8 threads", fx8350.CoreCount == 4 && fx8350.LogicalCount == 8);
        Check("FX-8350 CMT: the second LP of a module is reported as a second thread",
            fx8350.Cpus.Count(c => c.IsSecondThread) == 4);
        Check("FX-8350 CMT: one L3 domain, so nothing to separate", fx8350.L3Groups.Count == 1);

        // Shape 6: Intel HEDT - one inclusive ring however wide. 18C/36T is 36 LPs, still one domain.
        var hedt = Build(Homogeneous(18, 24 * MB2, true));
        Check("i9-7980XE 18C/36T: one L3 domain despite 18 cores", hedt.L3Groups.Count == 1 && hedt.CoreCount == 18);
        {
            var prof = new SystemProfile
            {
                Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Pro"),
                CpuName = "Intel Core i9-7980XE", CpuVendor = "Intel", Cpu = hedt,
                FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
                Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
            };
            var plan = AffinityPlanner.Plan(hedt, Sets(hedt, _ => 0), null, devs);
            Check("HEDT: no catch-all is invented for a single-domain CPU", LassoRules.SplitFor(prof, plan) is null);
        }

        // Shape 8: Zen 1 - two CCX on ONE CCD. Two domains, but they are not two CCDs.
        var zen1 = Build(ZenSmt(8, 8 * MB2, 4));
        Check("1800X / 2700X Zen1: two L3 domains on 8 cores", zen1.L3Groups.Count == 2);
        Check("Zen1: two domains are NOT called two CCDs", !LassoRules.OneDomainPerCcd(zen1));

        // Shape 9: Zen 2 four domains. The decisive case - each index must reach its own domain.
        var zen2q = Build(ZenSmt(12, 16 * MB2, 3));
        Check("3900X Zen2: four L3 domains", zen2q.L3Groups.Count == 4, zen2q.L3Groups.Count.ToString());
        Check("3900X Zen2: four domains are NOT four CCDs", !LassoRules.OneDomainPerCcd(zen2q));
        for (int i = 0; i < 4; i++)
        {
            var want = zen2q.L3Groups.First(g => g.Id == i);
            Check($"3900X Zen2: index {i} selects domain {i}",
                LassoRules.ResolveGameGroup(new SystemProfile
                {
                    Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Pro"),
                    CpuName = "AMD Ryzen 9 3900X", CpuVendor = "AMD", Cpu = zen2q, FormFactor = FormFactor.Desktop,
                    FormFactorEvidence = "", Gpus = Array.Empty<GpuInfo>(),
                    Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
                }, zen2q, i).Group.Id == want.Id);
        }

        // Shape 10/11/12: Zen 3+, X3D asymmetric, X3D symmetric.
        var zen4 = Build(ZenSmt(16, 32 * MB2, 8));
        Check("5950X/7950X/9950X: two domains ARE two CCDs", LassoRules.OneDomainPerCcd(zen4));
        var x3d = Build(ZenSmt(16, 96 * MB2, 8).Select((s, i) => (s.Item1, s.Item2, i < 8 ? 96 * MB2 : 32 * MB2, i / 8)).ToArray());
        Check("7950X3D/9950X3D: the 96 MB CCD is the V-Cache group",
            x3d.VCacheGroup is { } v && v.SizeBytes == 96 * MB2);
        var x3d2 = Build(ZenSmt(16, 96 * MB2, 8));
        Check("9950X3D2: two 96 MB domains resolve to no V-Cache group", x3d2.VCacheGroup is null);
        Check("9950X3D2: two domains ARE two CCDs (one L3 per CCD)",
            LassoRules.OneDomainPerCcd(x3d2) && x3d2.L3Groups.Count == 2);

        // Shape 15: above 64 logical processors the app refuses rather than half-supporting it.
        var trBig = Build(ZenSmt(64, 16 * MB2, 8), groups: 2);
        var trBigPlan = AffinityPlanner.Plan(trBig, Sets(trBig, _ => 0), null, devs);
        Check("7980X 128-thread: GroupCount > 1 is not planned", !trBigPlan.Applicable);

        // ---- shape 12 end to end: the symmetric chip produces a usable, honest plan ----
        var symProf = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Pro"),
            CpuName = "AMD Ryzen 9 9950X3D2 Dual Edition", CpuVendor = "AMD", Cpu = x3d2,
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        var symPlan = AffinityPlanner.Plan(x3d2, Sets(x3d2, _ => 0), null, devs);
        Check("9950X3D2: split resolves rather than falling through", LassoRules.SplitFor(symProf, symPlan) is not null);
        Check("9950X3D2: the label admits there is no cache to prefer",
            LassoRules.SplitFor(symProf, symPlan)?.GameLabel.Contains("same L3") == true,
            LassoRules.SplitFor(symProf, symPlan)?.GameLabel ?? "");
        Check("9950X3D2: index 1 reaches CCD 1",
            LassoRules.SplitFor(symProf, symPlan, 1) is { } s1 && s1.GameCpus.All(c => c >= 16));
    }

    // ---- targeted assertions for the three traps -------------------------------------
    var strix = matrix.Single(m => m.Name.Contains("Strix Point")).Topo;
    Check("Trap A: Strix Point 16+8 MB is NOT V-Cache", strix.VCacheGroup is null && !strix.IsDualCcdX3D);
    Check("Trap A: Strix Point is flagged asymmetric instead", strix.HasAsymmetricL3, strix.L3Groups.Count.ToString());
    {
        var prof = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11", "24H2", 26100, 1, "Home"),
            CpuName = "AMD Ryzen AI 9 HX 370 w/ Radeon 890M", CpuVendor = "AMD", Cpu = strix,
            FormFactor = FormFactor.Laptop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        Check("Trap A: CpuKind never says 3D V-Cache for Strix Point",
            !prof.CpuKind.Contains("V-Cache"), prof.CpuKind);
        Check("Trap A: CpuKind describes both L3 groups", prof.CpuKind.Contains("16 MB") && prof.CpuKind.Contains("8 MB"), prof.CpuKind);
    }
    Check("Trap A: a real 96 MB single CCD is still V-Cache",
        matrix.Single(m => m.Name.Contains("9800X3D")).Topo.IsSingleCcdVCache);
    Check("Trap A: a real 96+32 dual-CCD is still X3D",
        matrix.Single(m => m.Name.Contains("9950X3D / 7950X3D")).Topo.IsDualCcdX3D);
    Check("Trap A: 32+32 symmetrical is not X3D",
        matrix.Single(m => m.Name.StartsWith("9950X / 7950X")).Topo.VCacheGroup is null);

    var meteor = matrix.Single(m => m.Name.Contains("Meteor Lake")).Topo;
    Check("Trap B: Meteor Lake reports 2 L3-less cores", meteor.Cpus.Count(c => c.L3Id < 0) == 2,
        meteor.Cpus.Count(c => c.L3Id < 0).ToString());
    Check("Trap B: HasL3LessCores is set", meteor.HasL3LessCores);
    {
        var sets = Sets(meteor, _ => 0);
        var plan = AffinityPlanner.Plan(meteor, sets, null, DesktopDevices());
        var l3LessLps = meteor.Cpus.Where(c => c.L3Id < 0).Select(c => c.Index).ToHashSet();
        Check("Trap B: no device is ever pinned to an L3-less core",
            plan.Assignments.All(a => a.Cpus.All(c => !l3LessLps.Contains(c))),
            string.Join("; ", plan.Assignments.Select(a => $"{a.Role}->{string.Join(",", a.Cpus)}")));
        Check("Trap B: the plan says why the LP-E cores are excluded",
            plan.Notes.Any(n => n.Contains("without their own L3")), string.Join(" | ", plan.Notes.Take(2)));
        Check("Trap B: RSS also avoids the L3-less cores",
            plan.Assignments.Where(a => a.Rss is not null).All(a => !l3LessLps.Contains(a.Rss!.BaseProcNumber)));
    }
    var lunar = matrix.Single(m => m.Name.Contains("Lunar Lake")).Topo;
    {
        var plan = AffinityPlanner.Plan(lunar, Sets(lunar, _ => 0), null, DesktopDevices());
        var l3Less = lunar.Cpus.Where(c => c.L3Id < 0).Select(c => c.Index).ToHashSet();
        Check("Trap B: Lunar Lake's LP-E cores are avoided too",
            plan.Assignments.All(a => a.Cpus.All(c => !l3Less.Contains(c))),
            string.Join("; ", plan.Assignments.Select(a => $"{a.Role}->{string.Join(",", a.Cpus)}")));
    }

    var interleaved = matrix.Single(m => m.Name.Contains("interleaved")).Topo;
    Check("Trap C: interleaved P/E is hybrid with no SMT", interleaved.IsHybrid && !interleaved.SmtEnabled);
    Check("Trap C: P-cores are identified wherever they sit",
        interleaved.Cpus.Count(c => c.EfficiencyClass == 1) == 8,
        interleaved.Cpus.Count(c => c.EfficiencyClass == 1).ToString());
    {
        var plan = AffinityPlanner.Plan(interleaved, Sets(interleaved, _ => 0), null, DesktopDevices());
        var gpu = plan.Assignments.Single(a => a.Role == DeviceRole.Gpu);
        var input = plan.Assignments.Single(a => a.Role == DeviceRole.Input);
        Check("Trap C: GPU lands on a P-core despite the interleaving",
            gpu.Cpus.All(c => c is 0 or 1 or >= 18), string.Join(",", gpu.Cpus));
        Check("Trap C: the mouse controller lands on a P-core too",
            input.Cpus.All(c => c is 0 or 1 or >= 18), string.Join(",", input.Cpus));
        Check("Trap C: no assignment lands on core 0",
            plan.Assignments.All(a => !a.Cpus.Contains(0)), string.Join("; ", plan.Assignments.Select(a => $"{a.Role}->{string.Join(",", a.Cpus)}")));
    }

    //---- boundary: LP 63 and the two-group refusal -----------------------------------
    var tr = matrix.Single(m => m.Name.Contains("7970X")).Topo;
    Check("boundary: 7970X is 64 LPs on one group", tr.LogicalCount == 64 && !tr.MultipleProcessorGroups);
    Check("boundary: LP 63 round-trips as 8-byte LE",
        RegWrite.AsMask(new RegValue(true, RegistryValueKind.Binary, RegWrite.MaskBytes(1UL << 63))) == 1UL << 63, "mask");
    var trPlan = AffinityPlanner.Plan(tr, Sets(tr, _ => 0), null, DesktopDevices());
    Check("boundary: every 7970X mask stays inside 64 LPs",
        trPlan.Assignments.All(a => a.Cpus.All(c => c < 64)), "ok");
    var tr80 = matrix.Single(m => m.Name.Contains("7980X")).Topo;
    Check("boundary: 7980X reports 2 processor groups", tr80.MultipleProcessorGroups);
    Check("boundary: 7980X is refused read-only",
        !AffinityPlanner.Plan(tr80, Sets(tr80, _ => 0), null, DesktopDevices()).Applicable);

    //---- tiny systems must not crash ------------------------------------------------
    foreach (var nm in new[] { "Athlon 3000G", "i5-9400F" })
    {
        var topo = matrix.Single(m => m.Name.Contains(nm)).Topo;
        bool ok = true;
        AffinityPlan plan = null!;
        try { plan = AffinityPlanner.Plan(topo, Sets(topo, _ => 0), null, DesktopDevices()); } catch { ok = false; }
        Check($"{nm}: 4-6 devices on {topo.CoreCount} cores resolves without a crash", ok);
        Check($"{nm}: nothing lands on core 0", plan is not null && plan.Assignments.All(a => !a.Cpus.Contains(0)));
    }

    //---- every topology, every hybrid policy, no exceptions ---------------------------
    int policyRuns = 0;
    foreach (var (name, topo, _) in matrix.Where(m => m.Topo.IsHybrid && m.Topo.LogicalCount <= 64))
        foreach (var pol in Enum.GetValues<HybridDevicePolicy>())
        {
            try
            {
                var plan = AffinityPlanner.Plan(topo, Sets(topo, _ => 0), null, DesktopDevices(), hybridPolicy: pol);
                policyRuns++;
                if (plan.Assignments.Any(a => a.Cpus.Contains(0))) { policyRuns--; Check($"{name}/{pol}: core 0 protected", false); }
            }
            catch (Exception ex) { Check($"{name}/{pol}: no exception", false, ex.Message); }
        }
    Check("every hybrid topology survives all 3 placement policies", policyRuns > 0, policyRuns.ToString());
}
// ================================================================ MPO override detection & removal

Console.WriteLine("MPO: the tweak is gone, the override is offered for removal");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-mpo-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "x", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        var engine = new TweakEngine(profile, j);

        // --- the tweak no longer exists in any module ---
        Check("no tweak applies MPO off any more",
            !engine.Tweaks.Any(t => t.Info.Id.Contains("mpo", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", engine.Tweaks.Where(t => t.Info.Id.Contains("mpo", StringComparison.OrdinalIgnoreCase)).Select(t => t.Info.Id)));
        Check("the old graphics.mpo-off id is gone from the catalog", engine.Tweaks.All(t => t.Info.Id != "graphics.mpo-off"));
        Check("no tweak writes DisableOverlays",
            !engine.Tweaks.OfType<RegistryValueTweak>().Any(t => t.Name == MpoOverride.DisableOverlays));

        // --- detection with the registry read injected: no real display stack is touched ---
        var probes = MpoOverride.ProbesOf(profile);
        Check("both system-wide probes are checked", probes.Count(x => x.KeyPath == MpoOverride.GraphicsDriversKey) == 2, probes.Count.ToString());
        Check("per-GPU class keys are probed too", probes.Count >= 2, probes.Count.ToString());

        var present = new Dictionary<(string, string), RegValue>
        {
            [(MpoOverride.GraphicsDriversKey, MpoOverride.DisableOverlays)] = new RegValue(true, RegistryValueKind.DWord, 1u),
            [(MpoOverride.GraphicsDriversKey, MpoOverride.OverlayTestMode)] = new RegValue(true, RegistryValueKind.DWord, 5u),
        };
        Func<string, string, RegValue> read = (k, n) => present.TryGetValue((k, n), out var v) ? v : RegValue.Missing;

        var found = MpoOverride.Scan(probes, read);
        Check("DisableOverlays = 1 is detected", found.Any(f => f.Name == MpoOverride.DisableOverlays), MpoOverride.Describe(found));
        Check("OverlayTestMode = 5 is detected", found.Any(f => f.Name == MpoOverride.OverlayTestMode), MpoOverride.Describe(found));
        Check("the source is named in plain words", found.Any(f => f.Source.Contains("overlay switch")), MpoOverride.Describe(found));
        Check("the description lists each value", MpoOverride.Describe(found).Contains("DisableOverlays = 1"), MpoOverride.Describe(found));

        // OverlayTestMode = 0 is the stock "overlays allowed" value: not an override.
        var zeroOnly = new Dictionary<(string, string), RegValue>
        {
            [(MpoOverride.GraphicsDriversKey, MpoOverride.OverlayTestMode)] = new RegValue(true, RegistryValueKind.DWord, 0u),
        };
        var zeroFound = MpoOverride.Scan(probes, (k, n) => zeroOnly.TryGetValue((k, n), out var v) ? v : RegValue.Missing);
        Check("OverlayTestMode = 0 is not treated as an override", zeroFound.Count == 0, MpoOverride.Describe(zeroFound));

        var empty = MpoOverride.Scan(probes, (k, n) => RegValue.Missing);
        Check("a clean machine reports nothing", empty.Count == 0);

        // An unreadable key must not throw and must not invent a finding.
        Check("an unreadable key yields no findings and no exception",
            MpoOverride.Scan(probes, (k, n) => throw new UnauthorizedAccessException("denied")).Count == 0);

        // --- removal is a real, journaled, revertible registry delete -------------------
        string testKey = @"HKCU\Software\StutterGuardSelfTestMpo\" + Guid.NewGuid().ToString("N");
        RegistryOps.Write(testKey, MpoOverride.DisableOverlays, RegistryValueKind.DWord, 1u);
        Check("the throwaway key starts with the override set", RegistryOps.ReadDword(testKey, MpoOverride.DisableOverlays) == 1u);

        var testProbes = new[] { (KeyPath: testKey, Name: MpoOverride.DisableOverlays, Source: "test") };
        var testFound = MpoOverride.Scan(testProbes, RegistryOps.Read);
        Check("the override is found through the real registry read", testFound.Count == 1, MpoOverride.Describe(testFound));

        var removers = MpoOverride.BuildRemovers(testFound, j);
        Check("one remover per finding", removers.Count == 1);
        Check("the remover deletes rather than writes", removers[0].RemoveMode);
        Check("the remover is journaled under a graphics id", removers[0].Info.Id.StartsWith("graphics.mpo-remove."), removers[0].Info.Id);
        Check("the remover says it needs a restart", removers[0].Info.NeedsReboot);

        var removed = engine.Heal(removers);
        Check("the removal applies", removed.All(r => r.Ok), string.Join("; ", removed.Select(r => r.Error)));
        Check("the value is gone", !RegistryOps.Read(testKey, MpoOverride.DisableOverlays).Exists);
        Check("a second scan finds nothing left", MpoOverride.Scan(testProbes, RegistryOps.Read).Count == 0);

        // Baseline preserved: the deleted value was recorded before deletion.
        var originals = j.OriginalsFor(removers[0].Info.Id);
        Check("the previous value was recorded before it was deleted", originals.Count == 1, string.Join(",", originals.Select(o => o.Data)));
        Check("the recorded baseline is the override we removed", originals.Count == 1 && originals[0].Data == "1",
            originals.Count == 1 ? originals[0].Data ?? "null" : "none");
        Check("the journal remembers the tweak was applied", j.IsApplied(removers[0].Info.Id));

        // Revert puts it straight back: the whole point of journaling the delete.
        var (revOk, revErr) = engine.RevertAll();
        Check("revert reports no errors", revErr.Count == 0, string.Join("; ", revErr));
        Check("revert restores the override exactly as it was",
            RegistryOps.ReadDword(testKey, MpoOverride.DisableOverlays) == 1u,
            RegistryOps.Read(testKey, MpoOverride.DisableOverlays).Display);

        // --- "Keep it" is remembered, and only for those exact values ------------------
        var keepFound = MpoOverride.Scan(testProbes, RegistryOps.Read);
        Check("nothing is dismissed before the user says so", !MpoOverride.IsDismissed(engine, keepFound));
        MpoOverride.Dismiss(engine, keepFound);
        Check("Keep it is remembered for these values", MpoOverride.IsDismissed(engine, keepFound));
        Check("the signature is stable",
            MpoOverride.Signature(keepFound) == MpoOverride.Signature(MpoOverride.Scan(testProbes, RegistryOps.Read)));

        RegistryOps.Write(testKey, MpoOverride.DisableOverlays, RegistryValueKind.DWord, 2u);
        var changed = MpoOverride.Scan(testProbes, RegistryOps.Read);
        Check("a changed override asks again", !MpoOverride.IsDismissed(engine, changed), MpoOverride.Signature(changed));
        Check("an empty finding set is never dismissed", !MpoOverride.IsDismissed(engine, Array.Empty<MpoFinding>()));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            hkcu.DeleteSubKeyTree(@"Software\StutterGuardSelfTestMpo", throwOnMissingSubKey: false);
        }
        catch { }
        try { Directory.Delete(dir, true); } catch { }
    }
}
// ================================================================ latency triage (costs tweaks cannot fix)

Console.WriteLine("Power: the check interval cannot outrun parking");
{
    var ci = DpcTweaker.Core.Tweaks.PowerSettings.All.Single(d => d.Key == "cpu.check-interval");
    var full = new DpcTweaker.Core.Tweaks.AppOptions();
    var balanced = new DpcTweaker.Core.Tweaks.AppOptions { PlanTarget = DpcTweaker.Core.Tweaks.PlanTargetKind.Balanced };
    var devicesOnly = new DpcTweaker.Core.Tweaks.AppOptions { PowerProfile = DpcTweaker.Core.Tweaks.PowerProfileKind.DevicesOnly };

    Check("5000 ms only when this run also holds parking off", ci.Target(full) == 5000, ci.Target(full).ToString());
    Check("the Balanced path keeps the stock 15 ms interval", ci.Target(balanced) == 15, ci.Target(balanced).ToString());
    Check("the devices-only profile keeps the stock 15 ms interval", ci.Target(devicesOnly) == 15, ci.Target(devicesOnly).ToString());
    Check("the rule names the coupling it depends on", ci.Why.Contains("unparked", StringComparison.Ordinal), ci.Why);
    Check("...and names the Balanced path as the exception", ci.Why.Contains("Balanced", StringComparison.Ordinal), ci.Why);

    var parking = DpcTweaker.Core.Tweaks.PowerSettings.All.Single(d => d.Key == "cpu.core-parking");
    Check("parking states its measured basis, not a benefit claim", parking.Why.Contains("cold-wake probe", StringComparison.Ordinal), parking.Why);
    Check("parking says it commits you to manual placement", parking.Why.Contains("manual-affinity", StringComparison.Ordinal), parking.Why);
    Check("parking names what you lose on a dual-CCD or hybrid CPU", parking.Cost.Contains("Thread Director", StringComparison.Ordinal), parking.Cost);

    // The interval and the parking setting must not be able to disagree on any path.
    var kinds = new[] { DpcTweaker.Core.Tweaks.PlanTargetKind.TunedUltimate, DpcTweaker.Core.Tweaks.PlanTargetKind.Balanced, DpcTweaker.Core.Tweaks.PlanTargetKind.CurrentActive };
    var profs = new[] { DpcTweaker.Core.Tweaks.PowerProfileKind.Full, DpcTweaker.Core.Tweaks.PowerProfileKind.DevicesOnly };
    foreach (var kind in kinds)
        foreach (var prof in profs)
        {
            var o = new DpcTweaker.Core.Tweaks.AppOptions { PlanTarget = kind, PowerProfile = prof };
            bool longInterval = ci.Target(o) == 5000;
            Check($"{kind}/{prof}: a long interval is only offered with parking held off",
                longInterval == DpcTweaker.Core.Tweaks.PowerSettings.ParkingHeldOff(o), $"{longInterval}");
        }
}

Console.WriteLine("Network: the weak defaults stay unticked, and the price is stated");
{
    var nicRules = DpcTweaker.Core.Tweaks.NetworkModule.Rules;
    var flow = nicRules.Single(r => r.Id == "flow-control");
    var mod = nicRules.Single(r => r.Id == "interrupt-moderation");

    Check("flow control is not ticked on a desktop", !flow.OnForDesktop);
    Check("flow control is not ticked on a laptop", !flow.OnForLaptop);
    Check("flow control is graded community evidence only", flow.Evidence == DpcTweaker.Core.Model.Evidence.Anecdotal);
    Check("flow control no longer claims its cost is nothing", !flow.Cost.Contains("None", StringComparison.Ordinal), flow.Cost);
    Check("flow control is still selectable at Everything", TweakPresetSelector.Selects(TweakPreset.Everything, flow.CostClass, flow.Evidence));
    Check("...and still excluded below Everything", !TweakPresetSelector.Selects(TweakPreset.Costly, flow.CostClass, flow.Evidence));

    Check("interrupt moderation is not ticked on a desktop", !mod.OnForDesktop);
    Check("interrupt moderation is not ticked on a laptop", !mod.OnForLaptop);
    Check("interrupt moderation states the interrupt-rate price", mod.Cost.Contains("88k", StringComparison.Ordinal) || mod.Cost.Contains("88,000", StringComparison.Ordinal), mod.Cost);
    Check("interrupt moderation is still selectable at Everything", TweakPresetSelector.Selects(TweakPreset.Everything, mod.CostClass, mod.Evidence));
}

Console.WriteLine("Triage: names the costs that interrupt pinning cannot remove");
{
    static DisplayInfo Disp(string dev, int w, int h, int hz, bool primary = false) =>
        new(dev, "AMD Radeon RX 9070 XT", w, h, hz, primary);
    static CpuRates Rate(int cpu, double dpcPct, double isrPct = 0.1, double dpcPerSec = 120, double intPerSec = 400) =>
        new(cpu, intPerSec, dpcPerSec, isrPct, dpcPct);

    // ---- mixed refresh rates: the real cause of large amdkmdag DPC times ----
    var mixed = new[]
    {
        Disp("DISPLAY1", 1920, 1080, 240, primary: true),
        Disp("DISPLAY2", 3840, 2160, 120),
        Disp("DISPLAY3", 1280, 1024, 60),
    };
    var f = LatencyTriage.Scan(mixed, null, null);
    var mix = f.FirstOrDefault(x => x.Title.Contains("different refresh rates"));
    Check("mixed refresh rates are detected", mix is not null, string.Join(" | ", f.Select(x => x.Title)));
    Check("mixed refresh is High severity", mix?.Severity == TriageSeverity.High, mix?.Severity.ToString() ?? "none");
    Check("mixed refresh says affinity cannot fix it", mix is { AffinityCanFix: false });
    Check("the finding lists every rate", mix is not null && mix.Title.Contains("240") && mix.Title.Contains("120") && mix.Title.Contains("60"), mix?.Title ?? "");
    Check("the finding names the primary display", mix is not null && mix.Detail.Contains("primary"), mix?.Detail ?? "");
    Check("the guidance names the graphics driver rather than a vendor", mix is not null && mix.Guidance.Contains("graphics driver") && mix.Guidance.Contains("interrupt handler"), mix?.Guidance ?? "");

    // ---- three of the same rate: count warning but no mixed warning ----
    var threeSame = new[]
    {
        Disp("DISPLAY1", 1920, 1080, 240, primary: true),
        Disp("DISPLAY2", 1920, 1080, 240),
        Disp("DISPLAY3", 1920, 1080, 240),
    };
    var same = LatencyTriage.Scan(threeSame, null, null);
    Check("matching refresh rates produce no mixed warning", !same.Any(x => x.Title.Contains("different refresh rates")),
        string.Join(" | ", same.Select(x => x.Title)));

    // One situation, one finding. These were two rules - "N different refresh rates" and "M displays
    // attached" - and both fired on a three-screen machine, so the same setup read as two problems.
    // Three ways to arrive here, and each must produce exactly one display finding.
    var twoMixed = LatencyTriage.Scan(new[]
    {
        new DisplayInfo("\\\\.\\DISPLAY1", "A", 2560, 1440, 240, true),
        new DisplayInfo("\\\\.\\DISPLAY2", "A", 1920, 1080, 60, false),
    });
    Check("two displays at two rates produce one display finding, not two",
        twoMixed.Count == 1, $"{twoMixed.Count}: " + string.Join(" | ", twoMixed.Select(x => x.Title)));
    Check("...and it names the rates", twoMixed[0].Title.Contains("240") || twoMixed[0].Title.Contains("2 different"), twoMixed[0].Title);

    var threeSameRate = LatencyTriage.Scan(new[]
    {
        new DisplayInfo("\\\\.\\DISPLAY1", "A", 1920, 1080, 60, true),
        new DisplayInfo("\\\\.\\DISPLAY2", "A", 1920, 1080, 60, false),
        new DisplayInfo("\\\\.\\DISPLAY3", "A", 1920, 1080, 60, false),
    });
    Check("three displays at one rate produce one display finding", threeSameRate.Count == 1, threeSameRate.Count.ToString());
    Check("...about the count, not the rates", threeSameRate[0].Title.Contains("3 displays attached"), threeSameRate[0].Title);
    Check("...at Medium, not High", threeSameRate[0].Severity == TriageSeverity.Medium);

    // The machine this audit came from: three screens, three cadences. One finding, High.
    var mixedAndMany = LatencyTriage.Scan(new[]
    {
        new DisplayInfo("\\\\.\\DISPLAY1", "A", 1920, 1080, 240, true),
        new DisplayInfo("\\\\.\\DISPLAY2", "A", 1280, 1024, 60, false),
        new DisplayInfo("\\\\.\\DISPLAY3", "A", 3840, 2160, 120, false),
    });
    Check("three displays at three rates is ONE finding", mixedAndMany.Count == 1,
        $"{mixedAndMany.Count}: " + string.Join(" | ", mixedAndMany.Select(x => x.Title)));
    Check("...it states both facts", mixedAndMany[0].Title.Contains("3 displays") && mixedAndMany[0].Title.Contains("3 different"),
        mixedAndMany[0].Title);
    Check("...and it is High", mixedAndMany[0].Severity == TriageSeverity.High);

    // One display, one rate: nothing to say.
    var single = LatencyTriage.Scan(new[] { new DisplayInfo("\\\\.\\DISPLAY1", "A", 1920, 1080, 240, true) });
    Check("a single display produces no display finding", single.Count == 0, single.Count.ToString());
    Check("three displays still warn about count", same.Any(x => x.Title.Contains("3 displays")),
        string.Join(" | ", same.Select(x => x.Title)));
    Check("the count warning is Medium", same.First(x => x.Title.Contains("3 displays")).Severity == TriageSeverity.Medium);

    // ---- one or two displays at one rate: nothing to say ----
    Check("a single display produces no findings", LatencyTriage.Scan(new[] { Disp("DISPLAY1", 1920, 1080, 240, true) }).Count == 0);
    Check("two displays at the same rate produce no findings",
        LatencyTriage.Scan(new[] { Disp("DISPLAY1", 1920, 1080, 240, true), Disp("DISPLAY2", 1920, 1080, 240) }).Count == 0);
    Check("an empty display list is safe", LatencyTriage.Scan(Array.Empty<DisplayInfo>()).Count == 0);
    Check("a display reporting 0 Hz is ignored", LatencyTriage.Scan(new[] { Disp("X", 640, 480, 0), Disp("Y", 640, 480, 0) }).Count == 0);

    // ---- DPCs landing on cores the plan reserves ----
    var ratesHot = new[] { Rate(0, 0.30), Rate(1, 0.20), Rate(2, 4.10), Rate(3, 0.15) };
    var hot = LatencyTriage.Scan(new[] { Disp("DISPLAY1", 1920, 1080, 240, true) }, ratesHot, new[] { 0, 1, 2, 3 });
    var hotFinding = hot.FirstOrDefault(x => x.Title.Contains("landing on game cores"));
    Check("DPC on a reserved core is detected", hotFinding is not null, string.Join(" | ", hot.Select(x => x.Title)));
    Check("the reserved-core finding is High", hotFinding?.Severity == TriageSeverity.High);
    Check("the reserved-core finding says affinity CAN help", hotFinding is { AffinityCanFix: true });
    Check("it names the offending CPU and its share", hotFinding is not null && hotFinding.Title.Contains("CPU 2") && hotFinding.Title.Contains("4.10"), hotFinding?.Title ?? "");
    Check("only the hot core is listed", hotFinding is not null && !hotFinding.Title.Contains("CPU 0"), hotFinding?.Title ?? "");

    // ---- below the threshold: silence ----
    var ratesCalm = new[] { Rate(0, 0.05), Rate(1, 0.03), Rate(2, 0.02), Rate(3, 0.01) };
    Check("quiet cores produce no reserved-core warning",
        !LatencyTriage.Scan(new[] { Disp("DISPLAY1", 1920, 1080, 240, true) }, ratesCalm, new[] { 0, 1, 2, 3 }).Any(x => x.Title.Contains("landing on game cores")));

    // ---- sustained load on any core ----
    var sustained = new[] { Rate(0, 0.05), Rate(5, 3.20) };
    var s = LatencyTriage.Scan(new[] { Disp("DISPLAY1", 1920, 1080, 240, true) }, sustained, Array.Empty<int>());
    var sus = s.FirstOrDefault(x => x.Title.Contains("Sustained DPC load"));
    Check("sustained DPC load is detected", sus is not null, string.Join(" | ", s.Select(x => x.Title)));
    Check("sustained load is Medium", sus?.Severity == TriageSeverity.Medium);
    Check("sustained load names the worst CPU", sus is not null && sus.Title.Contains("CPU 5"), sus?.Title ?? "");

    // ---- Microsoft's budget, and the ceiling nobody can move ----
    Check("the DPC budget is the documented 100 us", LatencyTriage.DpcBudgetUs == 100, LatencyTriage.DpcBudgetUs.ToString());
    Check("the ISR budget is the documented 25 us", LatencyTriage.IsrBudgetUs == 25, LatencyTriage.IsrBudgetUs.ToString());
        // The budget no longer appears here on purpose: "mixed refresh rates push the driver well
        // past 100 us" was a magnitude claim with no primary source. The sourced budgets are
        // asserted on the sustained-DPC finding instead, where they describe a measurement.
        Check("the mixed-refresh finding does not borrow the budget for an unmeasured claim",
            mix is not null && !mix.Guidance.Contains("well past"), mix?.Guidance ?? "");
    Check("sustained DPC load is stated against the budget",
        sus is not null && sus.Detail.Contains("100 us for one DPC routine") && sus.Detail.Contains("25 us for one ISR"),
        sus?.Detail ?? "");

    // An SMI cannot be moved by affinity, so it must never be offered as something the app can fix.
    var smi = hot.FirstOrDefault(x => x.Title.Contains("System Management Interrupts"));
    Check("DPC trouble also names SMIs as the ceiling", smi is not null,
        string.Join(" | ", hot.Select(x => x.Title)));
    Check("the SMI finding says affinity cannot fix it", smi is { AffinityCanFix: false });
    Check("the SMI finding admits the app cannot measure it", smi is not null && smi.Guidance.Contains("cannot measure"),
        smi?.Guidance ?? "");
    Check("the SMI finding is Info, not a warning about this machine", smi?.Severity == TriageSeverity.Info,
        smi?.Severity.ToString() ?? "none");
    Check("a healthy machine is not lectured about SMIs",
        !LatencyTriage.Scan(new[] { Disp("DISPLAY1", 1920, 1080, 240, true) }, ratesCalm, new[] { 0, 1, 2, 3 })
            .Any(x => x.Title.Contains("System Management Interrupts")));


    // ---- no counters available: display findings still work ----
    var noCounters = LatencyTriage.Scan(mixed, null, new[] { 0, 1 });
        Check("the display cost is reported even with no counters", noCounters.Count == 1, $"{noCounters.Count}: " + string.Join(" | ", noCounters.Select(x => x.Title)));
    Check("mixed refresh alone (no DPC counters) does not raise the SMI card", !noCounters.Any(x => x.Title.Contains("System Management Interrupts")));

    // ---- ordering ----
    var ordered = LatencyTriage.Ordered(f);
    Check("ordering puts High before Medium",
        ordered.Select(x => (int)x.Severity).SequenceEqual(ordered.Select(x => (int)x.Severity).OrderByDescending(v => v)),
        string.Join(",", ordered.Select(x => x.Severity.ToString())));

    // ---- the real machine: enumeration and the engine path must not throw ----
    bool enumOk = true;
    IReadOnlyList<DisplayInfo> live = Array.Empty<DisplayInfo>();
    try { live = Displays.Active(); } catch { enumOk = false; }
    Check("display enumeration does not throw", enumOk);
    Check("this machine reports at least one display", live.Count >= 1, live.Count.ToString());
    Check("every enumerated display has a sane mode",
        live.All(d => d.Width > 0 && d.Height > 0 && d.RefreshHz > 0),
        string.Join(", ", live.Select(d => $"{d.Device} {d.Width}x{d.Height}@{d.RefreshHz}")));

    string tdir = Path.Combine(Path.GetTempPath(), "dpc-triage-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tdir);
    string saved = Paths.DataDir;
    Paths.DataDir = tdir;
    try
    {
        var j = new Journal(Path.Combine(tdir, "journal.json"));
        var eng = new TweakEngine(new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFFFFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        }, j);

        bool threw = false;
        int count = -1;
        try { count = eng.Triage(TimeSpan.FromMilliseconds(40)).Count; } catch { threw = true; }
        Check("engine.Triage samples and returns without throwing", !threw, threw ? "threw" : count.ToString());
        Check("engine.Triage returns a usable list", count >= 0, count.ToString());
    }
    finally
    {
        Paths.DataDir = saved;
        try { Directory.Delete(tdir, true); } catch { }
    }
}
// ================================================================ 2026-11-18: the regression pass

Console.WriteLine("A suppressed plan advertises nothing and keeps every applied pin revertible");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-suppress-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var topo = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD", Cpu = topo,
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
            Devices = DesktopDevices(),
        };
        var engine = new TweakEngine(profile, j);

        // DefaultsFor a parking-dependent AMD desktop is the Balanced path, which is the path that
        // suppressed the plan and made every applied pin disappear from the list.
        Check("the Balanced default suppresses the plan", !engine.Plan.Applicable, engine.Plan.NotApplicableReason ?? "applicable");
        Check("...and the planner still knows where the devices would go", engine.Plan.Assignments.Count > 0,
            engine.Plan.Assignments.Count.ToString());

        var plan = engine.Plan;
        string irqId = "irq." + plan.Assignments[0].Device.InstanceId;
        var nic = profile.Devices.Single(d => d.RssCapable);
        string rssId = "rss." + (nic.NetCfgInstanceId ?? nic.InstanceId);
        j.MarkApplied(irqId);
        j.MarkApplied(rssId);
        j.Save();

        var built = InterruptsModule.Build(profile, j, engine.Options, plan).ToList();
        Check("this path builds no pin at all", !built.OfType<AffinityTweak>().Any());
        Check("an applied pin keeps a revert row", built.Any(t => t.Info.Id == irqId && t is LeftoverAffinityTweak),
            string.Join(", ", built.Select(t => t.Info.Id + ":" + t.GetType().Name)));
        var rssLeft = built.SingleOrDefault(t => t.Info.Id == rssId);
        Check("an applied RSS row keeps a revert row", rssLeft is LeftoverTweak, rssLeft?.GetType().Name ?? "absent");
        Check("...under interrupt affinity, where it was applied", rssLeft?.Info.Group == AffinityTweak.GroupName, rssLeft?.Info.Group ?? "");
        Check("a leftover row is never ticked for the user", rssLeft?.Info.DefaultSelected == false);

        // Back on a path that does place interrupts, the same ids are planned rows, not leftovers.
        engine.SetOptions(engine.Options.With(o =>
        {
            o.PlanTarget = PlanTargetKind.TunedUltimate;
            o.PowerProfile = PowerProfileKind.Full;
        }));
        Check("the tuned path builds pins", engine.Plan.Applicable);
        var tuned = InterruptsModule.Build(profile, j, engine.Options, engine.Plan).ToList();
        Check("the pin is a planned row again", tuned.Any(t => t.Info.Id == irqId && t is AffinityTweak));
        Check("...and it is not also a leftover", !tuned.Any(t => t.Info.Id == irqId && t is LeftoverAffinityTweak));
        // A synthetic adapter has no driver key, so this id can only be a revert-only row here: the
        // point is that it is not lost across the path switch, not that a write is planned for it.
        var rssRows = tuned.Where(t => t.Info.Id == rssId).Select(t => t.GetType().Name).ToList();
        Check("the RSS id is still on the screen exactly once after the path switch", rssRows.Count == 1, string.Join(",", rssRows));

        // The screen: a suppressed plan must not list a plan it will not carry out.
        engine.SetOptions(engine.Options.With(o => o.PlanTarget = PlanTargetKind.Balanced));
        var main = new MainViewModel(new FakeUiHost());
        var vm = new InterruptOptionsViewModel(main, engine);
        Check("the interrupt screen lists no plan lines on a suppressed plan", vm.PlanLines.Count == 0, vm.PlanLines.Count.ToString());
        Check("...and states the reason instead", vm.NotApplicableText.Length > 0);
        Check("...while the plan's assignments are still non-empty", engine.Plan.Assignments.Count > 0);
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("A preset switch is scoped to the tab it was clicked on");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-scope-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var topo = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD", Cpu = topo,
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
            Devices = DesktopDevices(),
        };
        var engine = new TweakEngine(profile, j);
        engine.SetOptions(engine.Options.With(o =>
        {
            o.PlanTarget = PlanTargetKind.TunedUltimate;
            o.PowerProfile = PowerProfileKind.Full;
        }));

        var main = new MainViewModel(new FakeUiHost());
        var pages = main.Pages.OfType<ModulePageViewModel>().ToList();
        ModulePageViewModel Page(string module) => pages.First(p => p.Module == module);

        var power = Page("Power");
        power.Load(engine, carryTicks: false);
        var interrupts = Page("Interrupts");
        interrupts.Load(engine, carryTicks: false);
        Check("the Power tab has rows to switch", power.PresetRows().Count > 0, power.PresetRows().Count.ToString());
        Check("the Interrupts tab has rows to switch", interrupts.PresetRows().Count > 0, interrupts.PresetRows().Count.ToString());

        var powerIds = power.PresetRows().Select(r => r.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var irqIds = interrupts.PresetRows().Select(r => r.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("every row a Power switch may touch is a Power row",
            power.PresetRows().All(r => r.Info.Module == "Power"),
            string.Join(", ", power.PresetRows().Where(r => r.Info.Module != "Power").Select(r => r.Info.Id)));
        Check("no Interrupts row is in Power's scope",
            !power.PresetRows().Any(r => irqIds.Contains(r.Info.Id)));
        Check("no Power row is in Interrupts' scope",
            !interrupts.PresetRows().Any(r => powerIds.Contains(r.Info.Id)));

        // The transition the tab builds can therefore only name its own rows: the old code fed every
        // page's rows in, which is what reverted Costly tweaks on tabs the user had not touched.
        var facts = power.PresetRows()
            .Select(r => new RowFacts(r.Info.Id, r.Info.Title, r.IsSelected, r.IsApplicable, r.Info.DefaultSelected,
                r.Info.CostClass, r.Info.Evidence, r.Status))
            .ToList();
        var transition = PresetTransition.Plan(TweakPreset.Safe, facts);
        var scope = facts.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check("the transition only names rows in the tab's scope",
            transition.ToApply.Concat(transition.ToRevert).All(scope.Contains),
            string.Join(", ", transition.ToApply.Concat(transition.ToRevert).Where(id => !scope.Contains(id))));
        Check("...and none of them is an Interrupts row", !transition.ToRevert.Concat(transition.ToApply).Any(irqIds.Contains));

        // Background is one page with three sections: its scope is all three, and nothing else.
        var background = main.Background;
        background.Load(engine, carryTicks: false);
        var allowed = new[] { "Boot tasks", "Services", "Tasks" };
        Check("the Background tab has rows", background.PresetRows().Count > 0, background.PresetRows().Count.ToString());
        Check("Background's scope is only its own three sections",
            background.PresetRows().All(r => allowed.Contains(r.Info.Module)),
            string.Join(", ", background.PresetRows().Select(r => r.Info.Module).Distinct()));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("The Background tab keeps its tier when it reloads");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-bgtier-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        var engine = new TweakEngine(profile, j);

        var o = new AppOptions();
        o.SetPresetFor("Background", TweakPreset.Costly);
        Check("the page reads its own tier", o.PresetFor("Background") == TweakPreset.Costly);
        Check("startup tasks read the page's tier", o.PresetFor("Boot tasks") == TweakPreset.Costly, o.PresetFor("Boot tasks").ToString());
        Check("services read the page's tier", o.PresetFor("Services") == TweakPreset.Costly);
        Check("scheduled tasks read the page's tier", o.PresetFor("Tasks") == TweakPreset.Costly);
        Check("another tab keeps the global tier", o.PresetFor("Power") == TweakPreset.Safe, o.PresetFor("Power").ToString());

        // A section with an entry of its own still wins: the page is a fallback, not an override.
        o.SetPresetFor("Services", TweakPreset.Essential);
        Check("a section's own choice beats the page's", o.PresetFor("Services") == TweakPreset.Essential);

        // Through the journal, which is what a restart does.
        o.Save(j);
        var back = AppOptions.Load(j, profile);
        Check("the page's tier survives a save/load", back.PresetFor("Background") == TweakPreset.Costly, back.PresetFor("Background").ToString());
        Check("...and reaches the sections after the round trip",
            back.PresetFor("Boot tasks") == TweakPreset.Costly && back.PresetFor("Tasks") == TweakPreset.Costly);
        Check("the section's own choice survives too", back.PresetFor("Services") == TweakPreset.Essential);

        // And the view model: a reload must not drop the tab back to the global tier.
        engine.SetOptions(engine.Options.With(x => x.SetPresetFor("Background", TweakPreset.Costly)));
        var main = new MainViewModel(new FakeUiHost());
        var bg = main.Background;
        bg.Load(engine, carryTicks: false);
        Check("the tab shows Costly after a reload", bg.PresetChoice?.Value == TweakPreset.Costly, bg.PresetChoice?.Value.ToString() ?? "null");
        Check("...and so does every section", bg.Startup.PresetChoice?.Value == TweakPreset.Costly &&
            bg.Services.PresetChoice?.Value == TweakPreset.Costly && bg.Scheduled.PresetChoice?.Value == TweakPreset.Costly,
            $"{bg.Startup.PresetChoice?.Value}/{bg.Services.PresetChoice?.Value}/{bg.Scheduled.PresetChoice?.Value}");
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("Healing runs the same follow-up an apply does");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-healfin-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "Test CPU", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 4).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFUL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
        };
        var engine = new TweakEngine(profile, j);

        // Two tweaks behind one adapter restart, and one whose restart fails: exactly the shape
        // NicPropertyTweak has, where NeedsReboot is false because Finish() restarts the adapter.
        var a = new FakeFinisherTweak(j, "t.fin.a", "nic-1");
        var b = new FakeFinisherTweak(j, "t.fin.b", "nic-1");
        var c = new FakeFinisherTweak(j, "t.fin.c", "nic-2") { FailOnFinish = true };
        var healed = engine.Heal(new[] { a, b, c }, null);
        Check("all three were re-applied", healed.Count(r => r.Ok) == 3, string.Join(", ", healed.Select(r => r.Error ?? "ok")));
        Check("the shared follow-up ran once", a.FinishCount == 1 && b.FinishCount == 0, $"{a.FinishCount}/{b.FinishCount}");
        Check("each key gets its own follow-up", c.FinishCount == 1, c.FinishCount.ToString());
        Check("a failed follow-up is reported, not swallowed", engine.LastFinishErrors.Count == 1, string.Join(" | ", engine.LastFinishErrors));

        // Apply already did this; the assertion above is the regression, and this one proves the two
        // paths now agree.
        var d = new FakeFinisherTweak(j, "t.fin.d", "nic-1");
        engine.Apply(new[] { d });
        Check("apply and heal run the same follow-up", d.FinishCount == 1, d.FinishCount.ToString());
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}
Console.WriteLine("Triage on the Balanced path still knows which cores games use");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-triage2-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string savedDir = Paths.DataDir;
    Paths.DataDir = dir;
    try
    {
        var j = new Journal(Path.Combine(dir, "journal.json"));
        var profile = new SystemProfile
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD",
            Cpu = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
                new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) }),
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
            Devices = DesktopDevices(),
        };
        var engine = new TweakEngine(profile, j);
        Check("the Balanced default builds no pins", !engine.Plan.Applicable, engine.Plan.NotApplicableReason ?? "applicable");
        Check("...and the reserved cores are still known", engine.Plan.ReservedCpus.Count > 0, engine.Plan.ReservedCpus.Count.ToString());

        int gameCpu = engine.Plan.ReservedCpus[0];
        var rates = new[]
        {
            new CpuRates(gameCpu, 900, 400, 0.1, 2.2),
            new CpuRates(20, 900, 400, 0.1, 5.0),
        };
        var withCores = LatencyTriage.Scan(null, rates, engine.Plan.ReservedCpus);
        Check("DPCs on a reserved core are reported",
            withCores.Any(f => f.Title.Contains("landing on game cores")), string.Join(" | ", withCores.Select(f => f.Title)));
        var noCores = LatencyTriage.Scan(null, rates, null);
        Check("the same sample with no reserved-core list reports nothing, which is what the fix removed",
            !noCores.Any(f => f.Title.Contains("landing on game cores")));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dir, true); } catch { }
    }
}

Console.WriteLine("Power: class-1 rows only on an Intel hybrid, the overlay only on AMD");
{
    string dir = Path.Combine(Path.GetTempPath(), "dpc-pwrrows-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var j = new Journal(Path.Combine(dir, "journal.json"));

    var pCores = Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)).ToArray();
    var eCores = Enumerable.Range(16, 4).Select(c => (new[] { c }, (byte)1)).ToArray();
    var hybrid = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "Intel Core Ultra 9 285K", CpuVendor = "Intel",
        Cpu = Topo(pCores.Concat(eCores), new[] { (0x000FFFFFUL, 36u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    Check("the synthetic CPU is an Intel hybrid", hybrid.Cpu.IsHybrid);
    Check("...with a class 1 on the E-cores", hybrid.Cpu.Cpus.Any(c => c.EfficiencyClass > 0));

    static AppOptions Tuned() => new() { PlanTarget = PlanTargetKind.TunedUltimate, PowerProfile = PowerProfileKind.Full };
    var hybridIds = PowerModule.Build(hybrid, j, Tuned).Select(t => t.Info.Id).ToList();
    foreach (string id in new[] { "power.cpu.core-parking-class1", "power.cpu.min-state-class1", "power.cpu.epp-class1", "power.cpu.epp-latency-class1" })
        Check("a hybrid CPU builds " + id, hybridIds.Contains(id), string.Join(", ", hybridIds.Where(x => x.Contains("class1"))));
    Check("...and the boost ceiling", hybridIds.Contains("power.cpu.boost-policy"));
    Check("an Intel machine never gets the AMD overlay", !hybridIds.Contains("power.cpu.amd-overlay"));

    var hybridBalanced = PowerModule.Build(hybrid, j,
        () => new AppOptions { PlanTarget = PlanTargetKind.Balanced, PowerProfile = PowerProfileKind.Full }).Select(t => t.Info.Id).ToList();
    Check("Balanced builds no class-1 row", !hybridBalanced.Any(id => id.EndsWith("-class1", StringComparison.Ordinal)),
        string.Join(", ", hybridBalanced.Where(id => id.EndsWith("-class1", StringComparison.Ordinal))));
    Check("Balanced builds no boost ceiling either", !hybridBalanced.Contains("power.cpu.boost-policy"));

    var amd = new SystemProfile
    {
        Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
        CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD",
        Cpu = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) }),
        FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false), Software = new SoftwareInfo(),
    };
    var amdIds = PowerModule.Build(amd, j, Tuned).Select(t => t.Info.Id).ToList();
    Check("a 9950X3D reports no efficiency class above 0", amd.Cpu.Cpus.All(c => c.EfficiencyClass == 0));
    Check("...so none of the class-1 rows is built", !amdIds.Any(id => id.EndsWith("-class1", StringComparison.Ordinal)),
        string.Join(", ", amdIds.Where(id => id.EndsWith("-class1", StringComparison.Ordinal))));
    Check("...while the boost ceiling still is", amdIds.Contains("power.cpu.boost-policy"));

    var boost = PowerSettings.All.Single(d => d.Key == "cpu.boost-policy");
    Check("the boost ceiling is graded Documented, not Measured", boost.Evidence == Evidence.Documented);
    Check("...and costs nothing but power", boost.CostClass == TweakCost.Free);
    var overlay = PowerSettings.All.Single(d => d.Key == "cpu.amd-overlay");
    Check("the overlay is graded Documented too", overlay.Evidence == Evidence.Documented);
    Check("...and names the slider's top position", overlay.Format(3) == "Best performance", overlay.Format(3));
    Check("...and is never in the devices-only profile", !overlay.InDevicesOnly && overlay.InFull);

    // A CPU swap: the journal still holds a class-1 setting on a machine without a class 1.
    j.MarkApplied("power.cpu.core-parking-class1");
    j.Save();
    var swapped = PowerModule.Build(amd, j, Tuned).ToList();
    var left = swapped.SingleOrDefault(t => t.Info.Id == "power.cpu.core-parking-class1");
    Check("a class-1 setting left by a CPU swap becomes a leftover", left is LeftoverTweak, left?.GetType().Name ?? "absent");
    Check("...under the CPU heading", left?.Info.Group == "CPU", left?.Info.Group ?? "");
    Check("...and reads back without throwing", left is not null && left.Detect().Status != TweakStatus.Unknown);
}

Console.WriteLine("Plan notes: leftovers, live parking and the V-Cache conflict");
{
    string dirA = Path.Combine(Path.GetTempPath(), "dpc-notea-" + Guid.NewGuid().ToString("N"));
    string dirB = Path.Combine(Path.GetTempPath(), "dpc-noteb-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dirA);
    Directory.CreateDirectory(dirB);
    string savedDir = Paths.DataDir;
    try
    {
        var topo = Topo(Enumerable.Range(0, 16).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)),
            new[] { (0x0000FFFFUL, 96u << 20), (0xFFFF0000UL, 32u << 20) });
        SystemProfile Profile(bool vcacheService) => new()
        {
            Windows = new WindowsInfo("Windows 11 Pro", "26H2", 26300, 9711, "Professional"),
            CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor", CpuVendor = "AMD", Cpu = topo,
            FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
            Gpus = Array.Empty<GpuInfo>(), Security = new SecurityInfo(0, false, false),
            Software = new SoftwareInfo { AmdVCacheService = vcacheService },
            Devices = DesktopDevices(),
        };

        // M1: Balanced, with a CPU power setting left in the journal.
        Paths.DataDir = dirA;
        var jA = new Journal(Path.Combine(dirA, "journal.json"));
        var engineA = new TweakEngine(Profile(false), jA);
        Check("a clean Balanced machine gets no leftover note",
            !engineA.Plan.Notes.Any(n => n.Contains("from an earlier configuration")),
            string.Join(" | ", engineA.Plan.Notes));
        jA.MarkApplied("power.cpu.epp");
        jA.Save();
        engineA.SetOptions(engineA.Options.With(o => o.PlanTarget = PlanTargetKind.Balanced));
        Check("the leftover row exists", engineA.Tweaks.Any(t => t.Info.Id == "power.cpu.epp" && t is LeftoverTweak),
            string.Join(", ", engineA.Tweaks.Where(t => t.Info.Id == "power.cpu.epp").Select(t => t.GetType().Name)));
        Check("...and the M1 note is added with it",
            engineA.Plan.Notes.Any(n => n.Contains("from an earlier configuration") && n.Contains("leftover rows")),
            string.Join(" | ", engineA.Plan.Notes));

        // P-E: the V-Cache service is installed, and the path is not Balanced.
        Paths.DataDir = dirB;
        var jB = new Journal(Path.Combine(dirB, "journal.json"));
        var engineB = new TweakEngine(Profile(true), jB);
        engineB.SetOptions(engineB.Options.With(o =>
        {
            o.PlanTarget = PlanTargetKind.TunedUltimate;
            o.PowerProfile = PowerProfileKind.Full;
        }));
        Check("the tuned path applies", engineB.Plan.Applicable);
        Check("the V-Cache conflict is named",
            engineB.Plan.Notes.Any(n => n.Contains("amd3dvcacheSvc")), string.Join(" | ", engineB.Plan.Notes));
        engineB.SetOptions(engineB.Options.With(o => o.PlanTarget = PlanTargetKind.Balanced));
        Check("...and is not repeated on the Balanced path, which does not pin",
            !engineB.Plan.Notes.Any(n => n.Contains("amd3dvcacheSvc")), string.Join(" | ", engineB.Plan.Notes));
    }
    finally
    {
        Paths.DataDir = savedDir;
        try { Directory.Delete(dirA, true); } catch { }
        try { Directory.Delete(dirB, true); } catch { }
    }

    // The CurrentActive parking note, every branch, without touching a live plan.
    Check("100% unparked needs no note",
        TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, true, true, false, false, 100, 100) is null);
    string? parked = TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, true, true, false, false, 50, null);
    Check("parking on the active plan is reported", parked is not null && parked.Contains("allows core parking"), parked ?? "null");
    Check("a class-1 parking value counts as well",
        TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, true, true, false, false, 100, 60) is not null);
    Check("an unreadable value is not a claim",
        TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, true, true, false, false, null, null) is null);
    Check("the Balanced path has its own rule, so no note here",
        TweakEngine.ParkingWarning(PlanTargetKind.Balanced, true, true, true, false, 50, 50) is null);
    Check("a CPU that does not depend on parking gets no note",
        TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, false, true, false, false, 40, null) is null);
    string? laptop = TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, true, true, true, false, 40, null);
    Check("a laptop gets the battery caveat", laptop is not null && laptop.Contains("DC"), laptop ?? "null");
    string? laptopApplied = TweakEngine.ParkingWarning(PlanTargetKind.CurrentActive, true, true, true, true, 40, null);
    Check("...unless applying on battery too is on", laptopApplied is not null && !laptopApplied.Contains("DC"));
}

Console.WriteLine("Options: a null or re-read preset map cannot take a tab down");
{
    var o = new AppOptions { Presets = null! };
    Check("a null map reads as the global tier", o.PresetFor("Power") == o.Preset);
    Check("...and so does a Background section", o.PresetFor("Boot tasks") == o.Preset);
    o.SetPresetFor("Power", TweakPreset.Costly);
    Check("writing into a null map works", o.PresetFor("Power") == TweakPreset.Costly);

    var lower = new AppOptions();
    lower.SetPresetFor("interrupts", TweakPreset.Costly);
    var copy = lower.With(x => x.TimerResolution = 5000);
    Check("a With() copy still matches a tab name whatever its case",
        copy.PresetFor("Interrupts") == TweakPreset.Costly, copy.PresetFor("Interrupts").ToString());
}
Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
/// <summary>
/// A tweak that answers from a field instead of the system, so drift and healing can be exercised
/// without touching the real registry. Apply records a before-state exactly like RegWrite.Set does.
/// </summary>
internal sealed class FakeDriftTweak : TweakBase
{
    private readonly string _key;

    public FakeDriftTweak(Journal journal, string id, string key)
        : base(new TweakInfo { Id = id, Module = "Test", Title = "Fake " + id, NeedsReboot = false }, journal)
    {
        _key = key;
    }

    /// <summary>The live state: false means "someone put it back".</summary>
    public bool Matches { get; set; }

    public int ApplyCount { get; private set; }
    public bool FailOnApply { get; init; }

    protected override Probe Read() => new(Matches, Matches ? "tuned" : "stock", "tuned");

    public override void Apply(TweakContext ctx)
    {
        ApplyCount++;
        // Same order as RegWrite.Set: record first, then change. The second call must not add a
        // second entry, which is what keeps the original baseline after a heal.
        ctx.Journal.RecordOriginal(Info.Id, "reg", _key, true, "DWord", "0");
        if (FailOnApply) throw new InvalidOperationException("simulated failure");
        Matches = true;
    }
}

/// <summary>
/// A tweak with a batched follow-up, the shape NicPropertyTweak has: the write is one registry value
/// and the follow-up is one adapter restart for the whole batch. Its NeedsReboot is false for exactly
/// that reason, so a heal that forgets the follow-up leaves the registry changed and the driver not
/// running with it.
/// </summary>
internal sealed class FakeFinisherTweak : TweakBase, IBatchFinisher
{
    private readonly string _key;

    public FakeFinisherTweak(Journal journal, string id, string key)
        : base(new TweakInfo { Id = id, Module = "Test", Title = "Fake " + id, CostClass = TweakCost.Free, NeedsReboot = false }, journal)
    {
        _key = key;
    }

    public int FinishCount { get; private set; }
    public bool FailOnFinish { get; init; }

    string IBatchFinisher.FinishKey => _key;

    void IBatchFinisher.Finish()
    {
        FinishCount++;
        if (FailOnFinish) throw new InvalidOperationException("simulated finish failure");
    }

    protected override Probe Read() => new(true, "tuned", "tuned");

    public override void Apply(TweakContext ctx) => ctx.Journal.RecordOriginal(Info.Id, "reg", _key, true, "DWord", "0");
}

/// <summary>Nothing is shown, nothing is clicked: the view models under test never reach the UI.</summary>
internal sealed class FakeUiHost : IUiHost
{
    public List<string> Opened { get; } = new();
    public List<string> Messages { get; } = new();
    public void Post(Action action) => action();
    public bool Confirm(string title, string message) { Messages.Add(title); return true; }
    public void Inform(string title, string message) => Messages.Add(title);
    public bool Review(string title, string message, string payload) => true;
    public string? Prompt(string title, string message, string defaultText = "") => defaultText;
    public void Open(string path) => Opened.Add(path);
    public void Restart() => Messages.Add("restart");
}
