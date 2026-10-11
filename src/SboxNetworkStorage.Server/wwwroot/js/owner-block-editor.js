"use strict";
(function () {
  var host = document.getElementById("block-editor"), source = document.getElementById("definition");
  if (!host || !source || !window.OwnerDefinitionModel) return;
  var kind = host.dataset.kind, context = JSON.parse(host.dataset.context || "{}");
  var model, parsedSource, applying = false, expanded = new Set(), fieldId = 0, drag;
  var status = document.getElementById("block-editor-status"), catalog, catalogError;
  var families = {
    Data: ["read", "write", "delete", "lookup", "filter", "lookup_many", "random_select"],
    Logic: ["condition", "assert"], Values: ["transform", "object", "array", "merge", "sort", "switch", "compute"],
    Random: ["random"], Flow: ["block", "workflow", "sleep", "response"], Integrations: ["webhook"]
  };
  var descriptions = {
    read: "Load a record by key", write: "Apply operations to a record", delete: "Delete a record",
    lookup: "Find the first matching row", filter: "Find all matching rows", lookup_many: "Look up several keys",
    random_select: "Pick a weighted matching row", condition: "Continue or route on a check", assert: "Reject if a check fails",
    transform: "Evaluate a value or expression", object: "Build named fields", array: "Build a list of values",
    merge: "Combine values", sort: "Order a list", switch: "Choose a value by matching cases", compute: "Compute named values",
    random: "Generate a random value", block: "Group conditional steps", workflow: "Call a saved workflow",
    sleep: "Pause the pipeline", response: "Finish with the endpoint response", webhook: "Send a webhook message"
  };
  var labels = { collectionType: "Storage", accessMode: "Game client access", allowRecordDelete: "Allow client deletes", sourceVersion: "Source version", ttlSeconds: "Cache lifetime (seconds)", requiresSecretKey: "Require secret key", errorCode: "Error code", collectionId: "Collection", maxRecords: "Maximum records", keyField: "Key field", weightField: "Weight field", asMap: "Return as a map", onFail: "On failure", minLength: "Minimum length", maxLength: "Maximum length" };
  function el(tag, text, cls) { var n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n; }
  function button(text, action, cls) { var n = el("button", text, cls || "secondary"); n.type = "button"; n.addEventListener("click", function (event) { if (host.contains(n) && !flush()) return; action(event); }); return n; }
  function confirmDestructive(options) {
    if (window.OwnerDialog && typeof window.OwnerDialog.confirm === "function") return window.OwnerDialog.confirm(options);
    return Promise.resolve(false);
  }
  function label(key) { return labels[key] || String(key).replace(/([a-z])([A-Z])/g, "$1 $2").replace(/_/g, " ").replace(/^./, function (c) { return c.toUpperCase(); }); }
  function key(path) { return JSON.stringify(path); }
  function exists(path) { return model && model.has(path); }
  function value(path) { try { return model.scalar(path); } catch (_) { return undefined; } }
  function text(path) { try { return model.text(path); } catch (_) { return ""; } }
  function entries(path) { try { return model.entries(path); } catch (_) { return []; } }
  function message(s, error) { status.textContent = s; status.className = error ? "notice error" : "muted"; }
  function invalid() { return host.querySelector(":invalid"); }
  function flush() {
    Array.from(host.querySelectorAll("input,textarea,select")).filter(function (control) { return control._blockPending; }).forEach(function (control) { control.dispatchEvent(new Event("change")); });
    var field = invalid(); if (field) field.reportValidity(); return !field;
  }
  function mutate(action, redraw) {
    try {
      action(); parsedSource = model.stringify(); applying = true; source.value = parsedSource;
      source.dispatchEvent(new Event("input", { bubbles: true })); applying = false;
      message("Local changes. Save live definition to publish.");
      if (redraw !== false) render(); else refreshSummaries();
      return true;
    } catch (error) { applying = false; message(error.message, true); return false; }
  }
  function load(force) {
    if (!force && parsedSource === source.value && model) return true;
    try { model = window.OwnerDefinitionModel.parse(source.value); parsedSource = source.value; message(""); render(); return true; }
    catch (error) {
      model = undefined; parsedSource = undefined; host.replaceChildren();
      var box = el("div", undefined, "block-empty"); box.append(el("h3", "This source needs Raw editing"), el("p", error.message, "muted"), button("Open Raw source", openRaw)); host.append(box);
      message("Your source is unchanged. Correct the syntax in Raw, then return to Guided editor.", true); return false;
    }
  }
  function openRaw() { document.getElementById("raw-tab").click(); }
  function unique(base, used) { var name = base, i = 2; while (used.includes(name)) name = base + "_" + i++; return name; }
  function ids(path) { return entries(path).map(function (item) { return String(value(path.concat(item.key, "id")) || ""); }); }
  function title(path, fallback) {
    return String(value(path.concat("id")) || value(path.concat("key")) || value(path.concat("name")) || fallback);
  }
  function summary(path) {
    var nodeKind = model.kind(path), count = entries(path).length;
    if (nodeKind === "alias") return "YAML alias. Edit its source in Raw.";
    if (nodeKind === "scalar" || nodeKind === "null") return text(path).slice(0, 110);
    if (nodeKind === "seq") return count + (count === 1 ? " item" : " items");
    var type = value(path.concat("type")), collection = value(path.concat("collection")) || value(path.concat("collectionId")), table = value(path.concat("table"));
    if (descriptions[type]) {
      var target = collection || table || value(path.concat("workflow")) || value(path.concat("expression"));
      return descriptions[type] + (target ? ": " + target : "");
    }
    if (type === "group") return entries(path.concat("entries")).length + " values";
    if (type === "table") return entries(path.concat("rows")).length + " rows · " + entries(path.concat("columns")).length + " columns";
    if (type) return String(type);
    if (value(path.concat("method"))) return value(path.concat("method")) + " · " + (value(path.concat("enabled")) === false ? "Disabled" : "Enabled");
    return count + (count === 1 ? " option" : " options");
  }
  function refreshSummaries() {
    host.querySelectorAll("[data-block-path]").forEach(function (row) {
      var path = JSON.parse(row.dataset.blockPath);
      if (!exists(path)) return;
      row.querySelector(".block-summary").textContent = summary(path);
      if (row.dataset.blockDynamicTitle) row.querySelector(".block-title").textContent = title(path, row.dataset.blockDynamicTitle);
    });
  }
  function remap(parent, mapIndex) {
    var next = new Set(); expanded.forEach(function (encoded) {
      var path = JSON.parse(encoded), prefix = path.slice(0, parent.length);
      if (key(prefix) === key(parent) && typeof path[parent.length] === "number") {
        var mapped = mapIndex(path[parent.length]); if (mapped === null) return; path[parent.length] = mapped;
      }
      next.add(key(path));
    }); expanded = next;
  }
  function move(parent, from, to) {
    var count = entries(parent).length; if (to < 0 || to >= count || from === to) return;
    if (mutate(function () { model.move(parent, from, to); }, false)) {
      if (model.kind(parent) === "seq") remap(parent, function (i) { return i === from ? to : from < to && i > from && i <= to ? i - 1 : from > to && i >= to && i < from ? i + 1 : i; });
      render(); focusBlock(parent.concat(entries(parent)[to].key)); message("Moved to position " + (to + 1) + ". Save to publish.");
    }
  }
  function focusBlock(path) { var row = Array.from(host.querySelectorAll("[data-block-path]")).find(function (n) { return n.dataset.blockPath === key(path); }); if (row) { row.querySelector(".block-toggle").focus(); row.scrollIntoView({ block: "nearest" }); } }
  function copyBlock(path) {
    var node = model.node(path), full = model.stringify(), start = node.range[0], end = node.range[1];
    var indent = start - (full.lastIndexOf("\n", start - 1) + 1), lines = full.slice(start, end).trimEnd().split("\n");
    var copied = lines.map(function (line, i) { return i && line.startsWith(" ".repeat(indent)) ? line.slice(indent) : line; }).join("\n") + "\n";
    function fallback() {
      var d = modal("Copy block source"), pre = el("pre", copied, "block-copy-source"); pre.dataset.language = "yaml"; d.content.append(pre, el("p", "Source selected. Press Ctrl / ⌘ + C.", "muted"));
      d.submit.hidden = true; d.show(); document.dispatchEvent(new CustomEvent("owner:highlight", { detail: pre }));
      var range = document.createRange(); range.selectNodeContents(pre); var selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
    }
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(copied).then(function () { message("Block source copied."); }, fallback); else fallback();
  }
  function block(parent, path, heading, opts) {
    opts = opts || {}; var row = el("div", undefined, "logic-block"), header = el("div", undefined, "block-header"), main = el("div", undefined, "block-header-main"), body = el("div", undefined, "block-body");
    row.dataset.blockPath = key(path); if (opts.dynamic) row.dataset.blockDynamicTitle = heading;
    var toggle = button("", function () { if (expanded.has(key(path))) expanded.delete(key(path)); else expanded.add(key(path)); render(); focusBlock(path); }, "block-toggle");
    toggle.setAttribute("aria-expanded", String(expanded.has(key(path)))); body.id = "block-body-" + (++fieldId); toggle.setAttribute("aria-controls", body.id);
    toggle.append(el("span", expanded.has(key(path)) ? "⌄" : "›", "block-chevron"), el("strong", opts.dynamic ? title(path, heading) : heading, "block-title"));
    var type = value(path.concat("type")); if (type && descriptions[type]) toggle.append(el("span", label(type), "block-type"));
    toggle.append(el("span", summary(path), "block-summary")); main.append(toggle); header.append(main);
    if (opts.reorder) {
      var index = opts.index, siblings = entries(opts.parent), handle = button("⠿", function () {}, "secondary block-handle");
      handle.draggable = true; handle.title = "Drag to reorder. Alt + arrow up or down also moves this block."; handle.setAttribute("aria-label", "Reorder " + heading);
      main.prepend(el("span", String(index + 1), "block-index"), handle);
      handle.addEventListener("keydown", function (event) { if (event.altKey && (event.key === "ArrowUp" || event.key === "ArrowDown")) { event.preventDefault(); move(opts.parent, index, index + (event.key === "ArrowUp" ? -1 : 1)); } });
      handle.addEventListener("dragstart", function (event) { drag = { parent: opts.parent, index: index }; event.dataTransfer.effectAllowed = "move"; event.dataTransfer.setData("text/plain", key(drag)); row.classList.add("block-dragging"); });
      handle.addEventListener("dragend", clearDrag);
      row.addEventListener("dragover", function (event) {
        if (!drag || key(drag.parent) !== key(opts.parent) || event.target.closest(".logic-block") !== row) return;
        event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = "move";
        host.querySelectorAll(".block-drop-before,.block-drop-after").forEach(function (n) { n.classList.remove("block-drop-before", "block-drop-after"); });
        row.classList.add(event.clientY < header.getBoundingClientRect().top + header.offsetHeight / 2 ? "block-drop-before" : "block-drop-after");
      });
      row.addEventListener("drop", function (event) {
        if (!drag || key(drag.parent) !== key(opts.parent) || event.target.closest(".logic-block") !== row) return;
        event.preventDefault(); event.stopPropagation(); var destination = index + (row.classList.contains("block-drop-after") ? 1 : 0), from = drag.index;
        if (from < destination) destination--; clearDrag(); move(opts.parent, from, destination);
      });
      var actions = el("div", undefined, "block-row-actions"), up = button("↑", function () { move(opts.parent, index, index - 1); }), down = button("↓", function () { move(opts.parent, index, index + 1); });
      up.disabled = index === 0; down.disabled = index === siblings.length - 1; up.setAttribute("aria-label", "Move " + heading + " up"); down.setAttribute("aria-label", "Move " + heading + " down");
      actions.append(up, down, button("Copy", function () { copyBlock(path); }));
      if (opts.mutable !== false) {
        actions.append(button("Duplicate", function () {
          var newName = unique(String(value(path.concat("id")) || path[path.length - 1]) + "_copy", model.kind(opts.parent) === "seq" ? ids(opts.parent) : entries(opts.parent).map(function (e) { return String(e.key); }));
          if (mutate(function () { model.duplicate(opts.parent, index, newName); }, false)) { if (model.kind(opts.parent) === "seq") remap(opts.parent, function (i) { return i > index ? i + 1 : i; }); render(); }
        }), button("Remove", function (event) {
          var opener = event && event.currentTarget ? event.currentTarget : null;
          confirmDestructive({ title: "Remove " + heading + "?", message: "This stays local until you save.", confirmLabel: "Remove", danger: true, opener: opener }).then(function (confirmed) {
            if (!confirmed) return;
            if (mutate(function () { model.remove(path); }, false)) { if (model.kind(opts.parent) === "seq") remap(opts.parent, function (i) { return i === index ? null : i > index ? i - 1 : i; }); render(); }
          });
        }, "secondary danger"));
      }
      header.append(actions);
    }
    row.append(header, body); body.hidden = !expanded.has(key(path)); parent.append(row);
    if (!body.hidden) {
      if (model.kind(path) === "alias") body.append(el("p", "This is a YAML alias. Editing it here could also change another block. Use Raw to change the anchor or reference.", "muted"), button("Edit in Raw", openRaw));
      else if (opts.content) opts.content(body);
      else renderNode(body, path);
    }
    return row;
  }
  function clearDrag() { drag = undefined; host.querySelectorAll(".block-dragging,.block-drop-before,.block-drop-after").forEach(function (n) { n.classList.remove("block-dragging", "block-drop-before", "block-drop-after"); }); }
  function choices(path) {
    var name = path[path.length - 1], parent = path.slice(0, -1), current = String(value(path) ?? ""), stepType = value(parent.concat("type"));
    if (name === "method") return [["GET", "GET"], ["POST", "POST"]];
    if (name === "collectionType") return [["per-steamid", "Per player"], ["global", "Global"]];
    if (name === "accessMode") return [["endpoint", "Endpoints only"], ["public", "Direct game client access"]];
    if (name === "type" && parent.length && (parent[parent.length - 2] === "steps")) return Object.keys(descriptions).map(function (t) { return [t, label(t)]; });
    if (name === "type" && path.includes("schema") || name === "type" && path.includes("params") || name === "type" && path.includes("columns")) return ["number", "string", "boolean", "array", "object"].map(function (t) { return [t, label(t)]; });
    if (name === "type" && kind === "query" && parent.length === 0) return ["leaderboard", "count", "sum", "average", "min", "max"].map(function (t) { return [t, label(t)]; });
    if (name === "collection" || name === "collectionId") return (context.collections || []).map(function (c) { return [c.id, c.id]; });
    if (name === "workflow") return (context.workflows || []).map(function (w) { return [w.id, w.id]; });
    if (name === "table") return (context.tables || []).map(function (t) { return [t.id, t.id]; });
    if (name === "order" || name === "direction") return [["asc", "Ascending"], ["desc", "Descending"]];
    if (name === "op") return (path.includes("ops") ? ["set", "inc", "push", "pull", "remove", "delete", "merge", "set_if_null"] : ["==", "!=", ">", ">=", "<", "<=", "contains", "not_contains", "in", "not_in", "starts_with", "not_starts_with", "matches", "exists", "not_exists"]).map(function (t) { return [t, t]; });
    if (name === "source" && ["lookup", "filter", "lookup_many", "random_select"].includes(stepType)) return [["collection", "Collection"], ["values", "Game value table"]];
    if (name === "mode" && stepType === "random") return [["int", "Integer"], ["float", "Decimal"]];
    if (name === "exposure") return [["public", "Game callable"], ["internal", "Internal only"]];
    if (name === "action") return ["continue", "return", "reject", "goto", "skip", "run"].map(function (t) { return [t, label(t)]; });
    return null;
  }
  function select(options, selected) {
    var n = el("select"); options.forEach(function (o) { var option = el("option", o[1]); option.value = o[0]; n.append(option); }); n.value = selected; return n;
  }
  function scalarType(path) { var v = value(path); return v === null ? "null" : typeof v === "boolean" ? "boolean" : typeof v === "number" || typeof v === "bigint" ? "number" : "string"; }
  function references(path) {
    var refs = ["steamId", "now"], input = entries(["input", "properties"]); input.forEach(function (e) { refs.push("input." + e.key); });
    entries(["params"]).forEach(function (e) { refs.push(String(e.key)); });
    var stepAt = path.indexOf("steps"), stepPath = stepAt >= 0 ? path.slice(0, stepAt + 1) : ["steps"], index = stepAt >= 0 ? path[stepAt + 1] : Infinity;
    entries(stepPath).forEach(function (e) {
      if (e.key >= index) return; var p = stepPath.concat(e.key), id = value(p.concat("id")); if (!id) return; refs.push(String(id));
      var collection = (context.collections || []).find(function (c) { return c.id === value(p.concat("collection")); });
      (collection ? collection.fields : entries(p.concat("fields")).map(function (f) { return String(f.key); })).forEach(function (field) { refs.push(id + "." + field); });
    });
    (context.groups || []).forEach(function (g) { (g.keys || []).forEach(function (k) { refs.push("values." + g.id + "." + k); }); });
    return Array.from(new Set(refs));
  }
  function field(parent, path, heading, allowRemove) {
    var row = el("div", undefined, "block-field"), fieldLabel = el("label", heading), type = scalarType(path), current = type === "string" ? String(value(path) ?? "") : text(path), options = choices(path), control;
    var fixedId = path.length === 1 && path[0] === "id" && document.querySelector("#resource-definition-form input[name=id]");
    if (options && type === "string") {
      if (!options.some(function (o) { return o[0] === current; })) options.unshift([current, current || "Choose..."]);
      control = select(options, current);
    } else if (type === "boolean") control = select([["true", "On"], ["false", "Off"]], String(value(path)));
    else if (type === "null") { control = el("input"); control.value = "null"; control.readOnly = true; }
    else { control = el(type === "string" && current.includes("\n") ? "textarea" : "input"); control.value = current; if (control.tagName === "TEXTAREA") control.rows = 3; if (type === "number") control.inputMode = "decimal"; }
    control.id = "block-field-" + (++fieldId); fieldLabel.htmlFor = control.id; control.dataset.fieldPath = key(path); control.setAttribute("aria-label", heading); row.append(fieldLabel);
    var controls = el("div", undefined, "block-field-controls"); controls.append(control);
    if (fixedId) { control.readOnly = true; control.title = "Saved ids cannot change. Create a new resource to use a different id."; }
    var typeSelect;
    if (!options && !fixedId && !["id", "kind", "sourceVersion"].includes(String(path[path.length - 1]))) {
      typeSelect = select([["string", "Text"], ["number", "Number"], ["boolean", "Boolean"], ["null", "Null"]], type); typeSelect.className = "block-value-type"; typeSelect.setAttribute("aria-label", heading + " value type"); controls.append(typeSelect);
      typeSelect.addEventListener("change", function () {
        var next = typeSelect.value, initial = next === "boolean" ? "false" : next === "null" ? "null" : next === "number" ? (/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/i.test(control.value) ? control.value : "0") : control.value;
        if (mutate(function () { model.setScalar(path, next, initial); })) expanded.add(key(path.slice(0, -1)));
      });
    }
    function apply() {
      control.setCustomValidity("");
      var stepTypeChange = path[path.length - 1] === "type" && path.includes("steps") && catalog && catalog.stepDefaults[control.value];
      var applied = mutate(function () {
        if (stepTypeChange) {
          var parentPath = path.slice(0, -1), patch = { type: control.value };
          Object.entries(stepTypeChange).forEach(function (p) { if (p[0] !== "id" && !exists(parentPath.concat(p[0]))) patch[p[0]] = p[1]; });
          model.set(parentPath, patch);
        } else model.setScalar(path, type, control.value);
      }, false);
      control._blockPending = !applied;
      if (!applied) { control.setCustomValidity(status.textContent); control.reportValidity(); }
      else if (path[path.length - 1] === "type" || path[path.length - 1] === "source") render();
    }
    control.addEventListener("change", apply); control.addEventListener("input", function () { control._blockPending = true; control.setCustomValidity(""); });
    var name = String(path[path.length - 1]);
    if (type === "string" && !options && !fixedId && !["id", "kind", "name", "url", "slug", "keyField", "weightField", "errorCode", "error"].includes(name)) {
      var insert = select([["", "Insert reference..."]].concat(references(path).map(function (r) { return [r, r]; })), ""); insert.className = "block-reference"; insert.setAttribute("aria-label", "Insert reference into " + heading);
      insert.addEventListener("change", function () {
        if (!insert.value) return; var token = name === "field" || name === "path" ? insert.value : "{{" + insert.value + "}}";
        control.setRangeText(token, control.selectionStart || 0, control.selectionEnd || 0, "end"); apply(); insert.value = ""; control.focus();
      }); controls.append(insert);
      if (name === "field" || name === "path" || name === "by") {
        var list = el("datalist"); list.id = "block-suggestions-" + fieldId;
        var fields = Array.from(new Set((context.collections || []).flatMap(function (c) { return c.fields || []; }))); fields.forEach(function (f) { var option = el("option"); option.value = f; list.append(option); }); control.setAttribute("list", list.id); controls.append(list);
      }
    }
    if (["entries", "fields", "values", "returns"].includes(String(path[path.length - 2]))) controls.append(button("Rename", function () { rename(path); }));
    if (allowRemove) controls.append(button("Remove", function (event) {
      var opener = event && event.currentTarget ? event.currentTarget : null;
      confirmDestructive({ title: "Remove " + heading + "?", message: "This stays local until you save.", confirmLabel: "Remove", danger: true, opener: opener }).then(function (confirmed) {
        if (confirmed) mutate(function () { model.remove(path); });
      });
    }, "secondary block-field-remove"));
    row.append(controls); parent.append(row);
    if (name === "accessMode") parent.append(el("p", "Endpoints only blocks direct game reads and writes. Direct access also requires a public key; deletes additionally require Allow client deletes.", "muted block-field-help"));
    if (name === "requiresSecretKey") parent.append(el("p", "Dedicated servers only: when on, the endpoint answers 401 SECRET_KEY_REQUIRED without a secret key. Player-hosted games cannot call such endpoints.", "muted block-field-help"));
  }
  function knownOptions(path) {
    var result = {}, last = path[path.length - 1], type = value(path.concat("type"));
    if (path.length === 0) {
      if (kind === "collection") result = { name: "", collectionType: "per-steamid", accessMode: "endpoint", maxRecords: 1, allowRecordDelete: false };
      if (kind === "endpoint") result = { name: "", method: "POST", enabled: true, requiresSecretKey: false, exposure: "public", input: { type: "object", properties: {} }, response: { status: 200, body: {} } };
      if (kind === "workflow") result = { name: "", params: {}, returns: {} };
      if (kind === "query") result = { name: "", type: "leaderboard", sources: [], config: {}, cache: { ttlSeconds: 60 } };
    }
    if (descriptions[type] && catalog && catalog.stepDefaults[type]) result = Object.assign({}, catalog.stepDefaults[type]);
    if (path.includes("schema") || path.includes("input") || path.includes("params")) {
      if (["number", "string", "boolean", "array", "object"].includes(type)) {
        result.default = type === "number" ? 0 : type === "boolean" ? false : type === "array" ? [] : type === "object" ? {} : "";
        if (type === "number") { result.min = 0; result.max = 100; }
        if (type === "string") { result.minLength = 0; result.maxLength = 128; }
        if (type === "object") { result.properties = {}; result.required = []; }
        if (type === "array") result.items = { type: "string" };
      }
    }
    if (last === "response") result = { status: 200, body: {} };
    if (last === "config") result = { field: "", order: "desc", limit: 20 };
    if (last === "cache") result = { ttlSeconds: 60 };
    if (last === "check" || last === "when" || last === "where") result = { field: "", op: "==", value: "", all: [], any: [] };
    if (type === "group") result = { name: "", entries: {} };
    if (type === "table") result = { name: "", columns: [], rows: [] };
    if (last === "routes") result = { true: { action: "continue" }, false: { action: "reject", status: 400, error: "", message: "" } };
    delete result.id; delete result.type;
    return Object.entries(result).filter(function (p) { return !exists(path.concat(p[0])); });
  }
  function renderNode(parent, path, opts) {
    opts = opts || {}; var nodeKind = model.kind(path);
    if (nodeKind === "scalar" || nodeKind === "null") { field(parent, path, opts.heading || "Value", false); return; }
    if (nodeKind === "alias") { parent.append(el("p", "Edit this YAML alias in Raw.", "muted"), button("Open Raw source", openRaw)); return; }
    if (nodeKind === "seq") { list(parent, path); return; }
    if (["properties", "params"].includes(String(path[path.length - 1])) && !opts.omit) { list(parent, path, { fields: true }); return; }
    var fields = el("div", undefined, "block-fields"); parent.append(fields);
    entries(path).filter(function (e) { return !(opts.omit || []).includes(String(e.key)); }).forEach(function (entry) {
      var p = path.concat(entry.key), childKind = model.kind(p), heading = label(entry.key);
      if (childKind === "scalar" || childKind === "null") field(fields, p, heading, !["id", "kind", "sourceVersion", "type"].includes(String(entry.key)));
      else block(fields, p, heading, { content: function (body) {
        renderNode(body, p);
        body.append(button("Remove option", function (event) {
          var opener = event && event.currentTarget ? event.currentTarget : null;
          confirmDestructive({ title: "Remove " + heading + "?", message: "This removes the option and its contents. It stays local until you save.", confirmLabel: "Remove", danger: true, opener: opener }).then(function (confirmed) {
            if (confirmed) mutate(function () { model.remove(p); });
          });
        }, "secondary danger"));
      } });
    });
    if (!fields.children.length) fields.append(el("p", "No options yet. Add one below.", "muted"));
    var actions = el("div", undefined, "block-option-actions");
    if (opts.rename && model.kind(path.slice(0, -1)) === "map") actions.append(button("Rename field", function () { rename(path); }));
    actions.append(button("Add option", function () { addDialog(path); })); parent.append(actions);
  }
  function list(parent, path, opts) {
    opts = opts || {}; var rows = entries(path), container = el("div", undefined, "block-list"); parent.append(container);
    rows.forEach(function (entry, i) { block(container, path.concat(entry.key), opts.fields && typeof entry.key !== "number" ? String(entry.key) : title(path.concat(entry.key), (opts.fields ? "Field " : "Item ") + (i + 1)), { dynamic: !opts.fields, reorder: true, parent: path, index: i, content: opts.fields ? function (body) { renderNode(body, path.concat(entry.key), { rename: true }); } : undefined }); });
    if (!rows.length) container.append(el("p", "No " + (opts.fields ? "fields" : "items") + " yet. Add one to start.", "muted block-empty"));
    var name = path[path.length - 1], add = name === "steps" ? "Add step" : opts.fields ? "Add field" : name === "rows" ? "Add row" : name === "columns" ? "Add column" : name === "sources" ? "Add source" : name === "items" && kind === "game-values" ? "Add group or table" : "Add item";
    parent.append(button(add, function () { addDialog(path, opts.fields ? "schema" : undefined); }, "secondary block-add"));
  }
  function section(heading, path, opts) {
    opts = opts || {}; var section = el("section", undefined, "block-section"), headingRow = el("div", undefined, "block-section-heading"); headingRow.append(el("h3", heading));
    if (opts.sequence || opts.fields) headingRow.append(el("span", String(entries(path).length), "badge")); section.append(headingRow); host.append(section);
    if (!exists(path)) { section.append(el("p", "Not configured.", "muted"), button("Add " + heading.toLowerCase(), function () { if (mutate(function () { model.add([], path[0], opts.sequence ? [] : path[0] === "input" ? { type: "object", properties: {} } : {}); })) { expanded.add(key(path)); render(); } })); return; }
    if (opts.sequence || opts.fields) list(section, path, opts);
    else block(section, path, heading, { content: function (body) { renderNode(body, path, opts); } });
  }
  function render() {
    if (!model) return; host.replaceChildren(); fieldId = 0;
    var partitions = kind === "endpoint" ? ["steps", "input", "response"] : kind === "workflow" ? ["steps", "params", "returns"] : kind === "collection" ? ["schema", "constants", "tables"] : kind === "query" ? ["sources", "config", "cache"] : ["items", "groups", "tables"];
    var settings = el("section", undefined, "block-section"); host.append(settings);
    block(settings, [], kind === "game-values" ? "Other values and options" : "Settings", { content: function (body) { renderNode(body, [], { omit: partitions }); } });
    if (kind === "endpoint" || kind === "workflow") {
      section(kind === "endpoint" ? "Input fields" : "Parameters", kind === "endpoint" ? ["input"] : ["params"]);
      section("Pipeline", ["steps"], { sequence: true }); section(kind === "endpoint" ? "Response" : "Return values", kind === "endpoint" ? ["response"] : ["returns"]);
    } else if (kind === "collection") {
      var schema = el("section", undefined, "block-section"); schema.append(el("h3", "Schema fields")); host.append(schema);
      if (exists(["schema", "properties"])) list(schema, ["schema", "properties"], { fields: true });
      else if (exists(["schema"])) block(schema, ["schema"], "Schema", {});
      else schema.append(button("Add schema", function () { mutate(function () { model.add([], "schema", { type: "object", properties: {} }); }); }));
      if (exists(["schema", "properties"])) block(schema, ["schema"], "Validation options", { content: function (body) { renderNode(body, ["schema"], { omit: ["properties"] }); } });
      ["constants", "tables"].forEach(function (name) { if (exists([name])) section(label(name), [name], { sequence: true }); });
    } else if (kind === "query") { section("Sources", ["sources"], { sequence: true }); section("Query options", ["config"]); section("Cache", ["cache"]); }
    else {
      if (exists(["items"])) section("Groups and tables", ["items"], { sequence: true });
      else if (!exists(["groups"]) && !exists(["tables"])) host.append(button("Add value", function () { addDialog([]); }, "secondary block-add"));
      ["groups", "tables"].forEach(function (name) { if (exists([name])) section(label(name), [name], { sequence: true }); });
    }
  }
  function modal(heading) {
    var dialog = el("dialog", undefined, "block-dialog"), form = el("form"), title = el("h2", heading), content = el("div", undefined, "block-dialog-content"), error = el("p", "", "notice error"), actions = el("div", undefined, "dialog-actions"), cancel = button("Cancel", function () { dialog.close(); }), submit = el("button", "Add");
    title.id = "block-dialog-title"; dialog.setAttribute("aria-labelledby", title.id); submit.type = "submit"; error.hidden = true; error.setAttribute("role", "alert"); actions.append(cancel, submit); form.append(title, content, error, actions); dialog.append(form); document.body.append(dialog);
    dialog.addEventListener("close", function () { dialog.remove(); }); dialog.addEventListener("click", function (event) { if (event.target === dialog) dialog.close(); });
    return { dialog: dialog, form: form, content: content, error: error, submit: submit, show: function () { dialog.showModal(); var first = content.querySelector("input,select,button"); if (first) first.focus(); }, fail: function (s) { error.hidden = false; error.textContent = s; } };
  }
  function dialogField(parent, heading, control) { var wrapper = el("label", heading); wrapper.append(control); parent.append(wrapper); return control; }
  function newSchema(type) { return type === "object" ? { type: type, properties: {} } : type === "array" ? { type: type, items: { type: "string" } } : { type: type }; }
  function addDialog(path, mode) {
    if (invalid()) { invalid().reportValidity(); return; }
    var nodeKind = model.kind(path), name = path[path.length - 1], stepMode = name === "steps", gameMode = name === "items" && kind === "game-values" && path.length === 1, fieldMode = mode === "schema" || name === "properties" || name === "params";
    var d = modal(stepMode ? "Add pipeline step" : fieldMode ? "Add field" : gameMode ? "Add group or table" : "Add " + (nodeKind === "map" ? "option" : name === "rows" ? "row" : name === "columns" ? "column" : name === "sources" ? "source" : "item"));
    var chosen, preset, nameInput, typeInput, initialInput, fields = el("div", undefined, "block-dialog-fields");
    function config(type, initial, titleText) {
      chosen = type; preset = initial; fields.replaceChildren(); d.submit.disabled = false;
      if (nodeKind === "map" || stepMode || gameMode || fieldMode || name === "columns") {
        nameInput = el("input"); nameInput.required = true; nameInput.maxLength = 128;
        nameInput.value = unique(stepMode || gameMode ? type + "_1" : name === "columns" ? "column" : fieldMode ? "field" : "option", nodeKind === "map" ? entries(path).map(function (e) { return String(e.key); }) : ids(path));
        dialogField(fields, stepMode || gameMode ? "Id" : name === "columns" ? "Column key" : fieldMode ? "Field name" : "Option name", nameInput);
      }
      if (fieldMode || name === "columns") {
        typeInput = dialogField(fields, "Type", select(["number", "string", "boolean", "array", "object"].map(function (t) { return [t, label(t)]; }), "string"));
      } else if (!stepMode && !gameMode && (preset === undefined || preset === null || typeof preset !== "object")) {
        var initialType = preset === null ? "null" : typeof preset === "number" || typeof preset === "bigint" ? "number" : typeof preset === "boolean" ? "boolean" : "string";
        typeInput = dialogField(fields, "Value type", select([["string", "Text"], ["number", "Number"], ["boolean", "Boolean"], ["null", "Null"], ["object", "Object"], ["array", "List"]], initialType));
        initialInput = el("input"); initialInput.value = preset == null ? "" : String(preset); initialInput.disabled = initialType === "null"; dialogField(fields, "Value", initialInput);
        typeInput.addEventListener("change", function () { initialInput.disabled = ["object", "array", "null"].includes(typeInput.value); initialInput.value = typeInput.value === "number" ? "0" : typeInput.value === "boolean" ? "false" : ""; });
      }
      if (titleText) fields.prepend(el("p", titleText, "muted"));
    }
    if (stepMode) {
      d.submit.disabled = true; var search = el("input"); search.type = "search"; search.placeholder = "Find a step..."; search.setAttribute("aria-label", "Find a step"); var palette = el("div", undefined, "block-palette"); d.content.append(search, palette);
      function drawPalette() {
        palette.replaceChildren(); var term = search.value.toLowerCase();
        if (!catalog) { palette.append(el("p", catalogError || "Loading step catalog...", "muted"), button("Retry catalog", function () { document.dispatchEvent(new Event("owner:catalog-retry")); })); return; }
        Object.entries(families).forEach(function (group) {
          var matches = group[1].filter(function (t) { return catalog.stepDefaults[t] && (t + " " + descriptions[t]).includes(term); }); if (!matches.length) return;
          palette.append(el("h3", group[0])); matches.forEach(function (t) {
            var option = button("", function () { palette.querySelectorAll("button").forEach(function (b) { b.setAttribute("aria-pressed", String(b === option)); }); config(t, JSON.parse(JSON.stringify(catalog.stepDefaults[t])), descriptions[t]); nameInput.focus(); }, "block-palette-option"); option.setAttribute("aria-pressed", "false"); option.append(el("strong", label(t)), el("span", descriptions[t], "muted")); palette.append(option);
          });
        });
        if (!palette.children.length) palette.append(el("p", "No matching steps. Try another search.", "muted"));
      }
      search.addEventListener("input", drawPalette); var update = function () { if (d.dialog.open) drawPalette(); }; document.addEventListener("owner:catalog", update); document.addEventListener("owner:catalog-error", update); d.dialog.addEventListener("close", function () { document.removeEventListener("owner:catalog", update); document.removeEventListener("owner:catalog-error", update); }); drawPalette();
    } else if (gameMode) {
      var typePicker = dialogField(d.content, "Building block", select([["group", "Group of named values"], ["table", "Table with columns and rows"]], "group"));
      typePicker.addEventListener("change", function () { config(typePicker.value, typePicker.value === "group" ? { type: "group", name: "", entries: {} } : { type: "table", name: "", columns: [], rows: [] }); }); config("group", { type: "group", name: "", entries: {} });
    } else if (name === "sources") config("source", { collectionId: (context.collections || [])[0]?.id || "" });
    else if (name === "rows" && exists(path.slice(0, -1).concat("columns"))) {
      var row = {}; entries(path.slice(0, -1).concat("columns")).forEach(function (column) { var p = path.slice(0, -1).concat("columns", column.key), k = value(p.concat("key")) || value(p.concat("name")), t = value(p.concat("type")); if (k) row[k] = t === "number" ? 0 : t === "boolean" ? false : t === "array" ? [] : t === "object" ? {} : ""; }); config("row", row, "The new row starts with your table columns. Edit its values after adding.");
    } else {
      var presets = knownOptions(path);
      if (presets.length && !fieldMode && nodeKind === "map") {
        var presetSelect = dialogField(d.content, "Option", select([["", "Custom option"]].concat(presets.map(function (p) { return [p[0], label(p[0])]; })), ""));
        presetSelect.addEventListener("change", function () { var p = presets.find(function (p) { return p[0] === presetSelect.value; }); config("option", p ? p[1] : undefined); if (p) nameInput.value = p[0]; });
      }
      config("option");
    }
    d.content.append(fields); d.form.addEventListener("submit", function (event) {
      event.preventDefault(); if (!d.form.reportValidity()) return;
      try {
        var added, nameValue = nameInput ? nameInput.value.trim() : undefined;
        if (nameInput && !nameValue) throw new Error("Enter a name.");
        if (stepMode || gameMode) {
          if (!chosen) throw new Error("Choose a building block first."); if (ids(path).includes(nameValue)) throw new Error("This id already exists. Choose another id.");
          added = Object.assign({}, preset, { id: nameValue });
          if (stepMode && Object.hasOwn(added, "collection")) added.collection = (context.collections || [])[0]?.id || "";
          if (stepMode && Object.hasOwn(added, "workflow")) added.workflow = (context.workflows || [])[0]?.id || "";
          if (stepMode && Object.hasOwn(added, "table")) added.table = (context.tables || [])[0]?.id || "";
        } else if (fieldMode) { added = newSchema(typeInput.value); if (nodeKind === "seq") added.name = nameValue; }
        else if (name === "columns") added = { key: nameValue, type: typeInput.value };
        else if (preset !== null && typeof preset === "object") added = preset;
        else {
          var t = typeInput.value;
          if (t === "object") added = {}; else if (t === "array") added = []; else if (t === "null") added = null;
          else if (t === "string") added = initialInput.value;
          else if (t === "boolean") { if (!/^(true|false)$/i.test(initialInput.value)) throw new Error("Boolean values must be true or false."); added = initialInput.value.toLowerCase() === "true"; }
          else {
            if (initialInput.value.trim() === "") { d.fail("Enter a number, or choose Null for an empty value."); initialInput.focus(); return; }
            var temporary = window.OwnerDefinitionModel.parse("value: 0\n"); temporary.setScalar(["value"], "number", initialInput.value); added = temporary.scalar(["value"]);
          }
        }
        var index = entries(path).length;
        if (!mutate(function () { if (nodeKind === "map") model.add(path, nameValue, added); else model.insert(path, index, added); }, false)) { d.fail(status.textContent); return; }
        expanded.add(key(path.concat(nodeKind === "map" ? nameValue : index))); render(); d.dialog.close(); focusBlock(path.concat(nodeKind === "map" ? nameValue : index));
      } catch (error) { d.fail(error.message); }
    }); d.show();
  }
  function rename(path) {
    var d = modal("Rename field"), input = dialogField(d.content, "Field name", el("input")); input.value = String(path[path.length - 1]); input.required = true; d.submit.textContent = "Rename";
    d.form.addEventListener("submit", function (event) { event.preventDefault(); if (!d.form.reportValidity()) return; if (mutate(function () { model.rename(path, input.value.trim()); })) d.dialog.close(); else d.fail(status.textContent); }); d.show();
  }
  source.addEventListener("input", function () { if (!applying && !document.getElementById("definition-guided").hidden) load(); });
  source.form.addEventListener("submit", function (event) { if (!document.getElementById("definition-guided").hidden && !flush()) event.preventDefault(); });
  document.addEventListener("owner:catalog", function (event) { catalog = event.detail; catalogError = undefined; });
  document.addEventListener("owner:catalog-error", function () { catalogError = "Could not load the step catalog. Retry to add a step; existing blocks remain editable."; });
  document.getElementById("blocks-collapse").addEventListener("click", function () { if (flush()) { expanded.clear(); render(); } });
  document.getElementById("blocks-expand").addEventListener("click", function () {
    if (!model || !flush()) return;
    function walk(path) { expanded.add(key(path)); if (["map", "seq"].includes(model.kind(path))) entries(path).forEach(function (e) { var p = path.concat(e.key); if (["map", "seq", "alias"].includes(model.kind(p))) walk(p); }); }
    walk([]); render();
  });
  window.OwnerBlockEditor = {
    enterGuided: function () { load(); },
    leaveGuided: function (opener) {
      if (flush()) return Promise.resolve(true);
      return confirmDestructive({ title: "Discard unapplied input?", message: "An invalid option has not been applied to source. Discard that input and switch tabs?", confirmLabel: "Discard input", danger: true, opener: opener || null }).then(function (confirmed) {
        if (!confirmed) return false;
        render();
        return true;
      });
    },
    validate: flush,
    hasPending: function () { return Array.from(host.querySelectorAll("input,textarea,select")).some(function (control) { return control._blockPending; }); }
  };
  load();
})();
