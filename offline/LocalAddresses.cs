// Makes the game's fixed server addresses local while the offline server runs.
//
// The game always connects to the same addresses: the 7BEAT login at 210.128.4.130:58001, and the start-up
// request to https://pcgame.7rhythm.jp (pointed at 52.69.127.28 by the hosts file). The server adds these addresses
// to Windows' loopback adapter (active store only - gone after a reboot, and removed again when the server closes)
// and listens on them directly, so the game's connections are ordinary local connections. No packet driver or
// routes are needed, and it works the same with or without a network connection.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class LocalAddresses
{
    public const string AuthIp = "210.128.4.130";                            // 7BEAT login server (fixed in the game)
    public static readonly string[] ApiIps = { "52.69.127.28", "52.196.206.7" };  // original pcgame.7rhythm.jp addresses

    delegate bool ConsoleCtrlHandler(int ctrlType);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);
    static ConsoleCtrlHandler ctrlHandler;   // kept in a field so it is not garbage collected

    static string[] All()
    {
        List<string> l = new List<string>(); l.Add(AuthIp); l.AddRange(ApiIps); return l.ToArray();
    }

    static string RunPowerShell(string script)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return o.Trim().Replace("\r", "").Replace("\n", "; ");
            }
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    static string RemoveScript()
    {
        // also clears host routes an older version of the offline server may have left behind
        return "foreach ($ip in @('" + string.Join("','", All()) + "')) { " +
               "Remove-NetIPAddress -IPAddress $ip -Confirm:$false -ErrorAction SilentlyContinue; " +
               "Remove-NetRoute -DestinationPrefix ($ip + '/32') -Confirm:$false -ErrorAction SilentlyContinue }";
    }

    // Adds the addresses to the loopback adapter. Returns true if all of them are in place.
    public static bool Add()
    {
        string script = RemoveScript() + "; " +
            "$lo = (Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -like 'Loopback*' } | Select-Object -First 1).ifIndex; " +
            "if (-not $lo) { $lo = 1 }; " +
            "foreach ($ip in @('" + string.Join("','", All()) + "')) { " +
            "  try { New-NetIPAddress -InterfaceIndex $lo -IPAddress $ip -PrefixLength 32 -SkipAsSource $true -PolicyStore ActiveStore -ErrorAction Stop | Out-Null; 'ok ' + $ip } " +
            "  catch { 'failed ' + $ip + ': ' + $_.Exception.Message } }";
        string result = RunPowerShell(script);
        OfflineServer.Log("ADDR  local addresses on the loopback adapter: " + result);
        ctrlHandler = delegate (int type) { Remove(); return false; };
        SetConsoleCtrlHandler(ctrlHandler, true);
        AppDomain.CurrentDomain.ProcessExit += delegate { Remove(); };
        Thread.Sleep(1000);   // give Windows a moment before binding to the new addresses
        return !result.Contains("failed") && !result.StartsWith("error");
    }

    static int removed;
    public static void Remove()
    {
        if (Interlocked.Exchange(ref removed, 1) == 1) return;
        RunPowerShell(RemoveScript());
        try { OfflineServer.Log("ADDR  local addresses removed"); } catch { }
    }

    // pcgame.7rhythm.jp must point at one of the local API addresses (launch_offline.bat sets the hosts entry).
    public static void CheckHostsEntry()
    {
        try
        {
            List<string> found = new List<string>();
            foreach (System.Net.IPAddress a in System.Net.Dns.GetHostAddresses("pcgame.7rhythm.jp")) found.Add(a.ToString());
            bool ok = false; foreach (string s in found) if (Array.IndexOf(ApiIps, s) >= 0) ok = true;
            OfflineServer.Log("DNS   pcgame.7rhythm.jp resolves to " + (found.Count > 0 ? string.Join(", ", found.ToArray()) : "nothing") +
                (ok ? "" : " - expected " + ApiIps[0] + "; run launch_offline.bat so it can fix the hosts file"));
        }
        catch (Exception ex) { OfflineServer.Log("DNS   pcgame.7rhythm.jp could not be resolved (" + ex.Message + ") - run launch_offline.bat so it can fix the hosts file"); }
    }
}
