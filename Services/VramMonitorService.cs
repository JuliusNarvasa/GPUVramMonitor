using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using GpuVramMonitor.Models;

namespace GpuVramMonitor.Services;

public static class VramMonitorService
{
    // Actual format: pid_1234_luid_0x00000000_0x0000aaf3_phys_0
    private static readonly Regex PidRegex = new(@"^pid_(\d+)", RegexOptions.Compiled);

    public static List<GpuProcessInfo> GetProcessVramUsage(bool excludeDwm = true, double totalVramMB = 0)
    {
        var pidMap = new Dictionary<int, (string name, double vram)>();

        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            var instances = category.GetInstanceNames();

            foreach (var instance in instances)
            {
                var match = PidRegex.Match(instance);
                if (!match.Success) continue;

                int pid = int.Parse(match.Groups[1].Value);
                float value = 0;

                try
                {
                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance);
                    value = counter.NextValue();
                    Thread.Sleep(5);
                    float value2 = counter.NextValue();
                    value = value2 > 0 ? value2 : value;
                }
                catch
                {
                    continue;
                }

                double vramMB = value / (1024.0 * 1024.0);
                if (vramMB <= 0) continue;

                string processName = "Unknown";
                try
                {
                    var proc = Process.GetProcessById(pid);
                    processName = proc.ProcessName;
                }
                catch
                {
                    // Process may have exited
                }

                if (excludeDwm && processName.Equals("dwm", StringComparison.OrdinalIgnoreCase))
                    continue;

                // If same PID appears on multiple GPUs, keep the higher value
                if (pidMap.TryGetValue(pid, out var existing))
                {
                    if (vramMB > existing.vram)
                        pidMap[pid] = (processName, Math.Round(vramMB, 1));
                }
                else
                {
                    pidMap[pid] = (processName, Math.Round(vramMB, 1));
                }
            }

            // Bar = % of total VRAM (or of max process if total is unknown)
            double maxForBar = totalVramMB > 0 ? totalVramMB : (pidMap.Count > 0 ? pidMap.Max(p => p.Value.vram) : 1);

            return pidMap
                .OrderByDescending(p => p.Value.vram)
                .Select(p => new GpuProcessInfo
                {
                    ProcessName = p.Value.name,
                    PID = p.Key,
                    VramMB = p.Value.vram,
                    BarWidthPercent = Math.Round(p.Value.vram / maxForBar * 100, 1)
                })
                .ToList();
        }
        catch
        {
            // GPU performance counters not available on this system
            return new List<GpuProcessInfo>();
        }
    }

    public static List<(string name, double usedMB)> GetAdapterVramUsage()
    {
        var adapters = new List<(string name, double usedMB)>();

        try
        {
            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            var instances = category.GetInstanceNames();

            foreach (var instance in instances)
            {
                try
                {
                    using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance);
                    float value = counter.NextValue();
                    Thread.Sleep(5);
                    float value2 = counter.NextValue();
                    value = value2 > 0 ? value2 : value;

                    double usedMB = value / (1024.0 * 1024.0);
                    if (usedMB > 0) // only report adapters with actual usage
                        adapters.Add((instance, Math.Round(usedMB, 1)));
                }
                catch
                {
                    // Skip adapters we can't read
                }
            }
        }
        catch
        {
            // GPU adapter counters not available
        }

        return adapters;
    }

    public static double GetTotalVramCapacity()
    {
        // 1. Try registry: qwMemorySize (QWORD, avoids 4GB truncation)
        try
        {
            const string basePath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            // 1a. Try most common subkey directly first (0000 = primary GPU)
            double? direct = TryReadVramFromKey($@"{basePath}\0000");
            if (direct.HasValue) return direct.Value;

            // 1b. Fall back to scanning all numeric subkeys
            using (var baseKey = Registry.LocalMachine.OpenSubKey(basePath))
            {
                if (baseKey != null)
                {
                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        if (!subKeyName.All(char.IsDigit)) continue;
                        double? val = TryReadVramFromKey($@"{basePath}\{subKeyName}");
                        if (val.HasValue) return val.Value;
                    }
                }
            }
        }
        catch { }

        // 2. Fallback: WMI
        try { return ReadVramFromWmi(); } catch { }

        // 3. Guess from GPU name
        return GuessVramFromGpuName();
    }

    private static double? TryReadVramFromKey(string fullKeyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(fullKeyPath);
            if (key == null) return null;

            var raw = key.GetValue("HardwareInformation.qwMemorySize");
            if (raw != null)
            {
                double bytes = raw switch
                {
                    ulong u   => (double)u,
                    long   l  => (double)l,
                    int    i  => (double)(long)(uint)i,
                    byte[] b  => b.Length >= 8 ? BitConverter.ToUInt64(b, 0) : 0,
                    _         => 0
                };
                if (bytes > 1024 * 1024) // > 1MB = valid
                    return Math.Round(bytes / (1024.0 * 1024.0), 0);
            }

            // DWORD fallback
            var rawDw = key.GetValue("HardwareInformation.MemorySize");
            if (rawDw is int dw && dw > 0)
                return Math.Round((double)(long)(uint)dw / (1024.0 * 1024.0), 0);
        }
        catch { }
        return null;
    }

    private static double ReadVramFromWmi()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, AdapterRAM FROM Win32_VideoController WHERE AdapterRAM IS NOT NULL AND AdapterRAM > 0");
        foreach (var obj in searcher.Get())
        {
            var ram = obj["AdapterRAM"];
            if (ram != null)
            {
                double totalBytes = Convert.ToDouble(ram);
                if (totalBytes > 1024 * 1024)
                    return Math.Round(totalBytes / (1024.0 * 1024.0), 0);
            }
        }
        return 0;
    }

    private static double GuessVramFromGpuName()
    {
        try
        {
            var name = GetGpuName();
            if (name.Contains("7800")) return 16384;
            if (name.Contains("7900")) return 20480;
            if (name.Contains("7700")) return 12288;
            if (name.Contains("7600")) return 8192;
            if (name.Contains("4070")) return 12288;
            if (name.Contains("4080")) return 16384;
            if (name.Contains("4090")) return 24576;
        }
        catch { }
        return 0;
    }

    public static string GetGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController WHERE AdapterRAM IS NOT NULL AND AdapterRAM > 0");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
        catch { }

        return "Unknown GPU";
    }
}
