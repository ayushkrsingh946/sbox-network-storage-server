"use strict";
(function () {
  var collator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });
  function compareValues(left, right) {
    left = left.trim(); right = right.trim();
    // IDs and counters can exceed Number's precision. Compare integer text without rounding it.
    if (/^-?\d+$/.test(left) && /^-?\d+$/.test(right)) {
      var a = BigInt(left), b = BigInt(right);
      return a < b ? -1 : a > b ? 1 : 0;
    }
    var aMetric = /^(-?\d+(?:\.\d+)?)\s*(ms|s|B|KB|MB|GB)?$/.exec(left);
    var bMetric = /^(-?\d+(?:\.\d+)?)\s*(ms|s|B|KB|MB|GB)?$/.exec(right);
    var units = { "": ["number", 1], ms: ["time", 1], s: ["time", 1000], B: ["bytes", 1], KB: ["bytes", 1024], MB: ["bytes", 1048576], GB: ["bytes", 1073741824] };
    if (aMetric && bMetric) {
      var au = units[aMetric[2] || ""], bu = units[bMetric[2] || ""];
      if (au[0] === bu[0]) {
        var av = Number(aMetric[1]) * au[1], bv = Number(bMetric[1]) * bu[1];
        if (Number.isFinite(av) && Number.isFinite(bv)) return av < bv ? -1 : av > bv ? 1 : 0;
      }
    }
    return collator.compare(left, right);
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { compareValues: compareValues };
  if (typeof document === "undefined") return;
  document.querySelectorAll("table[data-sortable]").forEach(function (table) {
    var body = table.tBodies[0];
    if (!table.tHead || !body || !body.rows.length) return;
    var rows = Array.from(body.rows), original = new Map(rows.map(function (row, index) { return [row, index]; }));
    if (rows.some(function (row) { return Array.from(row.cells).some(function (cell) { return cell.colSpan > 1 || cell.rowSpan > 1; }); })) return;
    var headers = Array.from(table.tHead.rows[0].cells), active = null, direction = 1;
    var help = document.createElement("p"); help.className = "table-sort-help";
    help.textContent = table.dataset.sortScope === "page" ? "Click a column label to sort this page only." : "Click a column label to sort the visible rows.";
    table.parentElement.before(help);
    var announcement = document.createElement("span"); announcement.className = "sr-only"; announcement.setAttribute("role", "status"); table.after(announcement);
    headers.forEach(function (header, index) {
      var label = header.textContent.trim();
      if (header.hasAttribute("data-no-sort") || !label || /^(Actions|Preview|Permissions)$/i.test(label) || header.querySelector("a, button, input")) return;
      var button = document.createElement("button"), marker = document.createElement("span");
      button.type = "button"; button.className = "table-sort";
      marker.className = "table-sort-marker"; marker.setAttribute("aria-hidden", "true"); marker.textContent = "↕";
      button.append(document.createTextNode(label), marker); header.replaceChildren(button);
      header.scope = "col"; header.setAttribute("aria-sort", "none"); button.setAttribute("aria-label", "Sort " + label + " ascending");
      function value(row) { var cell = row.cells[index]; return cell ? cell.dataset.sortValue || cell.textContent.trim() : ""; }
      button.addEventListener("click", function () {
        direction = active === header ? -direction : 1; active = header;
        headers.forEach(function (item) {
          var control = item.querySelector(".table-sort"); if (!control) return;
          var selected = item === header;
          item.setAttribute("aria-sort", selected ? (direction === 1 ? "ascending" : "descending") : "none");
          control.querySelector(".table-sort-marker").textContent = selected ? (direction === 1 ? "↑" : "↓") : "↕";
          control.setAttribute("aria-label", "Sort " + control.firstChild.textContent + (selected && direction === 1 ? " descending" : " ascending"));
        });
        rows.sort(function (a, b) {
          var left = value(a), right = value(b);
          if (!left || !right) return left === right ? original.get(a) - original.get(b) : !left ? 1 : -1;
          return compareValues(left, right) * direction || original.get(a) - original.get(b);
        });
        rows.forEach(function (row) { body.appendChild(row); });
        announcement.textContent = label + ", " + (direction === 1 ? "ascending" : "descending") + ". Only the displayed rows were reordered.";
      });
    });
  });
})();
