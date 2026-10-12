# Server monitoring

The owner console has two server-wide pages next to **All projects**:

- **Server** (`/dashboard/server`): live CPU, memory, disk, network and
  request figures for this sbox-ns process and its host or container.
- **Alerts** (`/dashboard/server/alerts`): alert rules over those figures,
  the notification channels, and a history of what was sent. Channels and
  delivery are described in [alerting.md](alerting.md).

Both are owner-only operator diagnostics. They are never exposed on
game-facing routes, and the [read-only demo](admin-panel.md#public-read-only-demo)
does not serve them. No configuration is needed and nothing is sent anywhere
unless you add a channel.

## Server page

The header shows the version, uptime, .NET runtime, OS and architecture,
database provider, version and ping, and the data folder.

The live tiles update every 500 ms by default. Pick 250 ms, 1 s, 2 s or 5 s,
or pause; the choice is remembered in the browser. Each tile has a sparkline
of the last 2 minutes, or of the last 15 minutes or 24 hours (one point per
minute) when you change **Chart**. Polling pauses while the browser tab is
hidden. Without JavaScript the page shows the latest sample; refresh it to
update.

| Tile | Source |
| --- | --- |
| CPU | Linux: `/proc/stat`, or the cgroup's `cpu.stat` against its `cpu.max` (v2) or `cpuacct.usage` against `cpu.cfs_quota_us` (v1) when a CPU limit is set. macOS: `host_statistics`. Windows: `GetSystemTimes`. |
| Server process CPU | Process CPU time against every core the process may use. |
| Load average (1 min) | Linux `/proc/loadavg`, macOS `getloadavg`. Not reported on Windows. |
| Memory in use | Linux: `/proc/meminfo` (`MemTotal - MemAvailable`), or the cgroup's usage without reclaimable page cache against `memory.max` / `memory.limit_in_bytes`. macOS: app, wired and compressed memory. Windows: `GlobalMemoryStatusEx`. |
| Server process memory, .NET heap | Working set and GC heap size. |
| Network in / out | Linux `/proc/net/dev` of the process's network namespace (so a container reports its own interfaces); elsewhere the .NET interface statistics. Loopback is excluded. |
| Open connections | Kestrel's `kestrel.active_connections`. |
| Requests per second, 5xx error rate, latency p50/p95 | ASP.NET Core's `http.server.request.duration`, over the last sample. The page's own polling is left out. Percentiles are the upper bound of a latency bucket (buckets grow by 30 %). |
| Thread pool threads | .NET thread pool size. |
| Disk and backups | Free and total space of the data volume, the data and `backups` folder sizes (measured every 5 minutes, stopping at 200,000 files) and the newest file in `<data>/backups`. |

In a container with a CPU or memory limit, CPU and memory are relative to
the limit, and the tile says "container limit". A value the platform does
not report is shown as **Unavailable**, never as 0. Request tiles show
**No requests** for a sample without requests.

### Sampling and cost

One sampler serves every open page and every rule. It runs only while
someone watches the Server page (each poll keeps it going for a few seconds,
at the fastest interval any open page asked for) or while an enabled rule
needs it (one sample every 5 seconds). With no page open and no rule, it
takes no samples.

Samples stay in memory: 15 minutes at full resolution (at most 3,600 samples)
and 24 hours as one-minute rollups. Nothing is written to the database, and
a restart starts the history over. Each instance samples itself; several
instances sharing one PostgreSQL database each show their own figures.

Measured on an 8-core Apple M-series laptop (macOS, SQLite, Debug build): the
idle server used 0.7 % of one core; with one Server page open at 500 ms it
used 3.9 % of one core, which includes the page's two polls per second.

## Alert rules

A rule is a metric, a comparison, a threshold, a duration, a severity and
the channels to notify (none ticked means every channel that accepts the
severity). Rules are checked every 5 seconds.

| Metric | Unit | How it is evaluated |
| --- | --- | --- |
| `cpu.percent` CPU use | % | gauge |
| `process.cpu.percent` Server process CPU | % | gauge |
| `memory.percent` Memory in use | % | gauge |
| `process.memory.mb` Server process memory | MB | gauge |
| `disk.data.free.gb` Free disk space (data folder) | GB | gauge |
| `disk.data.free.percent` Free disk space (data folder) | % | gauge |
| `http.error_rate.percent` Request error rate | % | window, needs 20 requests |
| `http.p95.ms` Request latency p95 | ms | window, needs 20 requests |
| `http.requests.per_second` Request rate | req/s | window |
| `backup.age.hours` Newest backup age | h | gauge, no result without a backup |
| `update.available` Update available | 1 or 0 | gauge |
| `server.started` Server started | | one notification at each start |

- A **gauge** rule becomes *pending* when its condition holds and *firing*
  once it has held for the duration (0 fires on the first sample).
- A **window** rule measures the metric over the duration (at least 60
  seconds) and fires as soon as the condition holds. It has no result until
  half the window has been sampled, or with fewer than 20 requests for error
  rate and latency.
- When the condition stops holding, a firing rule is *resolved* and a
  resolved notification goes to the same channels.
- **No data** (an unavailable metric, too few requests) never fires and
  never resolves a firing rule.
- The **cooldown** (default 10 minutes) is the minimum time between two
  firing notifications of one rule. A rule that fires again sooner changes
  state on the page without notifying, and its resolve is not sent either.
  Rule notifications also share the server-wide limits in
  [alerting.md](alerting.md#limits).
- Rule state is kept in memory. Editing a rule, or restarting the server,
  starts its evaluation over; a condition that still holds fires again.

Examples:

| Goal | Metric | Comparison and threshold | Duration |
| --- | --- | --- | --- |
| CPU above 90 % for 2 minutes | CPU use | `> 90` | 120 |
| Less than 5 GB free | Free disk space (GB) | `< 5` | 0 |
| Error rate above 5 % over 5 minutes | Request error rate | `> 5` | 300 |
| p95 above 800 ms over 5 minutes | Request latency p95 | `> 800` | 300 |
| Backup older than 24 hours | Newest backup age | `> 24` | 0 |

The built-in **Captured errors** source ([alerting.md](alerting.md#what-triggers-a-captured-error-alert))
is listed with the rules. It is always on, uses severity `error`, and goes
to every channel that accepts `error`.

## History and storage

**Alerts → History** lists the last 500 sent alerts, test notifications and
rule or channel changes, newest first, with the delivery result (for
example which channel failed and why).

| `<data>/state/monitoring.json` | rules, channels and provider setups without secrets |
| `<data>/state/monitoring-history.json` | the history |
| `<data>/state/secrets/monitoring/channel-<id>.json` | one channel's secrets, owner-only |
| `<data>/state/secrets/monitoring/provider-<id>.json` | one provider's token and settings, owner-only |

A damaged `monitoring.json` is moved aside as
`monitoring.json.damaged-<timestamp>` and the server starts without rules
and channels. Rules, console channels and history are not part of a server
export; recreate them after moving to a new server.
