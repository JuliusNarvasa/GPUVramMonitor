namespace GpuVramMonitor.Models;

public class GpuProcessInfo
{
    public string ProcessName { get; set; } = "";
    public int PID { get; set; }
    public double VramMB { get; set; }
    public string VramDisplay => $"{VramMB:F1} MB";
    public double BarWidthPercent { get; set; }
}
