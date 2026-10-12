using System.Runtime.InteropServices;
using SboxNetworkStorage.Server.Configuration;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Updates;

namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>One live sample. Rates are per second over <see cref="IntervalSeconds"/>. Null = not reported by this platform.</summary>
public sealed record HostSample(
    long Seq,
    long At,
    double IntervalSeconds,
    double? Cpu,
    double? ProcessCpu,
    double? Load1,
    long? MemoryUsed,
    long? MemoryTotal,
    long ProcessMemory,
    long GcHeap,
    double? NetRx,
    double? NetTx,
    int? Connections,
    int Requests,
    double RequestsPerSecond,
    int Errors,
    double? ErrorRate,
    double? P50,
    double? P95,
    int Threads)
{
    public double? MemoryPercent => MemoryUsed is { } used && MemoryTotal is > 0 ? Math.Round(used * 100d / MemoryTotal.Value, 1) : null;
}

/// <summary>One minute of the 24-hour history (averages, request totals, worst p95).</summary>
public sealed record MinuteRollup(long At, double? Cpu, double? MemoryPercent, long ProcessMemory, double? NetRx, double? NetTx, int Requests, int Errors, double? P95);

/// <summary>Values that change slowly: disk space, folder sizes, newest backup. Refreshed every 30 seconds while sampling.</summary>
public sealed record SlowInfo(long? DataFree, long? DataTotal, long? DataSize, long? BackupSize, DateTimeOffset? NewestBackup);

/// <summary>
/// The single server-wide sampler behind the Server page and alert rules. It runs only while
/// someone watches the page (each poll leases an interval for a few seconds) or an enabled rule
/// needs it (every <see cref="RuleInterval"/>), so many open tabs cost one sampler and an idle
/// server costs nothing. Samples stay in memory: 15 minutes at full resolution and 24 hours as
/// one-minute rollups.
/// </summary>
public sealed class MetricsSampler(
    HostMetricsReader reader,
    RequestMetrics requests,
    MonitoringStore store,
    AlertRuleEngine rules,
    EffectiveConfig config,
    UpdateNoticeState updates,
    TimeProvider time,
    ILogger<MetricsSampler> logger) : BackgroundService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RuleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LiveRetention = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LeaseMinimum = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SizeRefresh = TimeSpan.FromMinutes(5);
    private const int MaxSamples = 3600;
    private const int MaxRollups = 1440;
    private const int MaxViewers = 64;
    private const int MaxSizeEntries = 200_000;

    private readonly Lock _gate = new();
    private readonly LinkedList<(HostSample Sample, RequestWindow Window)> _samples = new();
    private readonly LinkedList<MinuteRollup> _rollups = new();
    private readonly Dictionary<string, (TimeSpan Interval, DateTimeOffset Expires)> _viewers = new(StringComparer.Ordinal);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HostReading? _previous;
    private long _seq;
    private long _currentMinute;
    private DateTimeOffset _lastRules = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSlow = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSizes = DateTimeOffset.MinValue;
    private SlowInfo _slow = new(null, null, null, null, null);
    private long? _dataSize;
    private long? _backupSize;
    private int _sizing;

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public string BackupDirectory => Path.Combine(config.DataDirectory, "backups");

    public SlowInfo Slow
    {
        get { lock (_gate) return _slow; }
    }

    /// <summary>True while the sampler is taking samples.</summary>
    public bool Running => _previous is not null;

    /// <summary>Registers (or renews) a page viewer that wants a sample every <paramref name="interval"/>.</summary>
    public TimeSpan Watch(string viewer, TimeSpan interval)
    {
        interval = interval < MinInterval ? MinInterval : interval > MaxInterval ? MaxInterval : interval;
        var now = time.GetUtcNow();
        bool faster;
        lock (_gate)
        {
            var current = DesiredIntervalLocked(now);
            if (!_viewers.ContainsKey(viewer) && _viewers.Count >= MaxViewers)
            {
                // Drop the lease closest to expiry; viewers renew on every poll.
                _viewers.Remove(_viewers.MinBy(entry => entry.Value.Expires).Key);
            }

            _viewers[viewer] = (interval, now + Max(LeaseMinimum, interval * 4));
            faster = current is null || interval < current;
        }

        if (faster) Wake();
        return interval;
    }

    /// <summary>Samples after <paramref name="afterSeq"/>, oldest first, at most <paramref name="max"/> (the newest).</summary>
    public IReadOnlyList<HostSample> Since(long afterSeq, int max)
    {
        lock (_gate)
        {
            var list = new List<HostSample>();
            for (var node = _samples.Last; node is not null && list.Count < max && node.Value.Sample.Seq > afterSeq; node = node.Previous)
                list.Add(node.Value.Sample);
            list.Reverse();
            return list;
        }
    }

    /// <summary>Full-resolution samples over the last <paramref name="span"/> (at most 15 minutes).</summary>
    public IReadOnlyList<HostSample> Recent(TimeSpan span)
    {
        var from = (time.GetUtcNow() - span).ToUnixTimeMilliseconds();
        lock (_gate) return _samples.Where(s => s.Sample.At >= from).Select(s => s.Sample).ToList();
    }

    public IReadOnlyList<MinuteRollup> Rollups()
    {
        lock (_gate) return [.. _rollups];
    }

    public HostSample? Latest
    {
        get { lock (_gate) return _samples.Last?.Value.Sample; }
    }

    /// <summary>Requests over the last <paramref name="span"/> and the seconds of sampling it actually covers.</summary>
    public (RequestWindow Window, double CoveredSeconds) RequestWindowOver(TimeSpan span)
    {
        var from = (time.GetUtcNow() - span).ToUnixTimeMilliseconds();
        lock (_gate)
        {
            var inside = _samples.Where(s => s.Sample.At > from).ToList();
            return (RequestWindow.Merge(inside.Select(s => s.Window)), inside.Sum(s => s.Sample.IntervalSeconds));
        }
    }

    /// <summary>Wakes the loop: rules changed or a viewer wants a faster interval.</summary>
    public void Wake() => Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        store.Changed += Wake;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= Wake;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.GetBoolean("adminpanel.demo_read_only"))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }
        await rules.AnnounceStartAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var signal = _wake.Task;
            TimeSpan? interval;
            lock (_gate) interval = DesiredIntervalLocked(time.GetUtcNow());
            if (interval is null)
            {
                _previous = null;
                await signal.WaitAsync(stoppingToken);
                continue;
            }

            try
            {
                var sample = Collect();
                if (sample is not null && time.GetUtcNow() - _lastRules >= RuleInterval - TimeSpan.FromMilliseconds(50))
                {
                    _lastRules = time.GetUtcNow();
                    rules.Evaluate(this, updates.Current?.UpdateAvailable == true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Collecting a monitoring sample failed");
            }

            using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(Task.Delay(interval.Value, time, delay.Token), signal);
            await delay.CancelAsync();
        }
    }

    /// <summary>Takes one reading. The first reading after an idle period is only a baseline and returns null.</summary>
    internal HostSample? Collect()
    {
        var now = time.GetUtcNow();
        var reading = reader.Read(now);
        var window = requests.Drain();
        var previous = _previous;
        _previous = reading;
        RefreshSlow(now);
        if (previous is null) return null;

        var elapsed = reading.At - previous.At;
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var memory = GC.GetGCMemoryInfo();
        var sample = new HostSample(
            Seq: Interlocked.Increment(ref _seq),
            At: now.ToUnixTimeMilliseconds(),
            IntervalSeconds: Math.Round(seconds, 3),
            Cpu: reader.CpuPercent(previous, reading, elapsed),
            ProcessCpu: reader.ProcessCpuPercent(previous, reading, elapsed),
            Load1: reading.LoadAverage1,
            MemoryUsed: reading.MemoryUsedBytes,
            MemoryTotal: reading.MemoryTotalBytes,
            ProcessMemory: Environment.WorkingSet,
            GcHeap: memory.HeapSizeBytes,
            NetRx: Rate(previous.NetworkRxBytes, reading.NetworkRxBytes, seconds),
            NetTx: Rate(previous.NetworkTxBytes, reading.NetworkTxBytes, seconds),
            Connections: requests.ActiveConnections,
            Requests: window.Requests,
            RequestsPerSecond: Math.Round(window.Requests / seconds, 2),
            Errors: window.Errors,
            ErrorRate: window.ErrorRatePercent is { } rate ? Math.Round(rate, 2) : null,
            P50: window.Percentile(0.5),
            P95: window.Percentile(0.95),
            Threads: ThreadPool.ThreadCount);
        Add(sample, window, now);
        return sample;
    }

    private void Add(HostSample sample, RequestWindow window, DateTimeOffset now)
    {
        lock (_gate)
        {
            _samples.AddLast((sample, window));
            var cutoff = (now - LiveRetention).ToUnixTimeMilliseconds();
            while (_samples.Count > MaxSamples || _samples.First!.Value.Sample.At < cutoff) _samples.RemoveFirst();

            var minute = sample.At / 60_000;
            if (_currentMinute != 0 && minute != _currentMinute) RollUpLocked(_currentMinute);
            _currentMinute = minute;
        }
    }

    private void RollUpLocked(long minute)
    {
        var inMinute = _samples.Where(s => s.Sample.At / 60_000 == minute).ToList();
        if (inMinute.Count == 0) return;
        var merged = RequestWindow.Merge(inMinute.Select(s => s.Window));
        double? Average(Func<HostSample, double?> pick)
        {
            var values = inMinute.Select(s => pick(s.Sample)).Where(v => v is not null).Select(v => v!.Value).ToList();
            return values.Count == 0 ? null : Math.Round(values.Average(), 2);
        }

        _rollups.AddLast(new MinuteRollup(minute * 60_000, Average(s => s.Cpu), Average(s => s.MemoryPercent),
            inMinute.Max(s => s.Sample.ProcessMemory), Average(s => s.NetRx), Average(s => s.NetTx),
            merged.Requests, merged.Errors, merged.Percentile(0.95)));
        while (_rollups.Count > MaxRollups) _rollups.RemoveFirst();
    }

    private TimeSpan? DesiredIntervalLocked(DateTimeOffset now)
    {
        TimeSpan? interval = rules.NeedsSampling(store.Rules) ? RuleInterval : null;
        foreach (var stale in _viewers.Where(v => v.Value.Expires <= now).Select(v => v.Key).ToList()) _viewers.Remove(stale);
        foreach (var (_, lease) in _viewers)
            if (interval is null || lease.Interval < interval) interval = lease.Interval;
        return interval;
    }

    private void RefreshSlow(DateTimeOffset now)
    {
        if (now - _lastSlow < SlowRefresh) return;
        _lastSlow = now;
        long? free = null, total = null;
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(config.DataDirectory));
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        DateTimeOffset? newest = null;
        try
        {
            if (Directory.Exists(BackupDirectory))
            {
                foreach (var file in new DirectoryInfo(BackupDirectory).EnumerateFiles())
                {
                    if (newest is null || file.LastWriteTimeUtc > newest) newest = file.LastWriteTimeUtc;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (now - _lastSizes >= SizeRefresh && Interlocked.CompareExchange(ref _sizing, 1, 0) == 0)
        {
            _lastSizes = now;
            // Folder sizes walk the tree: off the sampling path, capped, every few minutes.
            _ = Task.Run(() =>
            {
                try
                {
                    var dataSize = FolderSize(config.DataDirectory);
                    var backupSize = Directory.Exists(BackupDirectory) ? FolderSize(BackupDirectory) : 0;
                    lock (_gate)
                    {
                        _dataSize = dataSize;
                        _backupSize = backupSize;
                        _slow = _slow with { DataSize = dataSize, BackupSize = backupSize };
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _sizing, 0);
                }
            });
        }

        lock (_gate) _slow = new SlowInfo(free, total, _dataSize, _backupSize, newest);
    }

    private static long? FolderSize(string path)
    {
        try
        {
            long size = 0;
            var count = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (++count > MaxSizeEntries) return null;
                size += file.Length;
            }

            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double? Rate(long? before, long? after, double seconds)
        => before is { } b && after is { } a && a >= b ? Math.Round((a - b) / seconds, 1) : null;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>Process and platform facts for the page header.</summary>
    public static string Platform => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string Version => BuildInfo.Version;
}
