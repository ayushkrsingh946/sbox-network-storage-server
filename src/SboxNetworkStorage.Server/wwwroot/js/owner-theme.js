"use strict";
(function () {
  var themes = ["system", "dark", "light", "mint", "slate", "warm"];
  var preference = "system";
  var media = window.matchMedia("(prefers-color-scheme: light)");
  try { var saved = localStorage.getItem("sbox-ns-theme"); if (themes.indexOf(saved) !== -1) preference = saved; } catch (_) {}
  function apply() {
    document.documentElement.dataset.theme = preference === "system" ? (media.matches ? "light" : "dark") : preference;
  }
  apply();
  media.addEventListener("change", function () { if (preference === "system") apply(); });
  document.addEventListener("DOMContentLoaded", function () {
    var picker = document.getElementById("theme-choice");
    if (!picker) return;
    picker.value = preference;
    picker.addEventListener("change", function () {
      if (themes.indexOf(picker.value) === -1) return;
      preference = picker.value;
      try { localStorage.setItem("sbox-ns-theme", preference); } catch (_) {}
      apply();
    });
  });
  document.addEventListener("DOMContentLoaded", function () {
    var toggle = document.getElementById("nav-toggle");
    var sidebar = document.getElementById("owner-nav");
    if (!toggle || !sidebar) return;
    function close() {
      sidebar.classList.remove("open");
      toggle.setAttribute("aria-expanded", "false");
    }
    toggle.addEventListener("click", function () {
      var open = sidebar.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
    sidebar.addEventListener("click", function (event) {
      if (event.target.closest("a") && window.innerWidth <= 760) close();
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && sidebar.classList.contains("open")) { close(); toggle.focus(); }
    });
  });
  document.addEventListener("DOMContentLoaded", function () {
    var strip = document.querySelector(".alert-strip");
    if (!strip) return;
    var sessionHidden = {};
    try { sessionHidden = JSON.parse(sessionStorage.getItem("sbox-ns-alerts") || "{}"); } catch (_) {}
    strip.querySelectorAll("[data-alert]").forEach(function (item) {
      var key = item.getAttribute("data-alert");
      var hidden = key === "update"
        ? (function () { try { return localStorage.getItem("sbox-ns-update-seen") === item.getAttribute("data-version"); } catch (_) { return false; } })()
        : !!sessionHidden[key];
      if (hidden) item.remove();
    });
    if (!strip.querySelector("[data-alert]")) { strip.remove(); return; }
    strip.querySelectorAll("[data-alert-close]").forEach(function (button) {
      button.addEventListener("click", function () {
        var item = button.closest("[data-alert]");
        if (!item) return;
        var key = item.getAttribute("data-alert");
        try {
          if (key === "update") localStorage.setItem("sbox-ns-update-seen", item.getAttribute("data-version") || "");
          else { sessionHidden[key] = true; sessionStorage.setItem("sbox-ns-alerts", JSON.stringify(sessionHidden)); }
        } catch (_) {}
        item.remove();
        if (!strip.querySelector("[data-alert]")) strip.remove();
      });
    });
  });
})();
