using System.Diagnostics;

static class HealthSampler
{
    static PerformanceCounter? cpu;
    static PerformanceCounter? mem;
    static bool primed;

    public static object Read()
    {
        try
        {
            cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            mem ??= new PerformanceCounter("Memory", "% Committed Bytes In Use");
            if (!primed) { cpu.NextValue(); primed = true; Thread.Sleep(200); }
            var cpuPct = (int)Math.Clamp(cpu.NextValue(), 0, 100);
            var memPct = (int)Math.Clamp(mem.NextValue(), 0, 100);
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C"));
            var diskPct = 50;
            if (drive != null && drive.TotalSize > 0)
                diskPct = (int)Math.Clamp(100 - drive.AvailableFreeSpace * 100.0 / drive.TotalSize, 0, 100);
            return new { cpuPct, memPct, diskPct };
        }
        catch
        {
            return new { cpuPct = 5, memPct = 40, diskPct = 50 };
        }
    }
}
