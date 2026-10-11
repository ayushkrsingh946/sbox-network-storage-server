"use strict";
// Regression guards for adding keys to a map that is a sequence item
// (wwwroot/js/owner-definition-model.js). The new key must align with the
// item content column (the first key after `- `), for indented and
// indentless sequences, and the splice must leave surrounding comments and
// sibling items byte-for-byte intact. Load the vendored YAML bundle first:
// requiring it sets globalThis.OwnerYaml as a side effect.
require("../../src/SboxNetworkStorage.Server/wwwroot/vendor/yaml-2.8.2.js");

const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const modelApi = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-definition-model.js");

function lines(...rows) {
  return rows.join("\n") + "\n";
}

describe("add key to a sequence-item map", () => {
  it("keeps an indented multi-pair item and its neighbours intact", () => {
    const before = lines(
      "sourceVersion: 1",
      "kind: query",
      "sources:",
      "  # keep me",
      "  - collectionId: player_stats",
      "    alias: p",
      "  - collectionId: other",
      "    alias: o",
      "config:",
      "  limit: 20"
    );
    const model = modelApi.parse(before);
    model.add(["sources", 0], "weight", 1);
    const after = model.stringify();
    assert.equal(
      after,
      lines(
        "sourceVersion: 1",
        "kind: query",
        "sources:",
        "  # keep me",
        "  - collectionId: player_stats",
        "    alias: p",
        "    weight: 1",
        "  - collectionId: other",
        "    alias: o",
        "config:",
        "  limit: 20"
      )
    );
    modelApi.parse(after);
    assert.ok(model.has(["sources", 0, "weight"]));
  });

  it("aligns a single-pair item with the key after the dash", () => {
    const before = lines(
      "sources:",
      "  - collectionId: player_stats",
      "config:",
      "  limit: 20"
    );
    const model = modelApi.parse(before);
    model.add(["sources", 0], "weight", 1);
    const after = model.stringify();
    assert.equal(
      after,
      lines(
        "sources:",
        "  - collectionId: player_stats",
        "    weight: 1",
        "config:",
        "  limit: 20"
      )
    );
    modelApi.parse(after);
    assert.ok(model.has(["sources", 0, "weight"]));
  });

  it("uses the content column for an indentless sequence", () => {
    const before = lines(
      "sources:",
      "- collectionId: player_stats",
      "config:",
      "  limit: 20"
    );
    const model = modelApi.parse(before);
    model.add(["sources", 0], "weight", 1);
    const after = model.stringify();
    assert.equal(
      after,
      lines(
        "sources:",
        "- collectionId: player_stats",
        "  weight: 1",
        "config:",
        "  limit: 20"
      )
    );
    modelApi.parse(after);
    assert.ok(model.has(["sources", 0, "weight"]));
  });

  it("fills a bare dash item on the item line", () => {
    const before = lines("sources:", "  -", "config:", "  limit: 20");
    const model = modelApi.parse(before);
    model.add(["sources", 0], "weight", 1);
    const after = model.stringify();
    assert.equal(after, lines("sources:", "  - weight: 1", "config:", "  limit: 20"));
    modelApi.parse(after);
    assert.ok(model.has(["sources", 0, "weight"]));
  });
});
