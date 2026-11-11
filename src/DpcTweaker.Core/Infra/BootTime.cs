namespace DpcTweaker.Core.Infra;

public static class BootTime
{
    /// <summary>
    /// When the running Windows session started. With Fast Startup the kernel
    /// session survives a shutdown, so this only moves on a real restart, which
    /// is exactly what interrupt, MSI and GPU key changes need.
    /// </summary>
    public static DateTime LastBoot => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
