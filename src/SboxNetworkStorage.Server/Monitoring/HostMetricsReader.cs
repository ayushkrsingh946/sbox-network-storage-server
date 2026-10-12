using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>Raw host readings at one instant. Null means the platform does not report the value.</summary>
public sealed record HostReading(
    DateTimeOffset At,
    TimeSpan ProcessCpu,
    CpuTicks? Cpu,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? LoadAverage1,
    long? NetworkRxBytes,
    long? NetworkTxBytes);

/// <summary>Busy and total CPU time, in any unit, for a percentage between two readings.</summary>
public readonly record struct CpuTicks(double Busy, double Total);

/// <summary>
/// Reads CPU, memory, load and network counters for the host, or for the container when the
/// process runs under a cgroup CPU or memory limit (cgroup v2 and v1), so a container reports
/// its limits rather than the host's. macOS and Windows use the system APIs. Any value the
/// platform does not expose is null and shown as unavailable, never as 0.
/// </summary>
public sealed class HostMetricsReader
{
    private readonly CgroupLimits? _cgroup = OperatingSystem.IsLinux() ? CgroupLimits.Detect() : null;

    /// <summary>What the CPU and memory figures describe, for the page label.</summary>
    public string Scope => _cgroup is { } limits && (limits.CpuCores is not null || limits.MemoryLimitBytes is not null)
        ? "container"
        : "host";

    /// <summary>CPU cores the CPU percentage is relative to.</summary>
    public double CpuCores => _cgroup?.CpuCores ?? Environment.ProcessorCount;

    public long? MemoryLimitBytes => _cgroup?.MemoryLimitBytes;

    public HostReading Read(DateTimeOffset now)
    {
        var (rx, tx) = ReadNetwork();
        var (used, total) = ReadMemory();
        return new HostReading(now, Environment.CpuUsage.TotalTime, ReadCpu(), used, total, ReadLoadAverage(), rx, tx);
    }

    private CpuTicks? ReadCpu()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (_cgroup is { CpuCores: not null } limits && limits.ReadCpuUsageMicroseconds() is { } usage)
                {
                    // Busy time against the wall time the limit allows; the caller divides by elapsed * cores.
                    return new CpuTicks(usage, double.NaN);
                }

                return ProcStat();
            }

            if (OperatingSystem.IsMacOS()) return MacCpu();
            if (OperatingSystem.IsWindows()) return WindowsCpu();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    /// <summary>CPU percentage between two readings over <paramref name="elapsed"/>.</summary>
    public double? CpuPercent(HostReading previous, HostReading current, TimeSpan elapsed)
    {
        if (previous.Cpu is not { } before || current.Cpu is not { } after || elapsed <= TimeSpan.Zero) return null;
        if (double.IsNaN(after.Total))
        {
            // Cgroup usage in microseconds against the CPU limit.
            var allowed = elapsed.TotalMicroseconds * CpuCores;
            return allowed <= 0 ? null : Clamp((after.Busy - before.Busy) * 100d / allowed);
        }

        var total = after.Total - before.Total;
        return total <= 0 ? null : Clamp((after.Busy - before.Busy) * 100d / total);
    }

    /// <summary>This process's CPU percentage against every core it may use.</summary>
    public double? ProcessCpuPercent(HostReading previous, HostReading current, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return null;
        return Clamp((current.ProcessCpu - previous.ProcessCpu).TotalMilliseconds * 100d / (elapsed.TotalMilliseconds * CpuCores));
    }

    private static double Clamp(double percent) => Math.Round(Math.Clamp(percent, 0, 100), 1);

    private static CpuTicks? ProcStat()
    {
        using var reader = new StreamReader("/proc/stat");
        var line = reader.ReadLine();
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double total = 0, idle = 0;
        // user nice system idle iowait irq softirq steal (guest time is already in user).
        for (var i = 1; i < Math.Min(parts.Length, 9); i++)
        {
            var value = double.Parse(parts[i], CultureInfo.InvariantCulture);
            total += value;
            if (i is 4 or 5) idle += value;
        }

        return new CpuTicks(total - idle, total);
    }

    private (long? Used, long? Total) ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (_cgroup is { MemoryLimitBytes: { } limit } limits && limits.ReadMemoryUsedBytes() is { } used)
                    return (used, limit);
                return ProcMeminfo();
            }

            if (OperatingSystem.IsMacOS()) return MacMemory();
            if (OperatingSystem.IsWindows()) return WindowsMemory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return (null, null);
    }

    private static (long? Used, long? Total) ProcMeminfo()
    {
        long? total = null, available = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kilobytes(line);
            if (total is not null && available is not null) break;
        }

        return total is null || available is null ? (null, total) : (total - available, total);

        static long Kilobytes(string line)
            => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
    }

    private static double? ReadLoadAverage()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var text = File.ReadAllText("/proc/loadavg");
                return double.Parse(text.Split(' ')[0], CultureInfo.InvariantCulture);
            }

            if (OperatingSystem.IsMacOS())
            {
                var loads = new double[3];
                return MacNative.getloadavg(loads, 3) >= 1 ? Math.Round(loads[0], 2) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    private static (long? Rx, long? Tx) ReadNetwork()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Namespace-aware: inside a container this is the container's own interfaces.
                long rx = 0, tx = 0;
                foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0 || line[..colon].Trim() == "lo") continue;
                    var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    rx += long.Parse(fields[0], CultureInfo.InvariantCulture);
                    tx += long.Parse(fields[8], CultureInfo.InvariantCulture);
                }

                return (rx, tx);
            }

            long received = 0, sent = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus != OperationalStatus.Up) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }

            return (received, sent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NetworkInformationException or PlatformNotSupportedException or IndexOutOfRangeException)
        {
            return (null, null);
        }
    }

    private static CpuTicks? MacCpu()
    {
        // host_cpu_load_info: user, system, idle, nice ticks.
        var info = new uint[4];
        var count = 4;
        if (MacNative.host_statistics(MacNative.mach_host_self(), MacNative.HostCpuLoadInfo, info, ref count) != 0) return null;
        double user = info[0], system = info[1], idle = info[2], nice = info[3];
        return new CpuTicks(user + system + nice, user + system + idle + nice);
    }

    private static (long? Used, long? Total) MacMemory()
    {
        long total = 0;
        var size = (nint)sizeof(long);
        if (MacNative.sysctlbyname("hw.memsize", ref total, ref size, 0, 0) != 0) return (null, null);
        // vm_statistics64 as 32-bit words (38 of them, uint64 fields take two). Used memory as Activity
        // Monitor counts it: app memory (internal - purgeable) + wired + compressed.
        var words = new uint[MacNative.HostVmInfo64Count];
        var count = MacNative.HostVmInfo64Count;
        if (MacNative.host_statistics64(MacNative.mach_host_self(), MacNative.HostVmInfo64, words, ref count) != 0) return (null, total);
        long page = Environment.SystemPageSize;
        long wired = words[3];
        long purgeable = words[22];
        long compressor = words[32];
        long internalPages = words[35];
        var used = (internalPages - purgeable + wired + compressor) * page;
        return used <= 0 || used > total ? (null, total) : (used, total);
    }

    private static CpuTicks? WindowsCpu()
    {
        if (!WindowsNative.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        // Kernel time includes idle time.
        double busy = kernel - idle + user, total = kernel + user;
        return new CpuTicks(busy, total);
    }

    private static (long? Used, long? Total) WindowsMemory()
    {
        var status = new WindowsNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<WindowsNative.MemoryStatusEx>() };
        if (!WindowsNative.GlobalMemoryStatusEx(ref status)) return (null, null);
        return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    private static class MacNative
    {
        public const int HostCpuLoadInfo = 3;
        public const int HostVmInfo64 = 4;
        public const int HostVmInfo64Count = 38;

        [DllImport("/usr/lib/libSystem.dylib")]
        public static extern uint mach_host_self();

        [DllImport("/usr/lib/libSystem.dylib")]
        public static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref int count);

        [DllImport("/usr/lib/libSystem.dylib")]
        public static extern int host_statistics64(uint host, int flavor, [Out] uint[] info, ref int count);

        [DllImport("/usr/lib/libSystem.dylib")]
        public static extern int sysctlbyname([MarshalAs(UnmanagedType.LPStr)] string name, ref long value, ref nint size, nint newValue, nint newSize);

        [DllImport("/usr/lib/libSystem.dylib")]
        public static extern int getloadavg([Out] double[] loads, int count);
    }

    private static class WindowsNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}

/// <summary>
/// CPU and memory limits of the process's cgroup (v2 unified, or v1 cpu/cpuacct/memory controllers).
/// Null limits mean "unlimited": the reader falls back to host-wide figures.
/// </summary>
internal sealed class CgroupLimits
{
    private readonly string? _cpuUsageFile;
    private readonly bool _cpuUsageNanoseconds;
    private readonly string? _memoryUsageFile;
    private readonly string? _memoryStatFile;

    private CgroupLimits(double? cpuCores, long? memoryLimit, string? cpuUsageFile, bool cpuUsageNanoseconds, string? memoryUsageFile, string? memoryStatFile)
    {
        CpuCores = cpuCores;
        MemoryLimitBytes = memoryLimit;
        _cpuUsageFile = cpuUsageFile;
        _cpuUsageNanoseconds = cpuUsageNanoseconds;
        _memoryUsageFile = memoryUsageFile;
        _memoryStatFile = memoryStatFile;
    }

    public double? CpuCores { get; }

    public long? MemoryLimitBytes { get; }

    public static CgroupLimits? Detect(string root = "/sys/fs/cgroup", string selfCgroup = "/proc/self/cgroup")
    {
        try
        {
            if (!File.Exists(selfCgroup)) return null;
            var lines = File.ReadAllLines(selfCgroup);
            var unified = lines.FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
            if (unified is not null && File.Exists(Path.Combine(root, "cgroup.controllers")))
                return DetectV2(root, unified[3..]);
            return DetectV1(root, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static CgroupLimits DetectV2(string root, string relative)
    {
        var own = Path.Combine(root, relative.TrimStart('/'));
        if (!Directory.Exists(own)) own = root;
        double? cores = null;
        long? memory = null;
        // The tightest limit on the path from our cgroup up to the root applies.
        for (var dir = own; dir.Length >= root.Length; dir = Path.GetDirectoryName(dir) ?? "")
        {
            var cpuMax = Read(Path.Combine(dir, "cpu.max"));
            if (cpuMax is not null)
            {
                var parts = cpuMax.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] != "max")
                {
                    var limit = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
                    cores = cores is null ? limit : Math.Min(cores.Value, limit);
                }
            }

            var memoryMax = Read(Path.Combine(dir, "memory.max"));
            if (memoryMax is not null && memoryMax != "max")
            {
                var limit = long.Parse(memoryMax, CultureInfo.InvariantCulture);
                memory = memory is null ? limit : Math.Min(memory.Value, limit);
            }

            if (string.Equals(dir.TrimEnd('/'), root.TrimEnd('/'), StringComparison.Ordinal)) break;
        }

        var cpuStat = Path.Combine(own, "cpu.stat");
        var memoryCurrent = Path.Combine(own, "memory.current");
        return new CgroupLimits(cores, memory,
            File.Exists(cpuStat) ? cpuStat : null, cpuUsageNanoseconds: false,
            File.Exists(memoryCurrent) ? memoryCurrent : null,
            File.Exists(Path.Combine(own, "memory.stat")) ? Path.Combine(own, "memory.stat") : null);
    }

    private static CgroupLimits DetectV1(string root, string[] lines)
    {
        string? Controller(string name)
        {
            foreach (var line in lines)
            {
                var parts = line.Split(':', 3);
                if (parts.Length == 3 && parts[1].Split(',').Contains(name))
                {
                    foreach (var mount in new[] { Path.Combine(root, parts[1]), Path.Combine(root, name) })
                    {
                        var nested = Path.Combine(mount, parts[2].TrimStart('/'));
                        if (Directory.Exists(nested)) return nested;
                        if (Directory.Exists(mount)) return mount;
                    }
                }
            }

            return null;
        }

        double? cores = null;
        string? cpuUsage = null;
        if (Controller("cpu") is { } cpu
            && Read(Path.Combine(cpu, "cpu.cfs_quota_us")) is { } quota && Read(Path.Combine(cpu, "cpu.cfs_period_us")) is { } period
            && long.Parse(quota, CultureInfo.InvariantCulture) > 0)
        {
            cores = double.Parse(quota, CultureInfo.InvariantCulture) / double.Parse(period, CultureInfo.InvariantCulture);
        }

        if (Controller("cpuacct") is { } acct && File.Exists(Path.Combine(acct, "cpuacct.usage")))
            cpuUsage = Path.Combine(acct, "cpuacct.usage");

        long? memory = null;
        string? memoryUsage = null, memoryStat = null;
        if (Controller("memory") is { } mem)
        {
            // An unset v1 limit reads as a huge page-rounded number.
            if (Read(Path.Combine(mem, "memory.limit_in_bytes")) is { } limit && long.TryParse(limit, CultureInfo.InvariantCulture, out var bytes) && bytes < long.MaxValue / 2)
                memory = bytes;
            if (File.Exists(Path.Combine(mem, "memory.usage_in_bytes"))) memoryUsage = Path.Combine(mem, "memory.usage_in_bytes");
            if (File.Exists(Path.Combine(mem, "memory.stat"))) memoryStat = Path.Combine(mem, "memory.stat");
        }

        return new CgroupLimits(cores, memory, cpuUsage, cpuUsageNanoseconds: true, memoryUsage, memoryStat);
    }

    /// <summary>Total CPU time the cgroup used, in microseconds.</summary>
    public double? ReadCpuUsageMicroseconds()
    {
        if (_cpuUsageFile is null) return null;
        if (_cpuUsageNanoseconds) return double.Parse(File.ReadAllText(_cpuUsageFile).Trim(), CultureInfo.InvariantCulture) / 1000d;
        foreach (var line in File.ReadLines(_cpuUsageFile))
        {
            if (line.StartsWith("usage_usec ", StringComparison.Ordinal))
                return double.Parse(line[11..], CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>Memory in use without reclaimable page cache, as <c>docker stats</c> counts it.</summary>
    public long? ReadMemoryUsedBytes()
    {
        if (_memoryUsageFile is null) return null;
        var used = long.Parse(File.ReadAllText(_memoryUsageFile).Trim(), CultureInfo.InvariantCulture);
        if (_memoryStatFile is not null)
        {
            foreach (var line in File.ReadLines(_memoryStatFile))
            {
                // v2 "inactive_file", v1 "total_inactive_file".
                if (line.StartsWith("inactive_file ", StringComparison.Ordinal) || line.StartsWith("total_inactive_file ", StringComparison.Ordinal))
                {
                    used -= long.Parse(line[(line.IndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        return Math.Max(0, used);
    }

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
