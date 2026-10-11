"use strict";
// Owner definition model: a YAML/JSON source-backed model for the resource
// block editor. Parsing and navigation use the offline vendored YAML bundle
// (wwwroot/vendor/yaml-2.8.2.js, exposed as window.OwnerYaml); all new text
// is emitted by the small emitter below. The model never whole-document
// serializes: every mutation is a CST-range or line splice against the
// current source text, followed by a reparse. Unmodified bytes (comments,
// unknown fields, quoting, numeric lexemes, flow style, indentless lists)
// survive by construction, and stringify() returns the source byte-for-byte
// while no mutation has happened.
//
// Usage (browser): load the vendor bundle, then this file, then
//   var model = window.OwnerDefinitionModel.parse(textarea.value);
//   model.set(["steps", 0, "enabled"], false);
//   textarea.value = model.stringify();
// Reads: node/has/kind/entries/scalar/text. Mutations (all return void):
// set/setScalar/remove/rename/insert/add/move/duplicate. Every mutation is
// all-or-nothing: any failure restores the previous source and rethrows an
// Error with an actionable message. Nodes returned by node()/entries() are
// snapshots: re-resolve them after any mutation. Reads resolve aliases;
// set/setScalar replace the alias itself and remove/rename act on the
// reference, but any structural edit through or into a shared reference
// (add/insert/move/duplicate, or a path crossing an alias) is rejected with
// Raw source guidance. Merge keys (<<) surface in entries() as ordinary
// keys with alias values.
//
// Node tests: load the vendor bundle first (requiring it sets
// globalThis.OwnerYaml as a side effect), then require this file.
(function () {
  function resolveYaml() {
    if (typeof globalThis !== "undefined" && globalThis.OwnerYaml) return globalThis.OwnerYaml;
    if (typeof window !== "undefined" && window.OwnerYaml) return window.OwnerYaml;
    if (typeof require === "function") {
      try { return require("yaml"); } catch (ignore) { return null; }
    }
    return null;
  }

  var Y = resolveYaml();

  function fail(message) { return new Error(message); }

  // Canonical decimal compare, matching owner-json-editor.js: two numeric
  // spellings are exactly equal only when their normalized forms agree.
  function decimal(token) {
    var parts = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(token);
    if (!parts) return null;
    var digits = (parts[2] + (parts[3] || "")).replace(/^0+/, "") || "0";
    var exponent = BigInt(parts[4] || "0") - BigInt((parts[3] || "").length);
    while (digits.length > 1 && digits.endsWith("0")) { digits = digits.slice(0, -1); exponent++; }
    return parts[1] + digits + "e" + (digits === "0" ? "0" : exponent.toString());
  }

  function exactFloat(token) {
    var number = Number(token);
    if (!Number.isFinite(number)) return false;
    var canonical = decimal(token);
    return canonical !== null && canonical === decimal(String(number));
  }

  function isPlainObject(value) {
    if (value === null || typeof value !== "object") return false;
    var proto = Object.getPrototypeOf(value);
    return proto === Object.prototype || proto === null;
  }

  function put(object, key, value) {
    if (key === "__proto__") Object.defineProperty(object, key, { value: value, enumerable: true, writable: true, configurable: true });
    else object[key] = value;
  }

  // -- source splicing ------------------------------------------------

  function splice(text, start, end, insert) {
    return text.slice(0, start) + insert + text.slice(end);
  }

  function lineStart(text, off) {
    var i = text.lastIndexOf("\n", off - 1);
    return i < 0 ? 0 : i + 1;
  }

  function lineEnd(text, off) {
    var i = text.indexOf("\n", off);
    return i < 0 ? text.length : i;
  }

  function sameLine(text, a, b) {
    return lineStart(text, a) === lineStart(text, b);
  }

  function leadingSpace(line) {
    return (/^[ \t]*/.exec(line) || [""])[0];
  }

  // Block node ranges run through the trailing line break; clip it so line
  // spans and replacements never eat the next line.
  function clippedEnd(src, r) {
    var end = r[1];
    if (end > r[0] && src[end - 1] === "\n") {
      end--;
      if (end > r[0] && src[end - 1] === "\r") end--;
    }
    return end;
  }

  function applyEdits(text, edits) {
    var ordered = edits.slice().sort(function (x, y) { return y.start - x.start; });
    var out = text;
    for (var i = 0; i < ordered.length; i++) out = splice(out, ordered[i].start, ordered[i].end, ordered[i].text);
    return out;
  }

  // -- tiny emitter for freshly generated text -------------------------
  // JSON double quotes are valid YAML double quotes (JSON.stringify never
  // emits the one unsupported escape, \/).

  var PLAIN_KEY = /^[A-Za-z_][A-Za-z0-9_-]*$/;
  var RESERVED_KEY = /^(true|True|TRUE|false|False|FALSE|null|Null|NULL|~|yes|Yes|YES|no|No|NO|on|On|ON|off|Off|OFF)$/;

  function emitKey(key) {
    if (typeof key !== "string" || key.length === 0) throw fail("Key must be a non-empty string. Edit in Raw source for other key forms.");
    if (PLAIN_KEY.test(key) && !RESERVED_KEY.test(key) && !/^-?[\d.]/.test(key)) return key;
    return JSON.stringify(key);
  }

  function emitLeaf(value) {
    if (value === null || value === undefined) return "null";
    if (typeof value === "boolean") return value ? "true" : "false";
    if (typeof value === "bigint") return value.toString();
    if (typeof value === "number") {
      if (!Number.isFinite(value)) throw fail("Number is not finite and has no YAML form. Use a string, or edit in Raw source.");
      return String(value);
    }
    if (typeof value === "string") return JSON.stringify(value);
    throw fail("Unsupported value type. Use a string, number, boolean, null, array or plain object, or edit in Raw source.");
  }

  function emitFlow(value, seen) {
    if (value === null || value === undefined || typeof value !== "object") return emitLeaf(value);
    if (seen.indexOf(value) >= 0) throw fail("Value is circular and cannot be written as YAML. Edit in Raw source.");
    seen.push(value);
    try {
      if (Array.isArray(value)) {
        return "[" + value.map(function (item) { return emitFlow(item, seen); }).join(", ") + "]";
      }
      if (!isPlainObject(value)) throw fail("Unsupported value type. Use a string, number, boolean, null, array or plain object, or edit in Raw source.");
      return "{" + Object.keys(value).map(function (key) { return emitKey(key) + ": " + emitFlow(value[key], seen); }).join(", ") + "}";
    } finally {
      seen.pop();
    }
  }

  // Block lines for a value, each prefixed with indent.
  function emitBlockLines(value, indent, seen) {
    if (value === null || value === undefined || typeof value !== "object") return [indent + emitLeaf(value)];
    if (seen.indexOf(value) >= 0) throw fail("Value is circular and cannot be written as YAML. Edit in Raw source.");
    seen.push(value);
    try {
      if (Array.isArray(value)) {
        if (value.length === 0) return [indent + "[]"];
        var lines = [];
        value.forEach(function (item) {
          if (item === null || item === undefined || typeof item !== "object") {
            lines.push(indent + "- " + emitLeaf(item));
          } else if (Array.isArray(item)) {
            lines.push(indent + "-");
            lines.push.apply(lines, emitBlockLines(item, indent + "  ", seen));
          } else if (isPlainObject(item)) {
            var inner = emitBlockLines(item, indent + "  ", seen);
            if (inner.length === 1 && inner[0].slice((indent + "  ").length) === "{}") lines.push(indent + "- {}");
            else {
              inner[0] = indent + "- " + inner[0].slice((indent + "  ").length);
              lines.push.apply(lines, inner);
            }
          } else throw fail("Unsupported value type. Use a string, number, boolean, null, array or plain object, or edit in Raw source.");
        });
        return lines;
      }
      if (!isPlainObject(value)) throw fail("Unsupported value type. Use a string, number, boolean, null, array or plain object, or edit in Raw source.");
      var keys = Object.keys(value);
      if (keys.length === 0) return [indent + "{}"];
      var out = [];
      keys.forEach(function (key) {
        var item = value[key];
        if (item === null || item === undefined || typeof item !== "object") out.push(indent + emitKey(key) + ": " + emitLeaf(item));
        else if (Array.isArray(item) && item.length === 0) out.push(indent + emitKey(key) + ": []");
        else if (isPlainObject(item) && Object.keys(item).length === 0) out.push(indent + emitKey(key) + ": {}");
        else {
          out.push(indent + emitKey(key) + ":");
          out.push.apply(out, emitBlockLines(item, indent + "  ", seen));
        }
      });
      return out;
    } finally {
      seen.pop();
    }
  }

  // -- AST helpers ------------------------------------------------------

  function pairKeyText(pair) {
    if (Y.isScalar(pair.key)) return String(pair.key.value);
    if (pair.key && pair.key.range) return null; // complex key: no text form
    return null;
  }

  function findPair(map, key) {
    var i, pair;
    for (i = 0; i < map.items.length; i++) {
      pair = map.items[i];
      if (Y.isScalar(pair.key) && pair.key.value === key) return { pair: pair, index: i };
    }
    // Numeric and boolean keys resolve by their printed form.
    if (typeof key === "string") {
      for (i = 0; i < map.items.length; i++) {
        pair = map.items[i];
        if (Y.isScalar(pair.key) && String(pair.key.value) === key) return { pair: pair, index: i };
      }
    }
    return null;
  }

  function coerceIndex(seg) {
    if (typeof seg === "number" && Number.isInteger(seg) && seg >= 0) return seg;
    if (typeof seg === "string" && /^\d+$/.test(seg)) return Number(seg);
    return null;
  }

  function hasAnchor(node) {
    if (!node || typeof node !== "object") return false;
    if (node.anchor) return true;
    if (Y.isAlias(node)) return false;
    if (Y.isMap(node)) {
      return node.items.some(function (pair) { return hasAnchor(pair.key) || hasAnchor(pair.value); });
    }
    if (Y.isSeq(node)) {
      return node.items.some(hasAnchor);
    }
    return false;
  }

  // Semantic copy of a node with anchors dropped and aliases inlined.
  // Throws for circular or otherwise unsafe shapes: the caller reports Raw.
  function nodeToJs(node, doc, resolving) {
    if (node === null || node === undefined) return null;
    if (Y.isAlias(node)) {
      if (resolving.indexOf(node) >= 0) throw fail("Reference loop cannot be copied safely. Duplicate it by hand in Raw source.");
      var target = node.resolve(doc);
      if (!target) throw fail("Dangling reference cannot be copied safely. Fix it in Raw source first.");
      resolving.push(node);
      try { return nodeToJs(target, doc, resolving); }
      finally { resolving.pop(); }
    }
    if (Y.isMap(node)) {
      var obj = {};
      node.items.forEach(function (pair) {
        if (!Y.isScalar(pair.key)) throw fail("Complex key cannot be copied safely. Duplicate it by hand in Raw source.");
        put(obj, String(pair.key.value), nodeToJs(pair.value, doc, resolving));
      });
      return obj;
    }
    if (Y.isSeq(node)) {
      return node.items.map(function (item) { return nodeToJs(item, doc, resolving); });
    }
    if (Y.isScalar(node)) return node.value;
    throw fail("Unsupported node cannot be copied safely. Duplicate it by hand in Raw source.");
  }

  function entryKeyOf(pair, src) {
    if (Y.isScalar(pair.key)) return String(pair.key.value);
    if (pair.key && pair.key.range) return src.slice(pair.key.range[0], pair.key.range[1]);
    return "?";
  }

  // Leading "&anchor (!tag )?" of a value or key source slice. Replacements
  // keep it so references stay bound to the edited node.
  function anchorPrefix(slice) {
    var anchored = /^(&[^\s\[{\],]+[ \t]+)(![^\s]+[ \t]+)?/.exec(slice);
    return anchored ? anchored[0] : "";
  }

  // -- model ------------------------------------------------------------

  function parseSource(source) {
    if (typeof source !== "string") throw fail("Definition source must be text.");
    if (!Y) throw fail("YAML support is missing: the vendor bundle did not load. Reload the page.");
    var docs;
    try {
      docs = Y.parseAllDocuments(source, { intAsBigInt: true });
    } catch (error) {
      throw fail("Invalid YAML definition: " + (error && error.message ? error.message : String(error)));
    }
    var live = docs.filter(function (doc) { return doc.contents !== null; });
    if (live.length === 0) throw fail("Definition is empty. Add a YAML mapping, or pick an example.");
    if (live.length > 1) throw fail("Definition holds more than one document. Keep a single mapping, and edit extras in Raw source.");
    var doc = live[0];
    if (doc.errors && doc.errors.length > 0) {
      var first = doc.errors[0];
      throw fail("Invalid YAML definition: " + (first && first.message ? first.message : "could not parse."));
    }
    if (!Y.isMap(doc.contents)) throw fail("Definition must be a YAML mapping at the top level, not a list or scalar.");
    var guard = findPair(doc.contents, "sourceText");
    if (guard && Y.isScalar(guard.pair.value) && typeof guard.pair.value.value === "string" && guard.pair.value.value.trim() !== "") {
      throw fail("This definition carries authoritative sourceText. Edit its source in Raw source; guided changes to compiled fields would be overwritten.");
    }
    return doc;
  }

  function parse(source) {
    var doc = parseSource(source);
    var state = { source: source, doc: doc, dirty: false };

    function normalizePath(path) {
      if (!Array.isArray(path)) throw fail("Path must be an array of keys and indices.");
      path.forEach(function (seg) {
        if (typeof seg !== "string" && typeof seg !== "number") throw fail("Path holds only strings and non-negative integers.");
        if (typeof seg === "number" && (!Number.isInteger(seg) || seg < 0)) throw fail("Path holds only strings and non-negative integers.");
      });
      return path.slice();
    }

    // Resolves a path. Returns undefined when a key or index is absent.
    // Throws when navigation crosses a scalar, or an index is out of range
    // for reads that require the node. Sets aliasHop when any intermediate
    // step crossed an alias; final holds parent context for mutations.
    function resolve(path, forMutation) {
      var segs = normalizePath(path);
      var node = state.doc.contents;
      var parent = null;
      var pair = null;
      var index = -1;
      var aliasHop = false;
      for (var depth = 0; depth < segs.length; depth++) {
        if (Y.isAlias(node)) {
          var target = node.resolve(state.doc);
          if (!target) {
            if (forMutation) throw fail("Dangling reference at \"" + segs.slice(0, depth).join(".") + "\". Fix it in Raw source first.");
            return undefined;
          }
          aliasHop = true;
          node = target;
        }
        var seg = segs[depth];
        if (Y.isMap(node)) {
          if (typeof seg !== "string") throw fail("Expected a string key at \"" + segs.slice(0, depth).join(".") + "\".");
          var found = findPair(node, seg);
          if (!found) return undefined;
          parent = node;
          pair = found.pair;
          index = found.index;
          node = pair.value;
        } else if (Y.isSeq(node)) {
          var at = coerceIndex(seg);
          if (at === null) throw fail("Expected a numeric index at \"" + segs.slice(0, depth).join(".") + "\".");
          if (at >= node.items.length) return undefined;
          parent = node;
          pair = null;
          index = at;
          node = node.items[at];
        } else {
          if (forMutation) throw fail("Nothing to change at \"" + segs.join(".") + "\": the path crosses a scalar.");
          return undefined;
        }
      }
      return { node: node, parent: parent, pair: pair, index: index, aliasHop: aliasHop, path: segs };
    }

    function requireNode(path) {
      var hit = resolve(path, false);
      if (!hit || !hit.node) throw fail("Unknown path \"" + normalizePath(path).join(".") + "\".");
      return hit;
    }

    function resolveTarget(hit) {
      var node = hit.node;
      if (Y.isAlias(node)) {
        var target = node.resolve(state.doc);
        if (!target) throw fail("Dangling reference at \"" + hit.path.join(".") + "\". Fix it in Raw source first.");
        return target;
      }
      return node;
    }

    function mutate(fn) {
      var snapshot = state.source;
      var next;
      try {
        next = fn(state.source, state.doc);
      } catch (error) {
        throw error;
      }
      var nextDoc;
      try {
        nextDoc = parseSource(next);
      } catch (error) {
        state.source = snapshot;
        state.doc = parseSource(snapshot);
        throw error;
      }
      // parseSource already rejects duplicates, syntax faults and
      // non-mapping roots, so reaching here means the edit is safe.
      state.source = next;
      state.doc = nextDoc;
      state.dirty = true;
    }

    // Span covering a map pair or seq item for whole-line block edits.
    function childSpan(src, parent, pair, index) {
      var node = pair ? pair.value : parent.items[index];
      var r = node && node.range ? node.range : null;
      var head = pair ? pair.key.range[0] : (r ? r[0] : 0);
      var start = lineStart(src, head);
      if (!pair && r) {
        var first = src.slice(start, lineEnd(src, start));
        if (!/^\s*-(\s|$)/.test(first)) {
          var prevEnd = start - 1;
          if (prevEnd >= 0) {
            var prevStart = lineStart(src, prevEnd);
            var prev = src.slice(prevStart, lineEnd(src, prevStart));
            if (/^\s*-\s*(#[^\n]*)?$/.test(prev)) start = prevStart;
          }
        }
      }
      var endOff = r ? clippedEnd(src, r) : head;
      if (pair && (!node || !node.range)) endOff = pair.key.range[1];
      var end = lineEnd(src, endOff);
      return { start: start, end: end };
    }

    // Cut [start, end) lines, eating one adjacent newline so no blank line
    // is left behind.
    function cutLines(src, start, end) {
      if (src[end] === "\n") return splice(src, start, end + 1, "");
      if (start > 0) return splice(src, start - 1, end, "");
      return splice(src, start, end, "");
    }

    // Replace the VALUE of a pair/item with newText (a single-line flow
    // lexeme or scalar), collapsing a nested block when the value starts on
    // a later line. Returns the edited source.
    function replaceValueText(src, parent, pair, index, newText) {
      var node = pair ? pair.value : parent.items[index];
      var r = node && node.range ? node.range : null;
      if (!r) throw fail("Value has no source position. Edit in Raw source.");
      if (r[0] === r[1]) {
        var before = r[0] > 0 ? src[r[0] - 1] : "\n";
        var after = r[0] < src.length ? src[r[0]] : "\n";
        var pre = before === " " || before === "\t" ? "" : " ";
        var suf = after === " " || after === "\t" || after === "#" || after === "\n" || after === "\r" ? "" : " ";
        return splice(src, r[0], r[1], pre + newText + suf);
      }
      var keep = anchorPrefix(src.slice(r[0], clippedEnd(src, r)));
      var rend = clippedEnd(src, r);
      if (Y.isMap(parent)) {
        if (sameLine(src, pair.key.range[0], r[0])) return splice(src, r[0], rend, keep + newText);
        var keyEnd = pair.key.range[1];
        var keyLineEnd = lineEnd(src, keyEnd);
        var between = src.slice(keyEnd, keyLineEnd);
        var hash = between.indexOf("#");
        var comment = hash >= 0 ? " " + between.slice(hash).replace(/\s+$/, "") : "";
        return splice(src, keyEnd, rend, ": " + keep + newText + comment);
      }
      if (Y.isSeq(parent)) {
        var ls = lineStart(src, r[0]);
        var line = src.slice(ls, lineEnd(src, ls));
        var dash = /^(\s*-\s*)/.exec(line);
        if (dash && lineStart(src, r[0]) === ls && r[0] >= ls + dash[0].length) {
          return splice(src, r[0], rend, keep + newText);
        }
        if (dash) {
          var rest = line.slice(dash[0].length);
          var mark = rest.indexOf("#");
          var dashComment = mark >= 0 ? " " + rest.slice(mark).replace(/\s+$/, "") : "";
          return splice(src, ls + dash[0].length, rend, keep + newText + dashComment);
        }
        return splice(src, r[0], rend, keep + newText);
      }
      return splice(src, r[0], rend, keep + newText);
    }

    function setLeaf(hit, value) {
      var node = hit.node;
      if (!Y.isScalar(node) && !Y.isAlias(node)) throw fail("Expected a scalar at \"" + hit.path.join(".") + "\". Use entries() to walk maps and lists.");
      mutate(function (src) {
        return replaceValueText(src, hit.parent, hit.pair, hit.index, emitLeaf(value));
      });
    }

    function isEmptyScalar(node) {
      return Y.isScalar(node) && node.value === null;
    }

    function setRecursive(path, value) {
      var hit = resolve(path, true);
      if (!hit || !hit.node) throw fail("Unknown path \"" + normalizePath(path).join(".") + "\".");
      if (hit.aliasHop) throw fail("This value passes through a shared reference. Edit it in Raw source.");
      if (path.length === 0 && !isPlainObject(value)) throw fail("The definition root must stay a mapping.");
      var node = hit.node;
      if (Y.isAlias(node)) { setLeaf(hit, value); return; }
      if (value === null || value === undefined || typeof value !== "object") {
        if (!Y.isScalar(node)) {
          mutate(function (src) {
            return replaceValueText(src, hit.parent, hit.pair, hit.index, emitLeaf(value));
          });
          return;
        }
        setLeaf(hit, value);
        return;
      }
      if (!Array.isArray(value) && !isPlainObject(value)) throw fail("Unsupported value type. Use a string, number, boolean, null, array or plain object, or edit in Raw source.");
      if (Array.isArray(value)) {
        if (!Y.isSeq(node)) {
          if (isEmptyScalar(node)) {
            for (var n = 0; n < value.length; n++) insertAt(path, n, value[n]);
            return;
          }
          mutate(function (src) {
            return replaceValueText(src, hit.parent, hit.pair, hit.index, emitFlow(value, []));
          });
          return;
        }
        var base = node.items.length;
        var min = Math.min(base, value.length);
        for (var i = 0; i < min; i++) setRecursive(path.concat([i]), value[i]);
        for (var add = min; add < value.length; add++) insertAt(path, add, value[add]);
        for (var drop = base - 1; drop >= value.length; drop--) removeAt(path.concat([drop]));
        return;
      }
      if (!Y.isMap(node)) {
        if (isEmptyScalar(node)) {
          var names = Object.keys(value);
          for (var m = 0; m < names.length; m++) addAt(path, names[m], value[names[m]]);
          return;
        }
        mutate(function (src) {
          return replaceValueText(src, hit.parent, hit.pair, hit.index, emitFlow(value, []));
        });
        return;
      }
      var keys = Object.keys(value);
      for (var k = 0; k < keys.length; k++) {
        var child = resolve(path.concat([keys[k]]), true);
        if (child && child.node) setRecursive(path.concat([keys[k]]), value[keys[k]]);
        else addAt(path, keys[k], value[keys[k]]);
      }
    }

    // Resolves a mutation target that must be a mapping (wantMap) or a list.
    // An empty value (null scalar) counts as an empty block collection so the
    // first key or item can be added; node is null in that case.
    function targetCollection(path, verb, wantMap) {
      var segs = normalizePath(path);
      var hit = resolve(segs, true);
      if (!hit || !hit.node) throw fail("Unknown path \"" + segs.join(".") + "\".");
      if (hit.aliasHop || Y.isAlias(hit.node)) throw fail("This entry is a shared reference. Edit it in Raw source.");
      var node = resolveTarget(hit);
      if (isEmptyScalar(node)) return { hit: hit, node: null };
      if (wantMap && !Y.isMap(node)) throw fail("Expected a mapping at \"" + hit.path.join(".") + "\" to " + verb + ". Use set() to replace the value first.");
      if (!wantMap && !Y.isSeq(node)) throw fail("Expected a list at \"" + hit.path.join(".") + "\" to " + verb + ". Use set() to replace the value first.");
      return { hit: hit, node: node };
    }

    function blockEntry(key, value) {
      var emittedKey = emitKey(key);
      if (isPlainObject(value) || Array.isArray(value)) {
        return emittedKey + ":\n" + emitBlockLines(value, "  ", []).join("\n");
      }
      return emittedKey + ": " + emitLeaf(value);
    }

    function addAt(path, key, value) {
      if (typeof key !== "string" || key.length === 0) throw fail("Key must be a non-empty string.");
      var target = targetCollection(path, "add a key", true);
      var node = target.node;
      if (node && findPair(node, key)) throw fail("Key \"" + key + "\" already exists at \"" + target.hit.path.join(".") + "\". Rename or remove it first.");
      var emittedKey = emitKey(key);
      mutate(function (src) {
        if (node && node.flow) {
          var inner = src.slice(node.range[0] + 1, node.range[1] - 1);
          if (/^\s*$/.test(inner)) {
            return splice(src, node.range[0], node.range[1], "{ " + emittedKey + ": " + emitFlow(value, []) + " }");
          }
          return splice(src, node.range[1] - 1, node.range[1] - 1, ", " + emittedKey + ": " + emitFlow(value, []));
        }
        var entry = blockEntry(key, value);
        if (!node || node.items.length === 0) {
          // Empty value or empty block map: hang the entry under the key line.
          if (!target.hit.pair) {
            // Only the root "{}" mapping has no key line of its own.
            return splice(src, node.range[0], node.range[1], entry);
          }
          var keyLs = lineStart(src, target.hit.pair.key.range[0]);
          var base = leadingSpace(src.slice(keyLs, lineEnd(src, keyLs))) + "  ";
          var anchorLine = lineEnd(src, target.hit.pair.key.range[1]);
          var prefixed = entry.split("\n").map(function (line) { return base + line; }).join("\n");
          return splice(src, anchorLine, anchorLine, "\n" + prefixed);
        }
        var last = node.items[node.items.length - 1];
        var span = childSpan(src, node, last, node.items.length - 1);
        var indent = leadingSpace(src.slice(span.start, span.end));
        var body = entry.split("\n").map(function (line) { return indent + line; }).join("\n");
        return splice(src, span.end, span.end, "\n" + body);
      });
    }

    function insertAt(path, index, value) {
      if (!Number.isInteger(index) || index < 0) throw fail("Index must be a non-negative integer.");
      var target = targetCollection(path, "insert an item", false);
      var node = target.node;
      var length = node ? node.items.length : 0;
      if (index > length) throw fail("Index " + index + " is past the end of the list at \"" + target.hit.path.join(".") + "\" (length " + length + ").");
      mutate(function (src) {
        if (node && node.flow) {
          var lex = emitFlow(value, []);
          if (node.items.length === 0) return splice(src, node.range[0], node.range[1], "[" + lex + "]");
          if (index === node.items.length) {
            var lastR = node.items[node.items.length - 1].range;
            return splice(src, lastR[1], lastR[1], ", " + lex);
          }
          var atR = node.items[index].range;
          return splice(src, atR[0], atR[0], lex + ", ");
        }
        var indent;
        if (node && node.items.length > 0) {
          var ref = index === node.items.length ? node.items.length - 1 : index;
          var refSpan = childSpan(src, node, null, ref);
          indent = leadingSpace(src.slice(refSpan.start, refSpan.end));
        } else if (target.hit.pair) {
          var keyLs = lineStart(src, target.hit.pair.key.range[0]);
          indent = leadingSpace(src.slice(keyLs, lineEnd(src, keyLs))) + "  ";
        } else {
          indent = "";
        }
        var body;
        if (value === null || value === undefined || typeof value !== "object") body = indent + "- " + emitLeaf(value);
        else if (Array.isArray(value)) {
          if (value.length === 0) body = indent + "- []";
          else body = indent + "-\n" + emitBlockLines(value, indent + "  ", []).join("\n");
        } else if (isPlainObject(value)) {
          var keys = Object.keys(value);
          if (keys.length === 0) body = indent + "- {}";
          else {
            var inner = emitBlockLines(value, indent + "  ", []);
            inner[0] = indent + "- " + inner[0].slice((indent + "  ").length);
            body = inner.join("\n");
          }
        } else throw fail("Unsupported value type. Use a string, number, boolean, null, array or plain object, or edit in Raw source.");
        if (!node || node.items.length === 0) {
          // Empty value: hang the item under the key line.
          if (!target.hit.pair) throw fail("Cannot insert here. Edit in Raw source.");
          var anchorLine = lineEnd(src, target.hit.pair.key.range[1]);
          return splice(src, anchorLine, anchorLine, "\n" + body);
        }
        if (index === node.items.length) {
          var tail = childSpan(src, node, null, node.items.length - 1);
          return splice(src, tail.end, tail.end, "\n" + body);
        }
        var slot = childSpan(src, node, null, index);
        return splice(src, slot.start, slot.start, body + "\n");
      });
    }

    function pairValueEnd(pair) {
      if (pair.value && pair.value.range) return pair.value.range[1];
      return pair.key.range[1];
    }

    function removeAt(path) {
      var segs = normalizePath(path);
      var hit = resolve(segs, true);
      if (!hit || !hit.node) throw fail("Unknown path \"" + segs.join(".") + "\".");
      if (hit.aliasHop) throw fail("This entry passes through a shared reference. Edit it in Raw source.");
      if (!hit.parent) throw fail("The definition root cannot be removed.");
      var parent = hit.parent;
      if (parent.flow) {
        mutate(function (src) {
          if (parent.items.length === 1) return splice(src, parent.range[0], parent.range[1], Y.isSeq(parent) ? "[]" : "{}");
          if (Y.isSeq(parent)) {
            if (hit.index === parent.items.length - 1) {
              return splice(src, parent.items[hit.index - 1].range[1], parent.items[hit.index].range[1], "");
            }
            return splice(src, parent.items[hit.index].range[0], parent.items[hit.index + 1].range[0], "");
          }
          var kids = parent.items;
          if (hit.index === kids.length - 1) {
            var prev = kids[hit.index - 1];
            var prevEnd = (prev.value && prev.value.range ? prev.value.range : prev.key.range)[1];
            return splice(src, prevEnd, pairValueEnd(hit.pair), "");
          }
          return splice(src, hit.pair.key.range[0], kids[hit.index + 1].key.range[0], "");
        });
        return;
      }
      if (parent.items.length > 1) {
        mutate(function (src) {
          var span = childSpan(src, parent, hit.pair, hit.index);
          return cutLines(src, span.start, span.end);
        });
        return;
      }
      // Last child: collapse the emptied collection to an explicit empty so
      // the key does not silently become null.
      var owner = resolve(segs.slice(0, -1), true);
      if (!owner || !owner.node) throw fail("Unknown path \"" + segs.slice(0, -1).join(".") + "\".");
      if (owner.aliasHop) throw fail("This entry passes through a shared reference. Edit it in Raw source.");
      if (!owner.parent) {
        // Only key of the root mapping.
        mutate(function () { return "{}\n"; });
        return;
      }
      mutate(function (src) {
        return replaceValueText(src, owner.parent, owner.pair, owner.index, Y.isSeq(parent) ? "[]" : "{}");
      });
    }

    // Public model.
    var model = {
      yaml: Y,
      get doc() { return state.doc; },
      isDirty: function () { return state.dirty; },
      stringify: function () { return state.source; },

      node: function (path) {
        var hit = resolve(path, false);
        return hit && hit.node ? hit.node : undefined;
      },

      has: function (path) {
        var segs = normalizePath(path);
        try {
          var hit = resolve(segs, false);
          return !!(hit && hit.node);
        } catch (error) {
          return false;
        }
      },

      kind: function (path) {
        var hit = requireNode(path);
        var node = hit.node;
        if (Y.isAlias(node)) return "alias";
        if (Y.isMap(node)) return "map";
        if (Y.isSeq(node)) return "seq";
        if (Y.isScalar(node)) return node.value === null ? "null" : "scalar";
        throw fail("Unknown node at \"" + hit.path.join(".") + "\". Edit in Raw source.");
      },

      entries: function (path) {
        var hit = requireNode(path);
        var node = resolveTarget(hit);
        if (Y.isMap(node)) {
          return node.items.map(function (pair) { return { key: entryKeyOf(pair, state.source), node: pair.value }; });
        }
        if (Y.isSeq(node)) {
          return node.items.map(function (item, i) { return { key: i, node: item }; });
        }
        throw fail("Expected a mapping or list at \"" + hit.path.join(".") + "\".");
      },

      scalar: function (path) {
        var hit = requireNode(path);
        var node = resolveTarget(hit);
        if (!Y.isScalar(node)) throw fail("Expected a scalar at \"" + hit.path.join(".") + "\". Use entries() to walk maps and lists.");
        return node.value;
      },

      text: function (path) {
        var hit = requireNode(path);
        var node = resolveTarget(hit);
        if (!Y.isScalar(node)) throw fail("Expected a scalar at \"" + hit.path.join(".") + "\".");
        if (typeof node.value === "string") return node.value;
        if (typeof node.source === "string" && node.source !== "") return node.source;
        return String(node.value);
      },

      set: function (path, value) {
        setRecursive(normalizePath(path), value);
      },

      setScalar: function (path, type, text) {
        if (type !== "string" && type !== "number" && type !== "boolean" && type !== "null") {
          throw fail("Scalar type must be one of string, number, boolean or null.");
        }
        if (typeof text !== "string") throw fail("Scalar text must be a string.");
        var lex;
        if (type === "string") lex = JSON.stringify(text);
        else if (type === "boolean") {
          if (!/^(true|True|TRUE|false|False|FALSE)$/.test(text)) throw fail("Boolean must be true or false. Edit in Raw source for other spellings.");
          lex = text.toLowerCase() === "true" ? "true" : "false";
        } else if (type === "null") {
          if (!/^(~|null|Null|NULL)?$/.test(text)) throw fail("Null must be null or ~. Edit in Raw source for other spellings.");
          lex = "null";
        } else {
          lex = validateNumberLexeme(text);
        }
        var hit = resolve(normalizePath(path), true);
        if (!hit || !hit.node) throw fail("Unknown path \"" + normalizePath(path).join(".") + "\".");
        if (hit.aliasHop) throw fail("This value passes through a shared reference. Edit it in Raw source.");
        if (!Y.isScalar(hit.node) && !Y.isAlias(hit.node)) {
          throw fail("Expected a scalar at \"" + hit.path.join(".") + "\". Use set() to replace maps and lists.");
        }
        mutate(function (src) {
          return replaceValueText(src, hit.parent, hit.pair, hit.index, lex);
        });
      },

      remove: function (path) {
        removeAt(normalizePath(path));
      },

      rename: function (path, newKey) {
        if (typeof newKey !== "string" || newKey.length === 0) throw fail("New key must be a non-empty string.");
        var hit = resolve(normalizePath(path), true);
        if (!hit || !hit.node) throw fail("Unknown path \"" + normalizePath(path).join(".") + "\".");
        if (!hit.parent || !Y.isMap(hit.parent) || !hit.pair) throw fail("Rename needs a mapping entry path.");
        if (hit.aliasHop) throw fail("This entry passes through a shared reference. Edit it in Raw source.");
        var current = Y.isScalar(hit.pair.key) ? hit.pair.key.value : null;
        if (current === newKey) return;
        if (findPair(hit.parent, newKey)) throw fail("Key \"" + newKey + "\" already exists. Rename or remove it first.");
        var emitted = emitKey(newKey);
        mutate(function (src) {
          var keep = anchorPrefix(src.slice(hit.pair.key.range[0], hit.pair.key.range[1]));
          return splice(src, hit.pair.key.range[0], hit.pair.key.range[1], keep + emitted);
        });
      },

      insert: function (path, index, value) {
        insertAt(normalizePath(path), index, value);
      },

      add: function (path, key, value) {
        addAt(normalizePath(path), key, value);
      },

      move: function (path, fromIndex, toIndex) {
        var hit = resolve(normalizePath(path), true);
        if (!hit || !hit.node) throw fail("Unknown path \"" + normalizePath(path).join(".") + "\".");
        if (hit.aliasHop || Y.isAlias(hit.node)) throw fail("This list is a shared reference. Edit it in Raw source.");
        var node = resolveTarget(hit);
        if (!Y.isMap(node) && !Y.isSeq(node)) throw fail("Move needs a mapping or list at \"" + hit.path.join(".") + "\".");
        var len = node.items.length;
        if (!Number.isInteger(fromIndex) || fromIndex < 0 || fromIndex >= len) throw fail("Move source " + fromIndex + " is outside the entries at \"" + hit.path.join(".") + "\" (length " + len + ").");
        if (!Number.isInteger(toIndex) || toIndex < 0 || toIndex >= len) throw fail("Move target " + toIndex + " is outside the entries at \"" + hit.path.join(".") + "\" (length " + len + ").");
        if (fromIndex === toIndex) return;
        mutate(function (src) {
          var order = node.items.map(function (_, i) { return i; });
          var moved = order.splice(fromIndex, 1)[0];
          order.splice(toIndex, 0, moved);
          if (node.flow) {
            var slots = node.items.map(function (item) { return { start: item.range[0], end: item.range[1], text: src.slice(item.range[0], item.range[1]) }; });
            var edits = slots.map(function (slot, i) { return { start: slot.start, end: slot.end, text: slots[order[i]].text }; });
            return applyEdits(src, edits);
          }
          var spans = node.items.map(function (_, i) {
            var pair = Y.isMap(node) ? node.items[i] : null;
            var span = childSpan(src, node, pair, i);
            return { start: span.start, end: span.end, text: src.slice(span.start, span.end) };
          });
          var swaps = spans.map(function (span, i) { return { start: span.start, end: span.end, text: spans[order[i]].text }; });
          return applyEdits(src, swaps);
        });
      },

      duplicate: function (parentPath, index, newId) {
        var hit = resolve(normalizePath(parentPath), true);
        if (!hit || !hit.node) throw fail("Unknown path \"" + normalizePath(parentPath).join(".") + "\".");
        if (hit.aliasHop || Y.isAlias(hit.node)) throw fail("This entry is a shared reference. Edit it in Raw source.");
        var node = resolveTarget(hit);
        if (!Number.isInteger(index) || index < 0) throw fail("Index must be a non-negative integer.");
        if (Y.isSeq(node)) {
          if (index >= node.items.length) throw fail("Index " + index + " is past the end of the list (length " + node.items.length + ").");
          var item = node.items[index];
          if (hasAnchor(item)) {
            var data = nodeToJs(item, state.doc, []);
            if (newId !== undefined) rewriteId(data, newId);
            insertAt(parentPath, index + 1, data);
            return;
          }
          var idEdit = null;
          if (newId !== undefined && Y.isMap(item)) {
            var idPair = findPair(item, "id");
            if (idPair && Y.isScalar(idPair.pair.value) && idPair.pair.value.range) {
              if (typeof newId !== "string" || newId.length === 0) throw fail("New id must be a non-empty string.");
              var idRange = idPair.pair.value.range;
              idEdit = { range: idRange, lex: idRange[1] > idRange[0] ? emitKey(newId) : " " + emitKey(newId) };
            }
          }
          mutate(function (src) {
            if (node.flow) {
              var slice = src.slice(item.range[0], item.range[1]);
              if (idEdit) slice = splice(slice, idEdit.range[0] - item.range[0], idEdit.range[1] - item.range[0], idEdit.lex);
              var anchor = node.items[index].range;
              return splice(src, anchor[1], anchor[1], ", " + slice);
            }
            var span = childSpan(src, node, null, index);
            var text = src.slice(span.start, span.end);
            if (idEdit) text = splice(text, idEdit.range[0] - span.start, idEdit.range[1] - span.start, idEdit.lex);
            return splice(src, span.end, span.end, "\n" + text);
          });
          return;
        }
        if (Y.isMap(node)) {
          if (index >= node.items.length) throw fail("Index " + index + " is past the end of the mapping (length " + node.items.length + ").");
          if (typeof newId !== "string" || newId.length === 0) throw fail("Duplicating a mapping entry needs a new key.");
          if (findPair(node, newId)) throw fail("Key \"" + newId + "\" already exists. Rename or remove it first.");
          var pair = node.items[index];
          if (!pair.value || !pair.value.range) {
            addAt(parentPath, newId, nodeToJs(pair.value, state.doc, []));
            return;
          }
          if (hasAnchor(pair.key) || hasAnchor(pair.value)) {
            addAt(parentPath, newId, nodeToJs(pair.value, state.doc, []));
            return;
          }
          var emittedKey = emitKey(newId);
          mutate(function (src) {
            if (node.flow) {
              var slice = src.slice(pair.value.range[0], pair.value.range[1]);
              var anchor = pair.value.range;
              return splice(src, anchor[1], anchor[1], ", " + emittedKey + ": " + slice);
            }
            var span = childSpan(src, node, pair, index);
            var indent = leadingSpace(src.slice(span.start, span.end));
            if (pair.value.range && sameLine(src, pair.key.range[0], pair.value.range[0])) {
              var entry = emittedKey + ": " + src.slice(pair.value.range[0], pair.value.range[1]);
              return splice(src, span.end, span.end, "\n" + indent + entry);
            }
            var firstValueLine = lineStart(src, pair.value.range[0]);
            var valueLines = src.slice(firstValueLine, span.end);
            return splice(src, span.end, span.end, "\n" + indent + emittedKey + ":\n" + valueLines);
          });
          return;
        }
        throw fail("Duplicate needs a mapping or list at \"" + hit.path.join(".") + "\".");
      }
    };

    function rewriteId(data, newId) {
      if (!isPlainObject(data)) return;
      if (typeof newId !== "string" || newId.length === 0) throw fail("New id must be a non-empty string.");
      if (Object.prototype.hasOwnProperty.call(data, "id")) put(data, "id", newId);
    }

    function validateNumberLexeme(text) {
      var t = text.trim();
      if (!t) throw fail("Number needs digits. Use null for empty, or edit in Raw source.");
      if (/^[-+]?(\.inf|\.Inf|\.INF|\.nan|\.NaN|\.NAN)$/.test(t)) throw fail("Number is not finite and cannot round-trip. Use a string, or edit in Raw source.");
      var intRe = /^[+-]?(0[xX][0-9a-fA-F](_?[0-9a-fA-F])*|0[oO][0-7](_?[0-7])*|[0-9](_?[0-9])*)$/;
      var floatRe = /^[+-]?([0-9](_?[0-9])*\.([0-9](_?[0-9])*)?|\.[0-9](_?[0-9])*|[0-9](_?[0-9])*)([eE][+-]?[0-9](_?[0-9])*)?$/.test(t)
        && /[.eE]/.test(t.replace(/_/g, ""));
      if (intRe.test(t)) {
        var plain = t.replace(/_/g, "");
        var want;
        try { want = BigInt(plain); } catch (ignore) { throw fail("Number \"" + t + "\" is not a plain integer. Use a string, or edit in Raw source."); }
        var probe = Y.parseDocument("k: " + t, { intAsBigInt: true });
        var got = probe.contents && probe.contents.get ? probe.contents.get("k", true) : null;
        if (!got || typeof got.value !== "bigint" || got.value !== want) {
          throw fail("YAML reads \"" + t + "\" as " + (got ? typeof got.value : "nothing") + ", not an integer. Quote it as a string, or edit in Raw source.");
        }
        return t;
      }
      if (floatRe) {
        var flat = t.replace(/_/g, "");
        if (!exactFloat(flat)) throw fail("Number \"" + t + "\" loses precision as a YAML float. Quote it as a string, or edit in Raw source.");
        var check = Y.parseDocument("k: " + t, { intAsBigInt: true });
        var scalar = check.contents && check.contents.get ? check.contents.get("k", true) : null;
        if (!scalar || typeof scalar.value !== "number") {
          throw fail("YAML reads \"" + t + "\" as " + (scalar ? typeof scalar.value : "nothing") + ", not a float. Quote it as a string, or edit in Raw source.");
        }
        return t;
      }
      throw fail("Text \"" + t + "\" is not a YAML number. Quote it as a string, or edit in Raw source.");
    }

    return model;
  }

  var api = { parse: parse };
  if (typeof window !== "undefined") window.OwnerDefinitionModel = api;
  if (typeof globalThis !== "undefined" && !globalThis.OwnerDefinitionModel) globalThis.OwnerDefinitionModel = api;
  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) module.exports = api;
})();
