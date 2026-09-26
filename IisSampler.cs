using System.Diagnostics;

static class IisSampler
{
    public static object? Read()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.SystemDirectory + "\\inetsrv\\appcmd.exe",
                Arguments = "list wp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!File.Exists(psi.FileName))
                return new { present = false, site = "unknown", pool = "unknown" };
            using var p = Process.Start(psi);
            var output = p!.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            var running = output.IndexOf("DefaultAppPool", StringComparison.OrdinalIgnoreCase) >= 0;
            return new {
                present = true,
                site = running ? "healthy" : "critical",
                pool = running ? "healthy" : "critical",
            };
        }
        catch
        {
            return new { present = false, site = "unknown", pool = "unknown" };
        }
    }
}
