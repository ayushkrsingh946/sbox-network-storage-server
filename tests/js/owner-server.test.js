"use strict";
// Guards for the Server page helpers (wwwroot/js/owner-server.js): sparkline scaling and the
// tile text, where a missing value must read "Unavailable", never 0.
const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const server = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-server.js");

describe("sparkPoints", () => {
  it("scales the series into the box with the maximum at the top", () => {
    assert.equal(server.sparkPoints([0, 50, 100], 120, 32), "0,32 60,16 120,0");
  });

  it("skips missing values instead of drawing them as zero", () => {
    assert.equal(server.sparkPoints([10, null, 30, undefined], 90, 30), "0,30 60,0");
  });

  it("keeps a flat zero series on the bottom edge and other flat series in the middle", () => {
    assert.equal(server.sparkPoints([0, 0], 10, 20), "0,20 10,20");
    assert.equal(server.sparkPoints([7, 7], 10, 20), "0,10 10,10");
    assert.equal(server.sparkPoints([], 10, 20), "");
  });
});

describe("tileText", () => {
  const sample = { cpu: null, processMemory: 1572864, memoryUsed: 2147483648, memoryTotal: 8589934592, netRx: null, errorRate: null, p95: 13.79, requestsPerSecond: 2.5 };

  it("says Unavailable or No requests for values the platform or traffic did not provide", () => {
    assert.equal(server.tileText(sample, "cpu"), "Unavailable");
    assert.equal(server.tileText(sample, "netRx"), "Unavailable");
    assert.equal(server.tileText(sample, "errorRate"), "No requests");
  });

  it("formats bytes, memory share and latency", () => {
    assert.equal(server.tileText(sample, "processMemory"), "1.5 MB");
    assert.equal(server.tileText(sample, "memory"), "25.0 % of 8.0 GB");
    assert.equal(server.tileText(sample, "p95"), "13.8 ms");
  });

  it("reads minute rollups for the 24-hour chart", () => {
    assert.equal(server.seriesValue({ requests: 120, errors: 6 }, "errorRate"), 5);
    assert.equal(server.seriesValue({ requests: 120, errors: 0 }, "rps"), 2);
    assert.equal(server.seriesValue({ memoryPercent: 40 }, "memory"), 40);
  });
});
