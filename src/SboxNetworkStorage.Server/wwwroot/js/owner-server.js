"use strict";
// Server page: polls /dashboard/server/stats at the chosen interval (each poll keeps the shared
// sampler running for a few seconds), updates the tiles and draws inline SVG sparklines.
// Alerts page: shows only the form fields that apply to the chosen channel type and metric.
// Without JavaScript the Server page shows the latest sample and the forms show every field.
(function () {
  var LiveWindowMs = 120000;
  var Units = ["B", "KB", "MB", "GB", "TB"];

  function formatBytes(value) {
    if (value === null || value === undefined) return "Unavailable";
    var v = value, i = 0;
    while (Math.abs(v) >= 1024 && i < Units.length - 1) { v /= 1024; i++; }
    return (i === 0 ? Math.round(v) : v.toFixed(1)) + " " + Units[i];
  }

  function formatPercent(value) {
    return value === null || value === undefined ? "Unavailable" : value.toFixed(1) + " %";
  }

  function formatMs(value) {
    return value === null || value === undefined ? "No requests" : (Math.round(value * 10) / 10) + " ms";
  }

  function formatNumber(value) {
    return value === null || value === undefined ? "Unavailable" : String(Math.round(value * 100) / 100);
  }

  function formatUptime(seconds) {
    var d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
    if (d > 0) return d + " d " + h + " h " + m + " min";
    if (h > 0) return h + " h " + m + " min";
    return m + " min " + Math.floor(seconds % 60) + " s";
  }

  function memoryPercent(sample) {
    return sample.memoryUsed !== null && sample.memoryUsed !== undefined && sample.memoryTotal ? sample.memoryUsed * 100 / sample.memoryTotal : null;
  }

  // Numeric series value for a tile key, from a live sample or a minute rollup.
  function seriesValue(point, key) {
    switch (key) {
      case "memory": return point.memoryPercent !== undefined ? point.memoryPercent : memoryPercent(point);
      case "errorRate": return point.errorRate !== undefined ? point.errorRate : point.requests ? point.errors * 100 / point.requests : null;
      case "rps": return point.requestsPerSecond !== undefined ? point.requestsPerSecond : point.requests / 60;
      default: return point[key] === undefined ? null : point[key];
    }
  }

  // Display text for a tile key from the latest sample.
  function tileText(sample, key) {
    switch (key) {
      case "cpu": case "processCpu": return formatPercent(sample[key]);
      case "memory": var p = memoryPercent(sample); return p === null ? "Unavailable" : formatPercent(p) + " of " + formatBytes(sample.memoryTotal);
      case "processMemory": case "gcHeap": return formatBytes(sample[key]);
      case "netRx": case "netTx": return sample[key] === null ? "Unavailable" : formatBytes(sample[key]) + "/s";
      case "errorRate": return sample.errorRate === null ? "No requests" : formatPercent(sample.errorRate);
      case "p50": case "p95": return formatMs(sample[key]);
      case "rps": return formatNumber(sample.requestsPerSecond);
      case "connections": case "threads": case "load1": return formatNumber(sample[key]);
      default: return "Unavailable";
    }
  }

  // Polyline points scaled into a width x height box; null values are skipped. A flat series sits
  // on the bottom edge when it is 0 and in the middle otherwise.
  function sparkPoints(values, width, height) {
    var present = [];
    for (var i = 0; i < values.length; i++) if (values[i] !== null && values[i] !== undefined && isFinite(values[i])) present.push(i);
    if (present.length === 0) return "";
    var min = Infinity, max = -Infinity;
    present.forEach(function (index) { min = Math.min(min, values[index]); max = Math.max(max, values[index]); });
    var span = max - min;
    var last = Math.max(values.length - 1, 1);
    return present.map(function (index) {
      var x = values.length === 1 ? width : index * width / last;
      var y = span === 0 ? (max === 0 ? height : height / 2) : height - (values[index] - min) * height / span;
      return Math.round(x * 10) / 10 + "," + Math.round(y * 10) / 10;
    }).join(" ");
  }

  if (typeof module !== "undefined" && module.exports) {
    module.exports = { formatBytes: formatBytes, formatPercent: formatPercent, formatUptime: formatUptime, sparkPoints: sparkPoints, seriesValue: seriesValue, tileText: tileText };
    return;
  }

  function startServerPage(root) {
    var intervalSelect = root.querySelector("[data-server-interval]");
    var rangeSelect = root.querySelector("[data-server-range]");
    var pause = root.querySelector("[data-server-pause]");
    var status = root.querySelector("[data-server-status]");
    var uptime = document.querySelector("[data-uptime]");
    var viewer = Math.random().toString(36).slice(2) + Date.now().toString(36);
    var live = [];
    var history = null;
    var after = 0;
    var paused = false;
    var timer = null;
    var historyTimer = null;

    try { var stored = localStorage.getItem("sbox-ns-server-interval"); if (stored && intervalSelect.querySelector('option[value="' + stored + '"]')) intervalSelect.value = stored; } catch (e) { /* storage blocked */ }
    pause.hidden = false;

    function setStatus(text) { status.textContent = text; }

    function draw() {
      var points = rangeSelect.value === "live" ? live : history || [];
      root.querySelectorAll("[data-spark]").forEach(function (svg) {
        var key = svg.getAttribute("data-spark");
        svg.querySelector("polyline").setAttribute("points", sparkPoints(points.map(function (p) { return seriesValue(p, key); }), 120, 32));
      });
    }

    function render(data) {
      if (data.samples.length > 0) {
        live = live.concat(data.samples);
        after = data.samples[data.samples.length - 1].seq;
        var cutoff = live[live.length - 1].at - LiveWindowMs;
        while (live.length > 0 && live[0].at < cutoff) live.shift();
        var latest = live[live.length - 1];
        root.querySelectorAll("[data-metric]").forEach(function (dd) {
          var text = tileText(latest, dd.getAttribute("data-metric"));
          dd.textContent = text;
          dd.classList.toggle("muted-value", text === "Unavailable" || text === "No requests");
        });
      }
      var slow = data.slow;
      var set = function (key, text) { var el = document.querySelector('[data-slow="' + key + '"]'); if (el) el.textContent = text; };
      set("dataFree", slow.dataFree !== null && slow.dataTotal ? formatBytes(slow.dataFree) + " of " + formatBytes(slow.dataTotal) : "Unavailable");
      set("dataSize", slow.dataSize === null ? "Measuring" : formatBytes(slow.dataSize));
      set("backupSize", slow.backupSize === null ? "Measuring" : formatBytes(slow.backupSize));
      set("newestBackup", slow.newestBackup === null ? "None" : new Date(slow.newestBackup).toISOString().slice(0, 16).replace("T", " ") + " UTC");
      if (uptime) uptime.textContent = formatUptime(data.uptimeSeconds);
      draw();
    }

    function schedule() {
      clearTimeout(timer);
      if (paused || document.hidden) return;
      timer = setTimeout(poll, Number(intervalSelect.value));
    }

    function poll() {
      var interval = Number(intervalSelect.value);
      fetch("/dashboard/server/stats?viewer=" + encodeURIComponent(viewer) + "&interval=" + interval + "&after=" + after,
        { credentials: "same-origin", headers: { Accept: "application/json" } })
        .then(function (response) {
          if (response.status === 401 || response.redirected) throw new Error("Your session ended. Log in again to see live values.");
          if (!response.ok) throw new Error("The server answered HTTP " + response.status + ". Retrying.");
          return response.json();
        })
        .then(function (data) {
          render(data);
          setStatus("Updating every " + (data.interval >= 1000 ? data.interval / 1000 + " s" : data.interval + " ms") + ".");
          schedule();
        })
        .catch(function (error) {
          setStatus(error.message || "Live update failed. Retrying.");
          if (!/session/.test(error.message)) { clearTimeout(timer); timer = setTimeout(poll, 5000); }
        });
    }

    function loadHistory() {
      clearTimeout(historyTimer);
      var range = rangeSelect.value;
      if (range === "live") { history = null; draw(); return; }
      fetch("/dashboard/server/history?range=" + range, { credentials: "same-origin", headers: { Accept: "application/json" } })
        .then(function (response) { if (!response.ok) throw new Error(); return response.json(); })
        .then(function (data) { if (rangeSelect.value === range) { history = data.samples || data.rollups; draw(); } })
        .catch(function () { setStatus("Could not load the chart history."); })
        .finally(function () { if (rangeSelect.value === range) historyTimer = setTimeout(loadHistory, range === "24h" ? 60000 : 15000); });
    }

    intervalSelect.addEventListener("change", function () {
      try { localStorage.setItem("sbox-ns-server-interval", intervalSelect.value); } catch (e) { /* storage blocked */ }
      poll();
    });
    rangeSelect.addEventListener("change", loadHistory);
    pause.addEventListener("click", function () {
      paused = !paused;
      pause.setAttribute("aria-pressed", String(paused));
      pause.textContent = paused ? "Resume" : "Pause";
      if (paused) { clearTimeout(timer); setStatus("Paused. The server stops sampling a few seconds after the last update."); } else poll();
    });
    document.addEventListener("visibilitychange", function () { if (!document.hidden && !paused) poll(); });
    poll();
  }

  function startAlertsPage() {
    var kind = document.querySelector("[data-channel-kind]");
    if (kind) {
      var apply = function () {
        document.querySelectorAll("[data-channel-form] [data-channel-section]").forEach(function (section) {
          section.hidden = section.getAttribute("data-channel-section").split(" ").indexOf(kind.value) < 0;
        });
      };
      kind.addEventListener("change", apply);
      apply();
    }
    var metric = document.querySelector("[data-rule-metric]");
    if (metric) {
      var applyMetric = function () {
        var option = metric.options[metric.selectedIndex];
        var isEvent = option && option.getAttribute("data-kind") === "event";
        document.querySelectorAll("[data-rule-threshold]").forEach(function (field) { field.hidden = isEvent; });
      };
      metric.addEventListener("change", applyMetric);
      applyMetric();
    }
  }

  document.addEventListener("DOMContentLoaded", function () {
    var root = document.querySelector("[data-server-live]");
    if (root) startServerPage(root);
    startAlertsPage();
  });
})();
