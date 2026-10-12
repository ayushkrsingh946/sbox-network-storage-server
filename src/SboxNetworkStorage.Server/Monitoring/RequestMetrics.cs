using System.Diagnostics.Metrics;

namespace SboxNetworkStorage.Server.Monitoring;

/// <summary>
/// Request rate, 5xx count, latency histogram and open connections for the whole server, read from
/// the built-in ASP.NET Core meters (<c>http.server.request.duration</c>,
/// <c>kestrel.active_connections</c>). Recording is a few interlocked adds per request; the sampler
/// drains the counters on each sample. Polls of the Server page itself are left out so watching
/// the page does not change what it shows.
/// </summary>
public sealed class RequestMetrics : IDisposable
{
    /// <summary>Latency bucket upper bounds in milliseconds (1 ms to 60 s, factor 1.3), plus one overflow bucket.</summary>
    public static readonly double[] BucketBoundsMs = BuildBounds();

    public static readonly HashSet<string> IgnoredRoutes = new(StringComparer.Ordinal) { "/dashboard/server/stats", "dashboard/server/stats" };

    private readonly MeterListener _listener = new();
    private readonly Lock _gate = new();
    private int[] _buckets = new int[BucketBoundsMs.Length + 1];
    private int _requests;
    private int _errors;
    private long _activeConnections;
    private bool _sawConnections;

    /// <param name="meterFactory">This host's meter factory: instruments from other hosts in the same process (tests) are ignored.</param>
    public RequestMetrics(IMeterFactory? meterFactory = null)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (meterFactory is not null && !ReferenceEquals(instrument.Meter.Scope, meterFactory)) return;
            if (instrument is { Meter.Name: "Microsoft.AspNetCore.Hosting", Name: "http.server.request.duration" }
                || instrument is { Meter.Name: "Microsoft.AspNetCore.Server.Kestrel", Name: "kestrel.active_connections" })
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>(OnDuration);
        _listener.SetMeasurementEventCallback<long>(OnConnections);
        _listener.Start();
    }

    /// <summary>Open connections, or null when the server does not report them (for example behind a test host).</summary>
    public int? ActiveConnections
    {
        get
        {
            lock (_gate) return _sawConnections ? (int)Math.Max(0, _activeConnections) : null;
        }
    }

    /// <summary>Returns and resets the counts collected since the previous call.</summary>
    public RequestWindow Drain()
    {
        lock (_gate)
        {
            var window = new RequestWindow(_requests, _errors, _buckets);
            _buckets = new int[BucketBoundsMs.Length + 1];
            _requests = 0;
            _errors = 0;
            return window;
        }
    }

    /// <summary>Records one request directly; used by tests and by hosts without the ASP.NET Core meter.</summary>
    public void Record(double milliseconds, int statusCode)
    {
        var bucket = Array.BinarySearch(BucketBoundsMs, milliseconds);
        if (bucket < 0) bucket = ~bucket;
        lock (_gate)
        {
            _requests++;
            if (statusCode >= 500) _errors++;
            _buckets[bucket]++;
        }
    }

    private void OnDuration(Instrument instrument, double seconds, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var status = 0;
        foreach (var tag in tags)
        {
            if (tag.Key == "http.route" && tag.Value is string route && IgnoredRoutes.Contains(route)) return;
            if (tag.Key == "http.response.status_code" && tag.Value is int code) status = code;
        }

        Record(seconds * 1000d, status);
    }

    private void OnConnections(Instrument instrument, long delta, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
        {
            _sawConnections = true;
            _activeConnections += delta;
        }
    }

    public void Dispose() => _listener.Dispose();

    private static double[] BuildBounds()
    {
        var bounds = new List<double>();
        for (var value = 1d; value < 60_000d; value *= 1.3) bounds.Add(Math.Round(value, 2));
        bounds.Add(60_000d);
        return [.. bounds];
    }
}

/// <summary>Requests seen over one sample interval. <see cref="Buckets"/> follows <see cref="RequestMetrics.BucketBoundsMs"/>.</summary>
public sealed record RequestWindow(int Requests, int Errors, int[] Buckets)
{
    public static RequestWindow Empty { get; } = new(0, 0, new int[RequestMetrics.BucketBoundsMs.Length + 1]);

    /// <summary>Merges windows (for rule windows and minute rollups).</summary>
    public static RequestWindow Merge(IEnumerable<RequestWindow> windows)
    {
        var buckets = new int[RequestMetrics.BucketBoundsMs.Length + 1];
        int requests = 0, errors = 0;
        foreach (var window in windows)
        {
            requests += window.Requests;
            errors += window.Errors;
            for (var i = 0; i < buckets.Length; i++) buckets[i] += window.Buckets[i];
        }

        return new RequestWindow(requests, errors, buckets);
    }

    /// <summary>Latency at <paramref name="quantile"/> as the upper bound of its bucket, or null with no requests.</summary>
    public double? Percentile(double quantile)
    {
        if (Requests == 0) return null;
        var target = Math.Max(1, (long)Math.Ceiling(Requests * quantile));
        long seen = 0;
        for (var i = 0; i < Buckets.Length; i++)
        {
            seen += Buckets[i];
            if (seen >= target)
                return i < RequestMetrics.BucketBoundsMs.Length ? RequestMetrics.BucketBoundsMs[i] : RequestMetrics.BucketBoundsMs[^1];
        }

        return RequestMetrics.BucketBoundsMs[^1];
    }

    public double? ErrorRatePercent => Requests == 0 ? null : Errors * 100d / Requests;
}
