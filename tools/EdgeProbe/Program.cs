using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Tweaks;
using Microsoft.Win32;

// Edge-case probe: does a registry value survive Serialize -> Deserialize -> Write -> Read?
// Uses HKCU only, deletes everything it creates.
internal static class Program
{
    private const string Root = @"HKCU\Software\StutterGuardEdgeProbe";

    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ok    {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
    }

    private static void Main()
    {
        try { RegistryOps.DeleteValue(Root, "__none__"); } catch { }
        try
        {
            Run();
        }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\StutterGuardEdgeProbe", false); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static void RoundTrip(string label, string name, RegistryValueKind kind, object value)
    {
        // Write the value, read it back the way the app does, serialize it, deserialize it, write it
        // again, and compare. This is exactly the journal -> undo-all.ps1 path.
        RegistryOps.Write(Root, name, kind, value);

        RegValue first = RegistryOps.Read(Root, name);
        if (!first.Exists) { Check(label, false, "value was not readable after write"); return; }

        string serialized;
        try { serialized = first.Serialize(); }
        catch (Exception ex) { Check(label, false, "Serialize threw: " + ex.GetType().Name + ": " + ex.Message); return; }

        object restored;
        try { restored = RegValue.Deserialize(first.Kind, serialized); }
        catch (Exception ex) { Check(label, false, "Deserialize threw: " + ex.GetType().Name + ": " + ex.Message); return; }

        RegistryOps.Write(Root, name, first.Kind, restored);
        RegValue second = RegistryOps.Read(Root, name);

        bool same = first.Kind == second.Kind && first.Display == second.Display;
        Check(label, same, $"kind {first.Kind}->{second.Kind}, {first.Display} -> {second.Display}");
    }

    private static void Run()
    {
        Console.WriteLine("Registry round-trip: the journal -> undo-all.ps1 path");

        RoundTrip("DWORD 0", "d0", RegistryValueKind.DWord, 0);
        RoundTrip("DWORD 1", "d1", RegistryValueKind.DWord, 1);
        RoundTrip("DWORD max (0xFFFFFFFF)", "dmax", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF));
        RoundTrip("QWORD max", "qmax", RegistryValueKind.QWord, unchecked((long)0xFFFFFFFFFFFFFFFF));
        RoundTrip("SZ normal", "sz1", RegistryValueKind.String, "hello");
        RoundTrip("SZ empty", "szempty", RegistryValueKind.String, "");
        RoundTrip("SZ with newline", "sznl", RegistryValueKind.String, "a\nb");
        RoundTrip("SZ with CRLF", "szcrlf", RegistryValueKind.String, "a\r\nb");
        RoundTrip("ExpandSZ with %VAR%", "esz", RegistryValueKind.ExpandString, "%SystemRoot%\\x");
        RoundTrip("Binary 8 bytes", "b8", RegistryValueKind.Binary, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        RoundTrip("Binary 0 bytes", "b0", RegistryValueKind.Binary, Array.Empty<byte>());
        RoundTrip("MultiString 2 items", "ms2", RegistryValueKind.MultiString, new[] { "a", "b" });
        RoundTrip("MultiString empty string item", "msempty", RegistryValueKind.MultiString, new[] { "" });
        RoundTrip("MultiString item containing newline", "msnl", RegistryValueKind.MultiString, new[] { "a\nb", "c" });
        RoundTrip("MultiString single empty", "msone", RegistryValueKind.MultiString, new[] { "" , "" });
        RoundTrip("REG_NONE bytes", "none1", RegistryValueKind.None, new byte[] { 0xDE, 0xAD });
        RoundTrip("REG_NONE zero-length", "none0", RegistryValueKind.None, Array.Empty<byte>());

        Console.WriteLine();
        Console.WriteLine("RegWrite mask edge cases");
        Check("MaskBytes(0) is 8 zero bytes", RegWrite.MaskBytes(0).Length == 8 && RegWrite.MaskBytes(0).All(b => b == 0));
        Check("FormatMask(0) says 'no CPU'", RegWrite.FormatMask(0) == "no CPU", RegWrite.FormatMask(0));
        Check("FormatMask(1) says 'CPU 0'", RegWrite.FormatMask(1) == "CPU 0", RegWrite.FormatMask(1));
        Check("FormatMask(0x55550000) lists 8 CPUs", RegWrite.FormatMask(0x55550000UL).Contains("16") && RegWrite.FormatMask(0x55550000UL).Contains("30"), RegWrite.FormatMask(0x55550000UL));

        Console.WriteLine();
        Console.WriteLine("Journal / reversibility edge cases");

        // E4: does writing to a missing key create it, and does deleting the value remove the key?
        const string freshKey = @"HKCU\Software\StutterGuardEdgeProbe\Created\Deep\Path";
        Check("key does not exist beforehand", !RegistryOps.KeyExists(freshKey));
        RegistryOps.Write(freshKey, "v", RegistryValueKind.DWord, 1);
        Check("Write() creates the missing key", RegistryOps.KeyExists(freshKey));
        RegistryOps.DeleteValue(freshKey, "v");
        Check("after reverting the value, the created key still exists (leftover)",
            RegistryOps.KeyExists(freshKey));

        // E3: a broken journal must never overwrite an earlier .broken copy, even within one second.
        string probeDir = Path.Combine(Path.GetTempPath(), "sg-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDir);
        string jf = Path.Combine(probeDir, "journal.json");
        try
        {
            File.WriteAllText(jf, "{ this is not json");
            _ = new Journal(jf);          // first failure -> .broken-<stamp>
            _ = new Journal(jf);          // second failure in the same second
            int broken = Directory.GetFiles(probeDir, "journal.json.broken-*").Length;
            Check("two broken journals both kept (no overwrite)", broken == 2, $"{broken} .broken file(s)");
        }
        finally { try { Directory.Delete(probeDir, true); } catch { } }        RegistryOps.Write(Root, "mask3", RegistryValueKind.Binary, new byte[] { 0x00, 0x00, 0x10 });
        var v3 = RegistryOps.Read(Root, "mask3");
        var m3 = RegWrite.AsMask(v3);
        Check("AsMask reads a 3-byte mask as CPU 20", m3 == 0x100000UL, $"0x{m3:X}");

        // AsMask over an over-long binary (what happens with > 8 bytes?)
        RegistryOps.Write(Root, "mask12", RegistryValueKind.Binary, new byte[] { 1,0,0,0,0,0,0,0, 0xFF,0xFF,0xFF,0xFF });
        var v12 = RegistryOps.Read(Root, "mask12");
        var m12 = RegWrite.AsMask(v12);
        Check("AsMask truncates a 12-byte mask to its low 8 bytes", m12 == 1UL, $"0x{m12:X}");
    }
}