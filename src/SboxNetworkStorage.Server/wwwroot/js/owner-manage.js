"use strict";
// Owner management pages: endpoint test helpers, copy buttons, project filter
// and color swatches. Destructive confirmations live in owner-dialog.js, which
// is loaded before this file. Nothing here calls window.confirm.
(function () {
  // Pure helper: server-side ?q= matching mirrored for client enhancement.
  // Matches project name or id, case-insensitive, on any substring.
  function matchesProject(name, id, query) {
    var q = (query || "").trim().toLowerCase();
    if (!q) return true;
    return (name || "").toLowerCase().indexOf(q) !== -1 || (id || "").toLowerCase().indexOf(q) !== -1;
  }

  // Test hook only: the browser never defines module, so this is inert in production.
  if (typeof module !== "undefined" && module.exports) module.exports = { matchesProject: matchesProject };

  if (typeof document === "undefined" || typeof window === "undefined") return;

  // Endpoint tests: switching endpoints swaps in that endpoint's input skeleton,
  // unless the owner already edited the input.
  var endpoint = document.querySelector("select[data-test-endpoint]");
  var input = document.querySelector("textarea[data-test-input]");
  if (endpoint && input) {
    var template = function () {
      var option = endpoint.options[endpoint.selectedIndex];
      return option ? option.getAttribute("data-template") || "{}" : "{}";
    };
    var lastTemplate = template();
    endpoint.addEventListener("change", function () {
      if (input.value.trim() === lastTemplate.trim() || input.value.trim() === "" || input.value.trim() === "{}") {
        input.value = template();
        input.dispatchEvent(new Event("change", { bubbles: true }));
      }
      lastTemplate = template();
    });
  }

  // Copy buttons sit right after the snippet they copy. Without the clipboard API (plain HTTP on a
  // remote address), or when it is refused, the snippet is selected so the owner can press Ctrl+C.
  function flash(button, text) {
    var label = button.getAttribute("data-label") || button.textContent;
    button.setAttribute("data-label", label);
    button.textContent = text;
    setTimeout(function () { button.textContent = label; }, 2500);
  }
  function select(source) {
    var range = document.createRange();
    range.selectNodeContents(source);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }
  document.querySelectorAll("[data-copy-previous]").forEach(function (button) {
    button.addEventListener("click", function () {
      var source = button.previousElementSibling;
      if (!source) return;
      var fallback = function () { select(source); flash(button, "Press Ctrl+C"); };
      if (!navigator.clipboard || !window.isSecureContext) { fallback(); return; }
      navigator.clipboard.writeText(source.textContent).then(function () { flash(button, "Copied"); }, fallback);
    });
  });

  // Project filter enhancement (server renders data-enhance only above 8
  // projects). Without JavaScript the same form submits as GET ?q=.
  var filter = document.getElementById("project-filter");
  var grid = document.getElementById("project-grid");
  if (filter && grid && filter.getAttribute("data-enhance") === "true") {
    var cards = Array.prototype.slice.call(grid.querySelectorAll("[data-project-name]"));
    var emptyNote = document.getElementById("project-filter-empty");
    filter.addEventListener("input", function () {
      var visible = 0;
      cards.forEach(function (card) {
        var show = matchesProject(card.getAttribute("data-project-name"), card.getAttribute("data-project-id"), filter.value);
        card.hidden = !show;
        if (show) visible += 1;
      });
      if (emptyNote) emptyNote.hidden = visible !== 0;
    });
  }

  // New-resource dialog: validate the id, then load a pristine draft skeleton through
  // ?draft=<id>. The current editor is left untouched so editing one resource while
  // starting another cannot inherit the old hidden resource id. Navigation is a plain
  // location change, so the normal unsaved-source warning still applies.
  (function () {
    var confirm = document.getElementById("new-resource-confirm");
    if (!confirm) return;
    confirm.addEventListener("click", function () {
      var field = document.getElementById("new-resource-id");
      var status = document.getElementById("new-resource-status");
      var id = field ? field.value.trim() : "";
      if (!/^[A-Za-z0-9_-]{1,128}$/.test(id)) {
        if (status) status.textContent = "Use 1-128 letters, numbers, underscore or hyphen.";
        if (field) field.focus();
        return;
      }
      window.location.assign(window.location.pathname + "?draft=" + encodeURIComponent(id));
    });
    var field = document.getElementById("new-resource-id");
    if (field) field.addEventListener("keydown", function (event) {
      if (event.key === "Enter") { event.preventDefault(); confirm.click(); }
    });
  })();

  // Pinned projects float to the top of the dashboard grid. Pins live in
  // localStorage, so they never touch server data.
  var pinKey = "sbox-ns-pinned-projects";
  function pinnedIds() {
    try { return JSON.parse(localStorage.getItem(pinKey) || "[]"); } catch (e) { return []; }
  }
  function orderPinned() {
    var grid = document.querySelector(".project-grid");
    if (!grid) return;
    var pinned = pinnedIds();
    var columns = Array.prototype.slice.call(grid.children);
    columns.forEach(function (column) {
      var card = column.querySelector(".project-card");
      // The card is wrapped in a .project-cell carrying data-project-id; the
      // anchor itself holds no data attributes so it stays a plain link.
      var on = pinned.indexOf(column.getAttribute("data-project-id") || "") >= 0;
      if (card) card.classList.toggle("pinned", on);
      var button = column.querySelector("[data-pin-project]");
      if (button) button.setAttribute("aria-pressed", on ? "true" : "false");
    });
    columns.sort(function (a, b) {
      var pa = a.querySelector(".project-card.pinned") ? 1 : 0;
      var pb = b.querySelector(".project-card.pinned") ? 1 : 0;
      return pb - pa;
    });
    columns.forEach(function (column) { grid.appendChild(column); });
  }
  document.querySelectorAll("[data-pin-project]").forEach(function (button) {
    button.addEventListener("click", function () {
      var id = button.getAttribute("data-pin-project") || "";
      var pinned = pinnedIds().filter(function (entry) { return entry !== id; });
      if (pinned.length === pinnedIds().length) pinned.push(id);
      try { localStorage.setItem(pinKey, JSON.stringify(pinned)); } catch (e) {}
      orderPinned();
    });
  });
  orderPinned();

  // Inline style attributes are blocked by the panel CSP; color swatches are set through CSSOM.
  document.querySelectorAll(".manage-swatch[data-color]").forEach(function (swatch) {
    var color = swatch.getAttribute("data-color") || "";
    if (/^#?[0-9a-fA-F]{6}$/.test(color)) swatch.style.backgroundColor = color.charAt(0) === "#" ? color : "#" + color;
  });
})();
