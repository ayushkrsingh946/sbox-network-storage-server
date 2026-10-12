using SboxNetworkStorage.Server.Monitoring;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>Containers must report their own CPU and memory limits, not the host's.</summary>
public sealed class CgroupLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sbox-ns-cgroup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void V2_UsesTheTightestLimitOnThePathAndSubtractsReclaimableCache()
    {
        var fs = Path.Combine(_root, "fs");
        var own = Path.Combine(fs, "system.slice", "sbox-ns.service");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(fs, "cgroup.controllers"), "cpu memory");
        File.WriteAllText(Path.Combine(fs, "system.slice", "cpu.max"), "150000 100000\n");
        File.WriteAllText(Path.Combine(own, "cpu.max"), "400000 100000\n");
        File.WriteAllText(Path.Combine(own, "memory.max"), "1073741824\n");
        File.WriteAllText(Path.Combine(own, "memory.current"), "600000000\n");
        File.WriteAllText(Path.Combine(own, "memory.stat"), "anon 400000000\nfile 200000000\ninactive_file 100000000\n");
        File.WriteAllText(Path.Combine(own, "cpu.stat"), "usage_usec 2500000\nuser_usec 2000000\n");
        var self = Path.Combine(_root, "self-cgroup");
        File.WriteAllText(self, "0::/system.slice/sbox-ns.service\n");

        var limits = CgroupLimits.Detect(fs, self)!;

        Assert.Equal(1.5, limits.CpuCores);
        Assert.Equal(1073741824, limits.MemoryLimitBytes);
        Assert.Equal(500000000, limits.ReadMemoryUsedBytes());
        Assert.Equal(2500000, limits.ReadCpuUsageMicroseconds());
    }

    [Fact]
    public void V2_UnlimitedReportsNoLimits()
    {
        var fs = Path.Combine(_root, "fs");
        Directory.CreateDirectory(fs);
        File.WriteAllText(Path.Combine(fs, "cgroup.controllers"), "cpu memory");
        File.WriteAllText(Path.Combine(fs, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(fs, "memory.max"), "max\n");
        var self = Path.Combine(_root, "self-cgroup");
        File.WriteAllText(self, "0::/\n");

        var limits = CgroupLimits.Detect(fs, self)!;

        Assert.Null(limits.CpuCores);
        Assert.Null(limits.MemoryLimitBytes);
    }

    [Fact]
    public void V1_ReadsQuotaAndIgnoresTheUnsetMemorySentinel()
    {
        var fs = Path.Combine(_root, "fs");
        Directory.CreateDirectory(Path.Combine(fs, "cpu,cpuacct"));
        Directory.CreateDirectory(Path.Combine(fs, "memory"));
        File.WriteAllText(Path.Combine(fs, "cpu,cpuacct", "cpu.cfs_quota_us"), "50000\n");
        File.WriteAllText(Path.Combine(fs, "cpu,cpuacct", "cpu.cfs_period_us"), "100000\n");
        File.WriteAllText(Path.Combine(fs, "cpu,cpuacct", "cpuacct.usage"), "3000000000\n");
        File.WriteAllText(Path.Combine(fs, "memory", "memory.limit_in_bytes"), "9223372036854771712\n");
        var self = Path.Combine(_root, "self-cgroup");
        File.WriteAllText(self, "4:memory:/docker/abc\n3:cpu,cpuacct:/docker/abc\n");

        var limits = CgroupLimits.Detect(fs, self)!;

        Assert.Equal(0.5, limits.CpuCores);
        Assert.Null(limits.MemoryLimitBytes);
        Assert.Equal(3000000, limits.ReadCpuUsageMicroseconds());
    }
}
