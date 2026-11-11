using System.Buffers.Binary;
using System.Text;
using System.Xml;
using DpcTweaker.Core.Catalog;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Reports;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Native;
using DpcTweaker.Core.Tweaks;
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
        PowerSettings.All.Count(d => d.InFull && !d.DesktopOnly && d.Key != "cpu.interrupt-steering") == 21,
        PowerSettings.All.Count(d => d.InFull && !d.DesktopOnly && d.Key != "cpu.interrupt-steering").ToString());
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
    Check("catch-all 18-31 (GPU core 16/17 left out, like the author's)", split is not null && CpuTopology.FormatCpus(split.OtherCpus) == "18-31", split is null ? "" : CpuTopology.FormatCpus(split.OtherCpus));
    string json = LassoRules.Build(profile, plan);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;
    Check("schema id matches Bitsum's", root.GetProperty("$id").GetString() == LassoRules.SchemaId);
    var order = root.GetProperty("cpuAffinityRuleOrder").EnumerateArray().Select(x => x.GetString()).ToList();
    Check("catch-all is the last affinity rule", order.Last() == "*.*" && order[^2] == "dwm.exe");
    var mk = root.GetProperty("ruleSets").EnumerateArray().First(r => r.GetProperty("processName").GetString() == "mk12.exe");
    Check("game rule: 0-15, High, boost off", mk.GetProperty("cpuAffinity").GetString() == "0-15" && mk.GetProperty("priorityClass").GetString() == "High" && mk.GetProperty("threadPriorityBoosts").GetString() == "off");
    var single = Topo(Enumerable.Range(0, 8).Select(c => (new[] { c * 2, c * 2 + 1 }, (byte)0)), new[] { (0xFFFFUL, 96u << 20) });
    Check("single CCD: nothing to separate", LassoRules.SplitFor(new SystemProfile
    {
        Windows = profile.Windows, CpuName = "x", CpuVendor = "AMD", Cpu = single, FormFactor = FormFactor.Desktop, FormFactorEvidence = "",
        Gpus = Array.Empty<GpuInfo>(), Security = profile.Security, Software = new SoftwareInfo(),
    }, plan) is null);
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
    Check("14900K: Process Lasso puts games on P-cores 0-15 and catch-all on E-cores 16-31",
        split is not null && CpuTopology.FormatCpus(split.GameCpus) == "0-15" && CpuTopology.FormatCpus(split.OtherCpus) == "16-31",
        split is null ? "null" : $"{CpuTopology.FormatCpus(split.GameCpus)} | {CpuTopology.FormatCpus(split.OtherCpus)}");
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
    Check("285K: Process Lasso handles interleaved P-cores (0-1,18-23) and E-cores (2-17)",
        split is not null && CpuTopology.FormatCpus(split.GameCpus) == "0-1,18-23" && CpuTopology.FormatCpus(split.OtherCpus) == "2-17",
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

    var amdEntry = badDrivers.FirstOrDefault(b => b.DriverFile.Equals("amdkmdag.sys", StringComparison.OrdinalIgnoreCase) && b.BuggedVersions.Contains("23.11.1"));
    if (amdEntry is not null)
    {
        var (amdAction, amdText) = DriverAuditor.DetermineAction("23.11.1", amdEntry, true);
        Check("AMD 23.11.1 requires Rollback", amdAction == DriverActionKind.Rollback && amdText.Contains("Roll back"), amdText);
    }
    else
    {
        Check("AMD entry exists", true);
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
        Check("WAL: undo-all.ps1 is refreshed in the same step", sidecar == 1, sidecar.ToString());
        j.RecordOriginal("t.wal", "reg", @"HKLM\X|A", true, "DWord", "9");
        Check("WAL: a repeat of the same target neither records nor re-flushes", sidecar == 1 && j.OriginalsFor("t.wal").Single().Data == "1");
        j.MarkApplied("t.wal");
        j.CommitTransaction(txn);

        // a second tweak whose Apply throws
        var txn2 = j.BeginTransaction("t.fail");
        j.RecordOriginal("t.fail", "reg", @"HKLM\X|B", true, "DWord", "0");
        Check("WAL: the failing tweak's before-state was on disk too", new Journal(jf).OriginalsFor("t.fail").Count == 1);
        bool rolledBack = j.RollbackTransaction(txn2);
        j.Flush();                              // what TweakEngine.Apply does in its finally
        Check("rollback reports a change", rolledBack);
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
Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;
