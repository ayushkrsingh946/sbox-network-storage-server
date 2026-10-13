"""Generates the static owner-console redesign mockups (one HTML file per screen state)."""
import os, sys

OUT = sys.argv[1]

P = {
    "plus": '<path d="M8 3v10M3 8h10"/>',
    "more": '<circle cx="3.5" cy="8" r=".9" fill="currentColor"/><circle cx="8" cy="8" r=".9" fill="currentColor"/><circle cx="12.5" cy="8" r=".9" fill="currentColor"/>',
    "copy": '<rect x="5.5" y="5.5" width="8" height="8" rx="1.2"/><path d="M10.5 5.5V3.7c0-.7-.5-1.2-1.2-1.2H3.7c-.7 0-1.2.5-1.2 1.2v5.6c0 .7.5 1.2 1.2 1.2h1.8"/>',
    "download": '<path d="M8 2.5v8M4.8 7.5 8 10.7l3.2-3.2M3 13.5h10"/>',
    "upload": '<path d="M8 10.5v-8M4.8 5.5 8 2.3l3.2 3.2M3 13.5h10"/>',
    "gear": '<circle cx="8" cy="8" r="2.2"/><path d="M8 1.8v1.6M8 12.6v1.6M14.2 8h-1.6M3.4 8H1.8M12.4 3.6l-1.1 1.1M4.7 11.3l-1.1 1.1M12.4 12.4l-1.1-1.1M4.7 4.7 3.6 3.6"/>',
    "search": '<circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3.5 3.5"/>',
    "filter": '<path d="M2 3.5h12M4.5 8h7M7 12.5h2"/>',
    "columns": '<rect x="2" y="2.5" width="12" height="11" rx="1"/><path d="M6 2.5v11M10 2.5v11"/>',
    "chev": '<path d="m6 3.5 4.5 4.5L6 12.5"/>',
    "chevl": '<path d="M10 3.5 5.5 8l4.5 4.5"/>',
    "chevd": '<path d="m4 6 4 4 4-4"/>',
    "updown": '<path d="m5 6 3-3 3 3M5 10l3 3 3-3"/>',
    "x": '<path d="m4 4 8 8M12 4l-8 8"/>',
    "pencil": '<path d="M10.5 2.5 13.5 5.5 5.5 13.5H2.5v-3z"/>',
    "play": '<path d="M4.5 2.5v11l9-5.5z"/>',
    "trash": '<path d="M2.5 4h11M6 4V2.5h4V4M4 4l.7 9.5h6.6L12 4"/>',
    "key": '<circle cx="5" cy="11" r="2.5"/><path d="m6.8 9.2 6.7-6.7M11 4.5l1.8 1.8M9.5 6l1.3 1.3"/>',
    "check": '<path d="m3 8.5 3 3 7-7"/>',
    "help": '<circle cx="8" cy="8" r="6"/><path d="M6.3 6.2a1.8 1.8 0 1 1 2.4 1.7c-.5.2-.7.6-.7 1.1M8 11.3h.01"/>',
    "theme": '<circle cx="8" cy="8" r="5.5"/><path d="M8 2.5v11a5.5 5.5 0 0 0 0-11z" fill="currentColor"/>',
    "grid": '<rect x="2" y="2" width="5" height="5" rx="1"/><rect x="9" y="2" width="5" height="5" rx="1"/><rect x="2" y="9" width="5" height="5" rx="1"/><rect x="9" y="9" width="5" height="5" rx="1"/>',
    "home": '<path d="M2.5 7 8 2l5.5 5M4 6.5V14h8V6.5"/>',
    "database": '<ellipse cx="8" cy="3.5" rx="5.5" ry="2"/><path d="M2.5 3.5v9c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2v-9M2.5 8c0 1.1 2.5 2 5.5 2s5.5-.9 5.5-2"/>',
    "collection": '<rect x="2" y="3" width="12" height="10" rx="1"/><path d="M2 6.5h12M2 10h12M7 6.5V13"/>',
    "endpoint": '<path d="M8.5 1.5 3.5 9H7l-1 5.5L11.5 7H8z"/>',
    "workflow": '<circle cx="4" cy="4" r="1.8"/><circle cx="12" cy="4" r="1.8"/><circle cx="8" cy="12" r="1.8"/><path d="M5.5 5 6.8 10.4M10.5 5 9.2 10.4M5.8 4h4.4"/>',
    "query": '<circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3.5 3.5"/>',
    "values": '<rect x="1.5" y="5" width="13" height="7" rx="3.5"/><path d="M5 8v2M4 9h2M11 8.5h.01M12.5 10h.01"/>',
    "analytics": '<path d="M2.5 13.5h11M4.5 13V9.5M8 13V5.5M11.5 13v-3"/>',
    "logs": '<path d="M5.5 4h8M5.5 8h8M5.5 12h8M2.5 4h.01M2.5 8h.01M2.5 12h.01"/>',
    "errors": '<path d="M8 2 14.5 13.5h-13zM8 6.5v3.5M8 12h.01"/>',
    "usage": '<path d="M2.5 12.5a6 6 0 0 1 11 0M8 12.5 11 8"/>',
    "tests": '<path d="M4.5 2.5v11l9-5.5z"/>',
    "versions": '<circle cx="8" cy="8" r="5.5"/><path d="M8 5v3l2 1.5"/>',
    "clients": '<circle cx="6" cy="5" r="2.5"/><path d="M1.5 14v-2.5a4.5 4.5 0 0 1 9 0V14M11 3.5a2.5 2.5 0 0 1 0 5M13 10a4 4 0 0 1 1.5 3.5"/>',
    "limits": '<path d="M2.5 4h11M2.5 8h7M2.5 12h4"/>',
    "webhooks": '<path d="M14 2 7 9M14 2l-4.5 12-2.5-5-5-2.5z"/>',
    "pages": '<path d="M4 1.5h5.5L12.5 4.5v10h-8.5zM9.5 1.5v3h3M6 8h4.5M6 11h4.5"/>',
    "server": '<rect x="2" y="2.5" width="12" height="4.5" rx="1"/><rect x="2" y="9" width="12" height="4.5" rx="1"/><path d="M4.5 4.75h.01M4.5 11.25h.01"/>',
    "alerts": '<path d="M4 11V7a4 4 0 0 1 8 0v4l1.5 1.5h-11zM6.5 14h3"/>',
    "shield": '<path d="M8 1.5 13.5 3.5v4c0 3.5-2.5 5.8-5.5 7-3-1.2-5.5-3.5-5.5-7v-4z"/>',
    "warn": '<path d="M8 2 14.5 13.5h-13zM8 6.5v3.5M8 12h.01"/>',
    "ext": '<path d="M9.5 2.5h4v4M13.5 2.5 7 9M11.5 9.5v4h-9v-9h4"/>',
    "list": '<path d="M5.5 4h8M5.5 8h8M5.5 12h8M2.5 4h.01M2.5 8h.01M2.5 12h.01"/>',
    "braces": '<path d="M5.5 2.5c-1.5 0-2 .6-2 1.8v1.4c0 .9-.5 1.6-1.5 2.3 1 .7 1.5 1.4 1.5 2.3v1.4c0 1.2.5 1.8 2 1.8M10.5 2.5c1.5 0 2 .6 2 1.8v1.4c0 .9.5 1.6 1.5 2.3-1 .7-1.5 1.4-1.5 2.3v1.4c0 1.2-.5 1.8-2 1.8"/>',
    "dup": '<rect x="5.5" y="5.5" width="8" height="8" rx="1.2"/><path d="M10.5 5.5V3.7c0-.7-.5-1.2-1.2-1.2H3.7c-.7 0-1.2.5-1.2 1.2v5.6c0 .7.5 1.2 1.2 1.2h1.8M9.5 8v3M8 9.5h3"/>',
    "user": '<circle cx="8" cy="5.5" r="2.8"/><path d="M2.5 14a5.5 5.5 0 0 1 11 0"/>',
    "dots": '<circle cx="3.5" cy="8" r="1.1" fill="currentColor"/><circle cx="8" cy="8" r="1.1" fill="currentColor"/><circle cx="12.5" cy="8" r="1.1" fill="currentColor"/>',
}


def i(name, cls="i"):
    return f'<svg class="{cls}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">{P[name]}</svg>'


def chip(text, title=None):
    return f'<span class="chip" title="{title or text}">{text}<span class="cp">{i("copy")}</span></span>'


PROJECT_NAV = [
    (None, [("Overview", "home", "overview"), ("Data", "database", "data")]),
    ("Build", [("Collections", "collection", "collections", "7"), ("Endpoints", "endpoint", "endpoints", "11"), ("Workflows", "workflow", "workflows", "3"), ("Queries", "query", "queries", "2"), ("Game values", "values", "values", "")]),
    ("Observe", [("Requests", "logs", "requests"), ("Errors", "errors", "errors", "2"), ("Analytics", "analytics", "analytics"), ("Usage", "usage", "usage")]),
    ("Manage", [("API keys", "key", "keys", "2"), ("Endpoint tests", "tests", "tests"), ("Versions", "versions", "versions"), ("Clients", "clients", "clients"), ("Rate limits", "limits", "limits"), ("Webhooks", "webhooks", "webhooks"), ("Pages", "pages", "pages"), ("Settings", "gear", "settings")]),
]
WORKSPACE_NAV = [(None, [("Projects", "grid", "projects", "3"), ("Server", "server", "server"), ("Alerts", "alerts", "alerts"), ("Security", "shield", "security")])]


def sidebar(current, project=True):
    out = ['<aside class="side">']
    groups = PROJECT_NAV if project else WORKSPACE_NAV
    if project:
        out.append(f'<button class="switcher"><span class="pav sm">IS</span><span class="nm">Island survival demo</span><span class="chev">{i("updown")}</span></button>')
    for label, items in groups:
        if label:
            out.append(f'<div class="grp">{label}</div>')
        for item in items:
            name, icon, key = item[0], item[1], item[2]
            count = item[3] if len(item) > 3 else ""
            cur = ' aria-current="page"' if key == current else ""
            c = f'<span class="count">{count}</span>' if count else ""
            out.append(f'<a class="nav" href="#"{cur}>{i(icon)}{name}{c}</a>')
    out.append("</aside>")
    return "\n".join(out)


def topbar(crumbs):
    parts = []
    for n, c in enumerate(crumbs):
        if n:
            parts.append('<span class="sep">/</span>')
        parts.append(f'<span class="here">{c}</span>' if n == len(crumbs) - 1 else f'<a href="#">{c}</a>')
    return f'''<header class="topbar"><a class="brand" href="#"><img src="brand.svg" alt="">Network Storage</a>
<nav class="crumbs" aria-label="Breadcrumb">{"".join(parts)}</nav><span class="spacer"></span>
<div class="top-actions"><span class="kbd">{i("search")}Jump to<kbd>Ctrl K</kbd></span>
<button class="icon-btn" title="Guides">{i("help")}</button><button class="icon-btn" title="Theme">{i("theme")}</button><span class="avatar-btn" title="Owner menu">OW</span></div></header>'''


def mobile_head(back, title, right=""):
    b = f'<a class="back" href="#">{i("chevl")}{back}</a>' if back else ""
    return f'<div class="mobile-head">{b}<span class="mt-title">{title}</span><span class="r">{right}</span></div>'


def tabbar(on, workspace=False):
    tabs = [("Projects", "grid"), ("Server", "server"), ("Alerts", "alerts"), ("Security", "shield"), ("More", "dots")] if workspace else [("Overview", "home"), ("Data", "database"), ("Build", "endpoint"), ("Activity", "analytics"), ("More", "dots")]
    return '<nav class="tabbar">' + "".join(f'<a class="{"on" if t == on else ""}" href="#">{i(ic)}{t}</a>' for t, ic in tabs) + "</nav>"


def page(name, title, body, current, crumbs, project=True, extra="", mhead="", tab="Overview", theme="dark"):
    html = f'''<!doctype html><html lang="en" data-theme="{theme}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title><link rel="stylesheet" href="console.css"></head><body>
{topbar(crumbs)}
<div class="app">{sidebar(current, project)}
<main>{mhead}{body}</main></div>
{tabbar(tab) if project else tabbar("Projects", workspace=True)}
{extra}
</body></html>'''
    with open(os.path.join(OUT, name), "w") as f:
        f.write(html)


# ---------- Projects ----------
projects = [
    ("IS", "", "Island survival demo", "Generated sample data", "proj_eb58f3…a8979", "Enabled", "ok", "Player-hosted", "42", "2 min ago"),
    ("OS", "c2", "Orbit Survival", "Production", "proj_aa68ce…66f56", "Enabled", "ok", "Dedicated", "118", "14 s ago"),
    ("WS", "c3", "Workshop Sandbox", "Development collections and endpoint experiments", "proj_ebae25…f4278", "Disabled", "", "Hybrid", "Not synced", "6 days ago"),
]


def projects_body(menu=False):
    rows = []
    for av, c, name, desc, pid, st, stc, prof, rev, last in projects:
        rows.append(f'''<tr><td><div class="name"><span class="pav sm {c}">{av}</span><span><b>{name}</b><span class="sub">{desc}</span></span></div></td>
<td>{chip(pid)}</td><td><span class="tag {stc}">{st}</span></td><td class="mut">{prof}</td><td class="num">{rev}</td><td class="mut">{last}</td><td class="w-act"><span class="rowmenu">{i("chev")}</span></td>
<td class="m-main"><div class="mr"><span class="pav sm {c}">{av}</span><span class="grow"><span class="l1">{name}</span><span class="l2">{pid} · {st}</span></span><span class="rt">{last}</span>{i("chev")}</div></td></tr>''')
    for r in range(len(rows)):
        rows[r] = rows[r].replace("<tr>", '<tr class="m-row">', 1).replace("<td>", '<td class="m-hide">').replace('<td class="num">', '<td class="num m-hide">').replace('<td class="mut">', '<td class="mut m-hide">').replace('<td class="w-act">', '<td class="w-act m-hide">')
    menu_html = ""
    if menu:
        menu_html = f'''<div class="menu" style="top:96px; right:24px">
<a href="#">{i("upload")}Import project archive</a><a href="#">{i("download")}Export server</a><hr><a href="#">{i("help")}Connect a game guide</a><a href="#">{i("ext")}Coding agent setup</a></div>'''
    return f'''<div class="head"><div class="titles"><h1>Projects</h1><div class="meta">3 projects<span class="dot">·</span>2 enabled<span class="dot">·</span>1,297 records</div></div>
<div class="tools"><button class="btn primary">{i("plus")}New project</button><button class="icon-btn bordered {"on" if menu else ""}" title="More">{i("more")}</button></div></div>
<section class="panel"><div class="tbl-wrap"><table class="t tall"><thead><tr><th>Project</th><th>Project ID</th><th>Status</th><th>Hosting</th><th class="num">Live rev</th><th>Last request</th><th></th></tr></thead><tbody>
{"".join(rows)}</tbody></table></div></section>{menu_html}'''


mh_projects = mobile_head("", "Projects", f'<button class="icon-btn">{i("more")}</button><button class="icon-btn">{i("plus")}</button>')
page("projects.html", "Projects", projects_body(), "projects", ["Projects"], project=False, mhead=mh_projects)
page("projects-menu.html", "Projects", projects_body(menu=True), "projects", ["Projects"], project=False, mhead=mh_projects)

new_project = f'''<div class="scrim"></div><div class="modal" role="dialog"><div class="modal-h"><h2>New project</h2><span class="spacer"></span><button class="icon-btn">{i("x")}</button></div>
<div class="modal-b"><label class="field"><span>Name</span><div class="input focus">Harbor Wars<span class="caret"></span></div></label>
<div class="field"><span>Hosting</span><div class="choice"><div class="on"><b>Player-hosted</b><span>Public key only</span></div><div><b>Dedicated</b><span>Secret key</span></div><div><b>Both</b><span>Hybrid</span></div></div></div>
<p>Creates a public game key and requires s&amp;box auth. Change either in Settings.</p></div>
<div class="modal-f"><button class="btn ghost">Cancel</button><button class="btn primary">Create project</button></div></div>'''
page("projects-new.html", "Projects", projects_body(), "projects", ["Projects"], project=False, extra=new_project, mhead=mh_projects)

# ---------- Project overview ----------
cfg = '<span class="f">NetworkStorage.Configure</span>( <span class="s">"proj_eb58f3a13d02e6f5620a8979"</span>, <span class="s">"sbox_ns_7Hq2…Lk9"</span>, <span class="s">"https://island.sboxns.com"</span> );'
overview = f'''<div class="head"><span class="pav">IS</span><div class="titles"><h1>Island survival demo</h1>
<div class="meta">{chip("proj_eb58f3a13d02e6f5620a8979")}<span class="tag ok">Enabled</span><span class="tag">Player-hosted</span><span class="tag">Live rev 42</span></div></div>
<div class="tools"><button class="icon-btn bordered" title="Settings">{i("gear")}</button><button class="icon-btn bordered" title="More">{i("more")}</button><button class="btn primary">{i("database")}Browse data</button></div></div>
<div class="stats">
<a class="stat" href="#"><div class="k">{i("collection")}Collections</div><div class="v">7</div></a>
<a class="stat" href="#"><div class="k">{i("endpoint")}Endpoints</div><div class="v">11</div></a>
<a class="stat" href="#"><div class="k">{i("database")}Records</div><div class="v">1,297<small>2.4 MB</small></div></a>
<a class="stat" href="#"><div class="k">{i("logs")}Requests 24h</div><div class="v">300<small>39 ms avg</small></div></a>
<a class="stat" href="#"><div class="k">{i("errors")}Errors 24h</div><div class="v bad">2<small>0.7%</small></div></a></div>
<div class="cols">
<section class="panel"><div class="panel-h"><h2>Connect</h2><div class="tools"><span class="seg"><span class="on">C#</span><span>Editor</span></span></div></div>
<div class="panel-b"><div class="code"><code>{cfg}</code><button class="icon-btn" title="Copy">{i("copy")}</button></div>
<dl class="kv"><dt>Project ID</dt><dd>{chip("proj_eb58f3a13d02e6f5620a8979")}</dd><dt>Public key</dt><dd>{chip("sbox_ns_7Hq2c0dVwz1Lk9")}</dd><dt>Base URL</dt><dd>{chip("https://island.sboxns.com")}</dd><dt>Secret key</dt><dd><a class="btn" href="#">{i("key")}Create for editor sync</a></dd></dl></div></section>
<section class="panel"><div class="panel-h"><h2>Health</h2><div class="tools"><a class="btn ghost" href="#">Run check</a></div></div>
<ul class="list">
<li class="link"><span class="lbl">Public key</span><span class="val"><span class="ok">Ready</span>{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Definitions</span><span class="val">18 synced{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Last request<span class="sub">POST /v3/endpoints/save-progress · 200</span></span><span class="val">2 min ago{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Rejected 24h</span><span class="val"><span class="ok">None</span>{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Authority check</span><span class="val"><span class="warn">1 finding</span>{i("chev", "i chev")}</span></li></ul></section></div>
<section class="panel mt"><div class="panel-h"><h2>Recent requests</h2><div class="tools"><a class="btn ghost" href="#">All requests{i("chev")}</a></div></div>
<div class="tbl-wrap"><table class="t"><thead><tr><th>Time</th><th>Method</th><th>Path</th><th>Player</th><th class="num">Status</th><th class="num">Duration</th></tr></thead><tbody>
<tr><td class="mut">2 min ago</td><td><span class="tag post">POST</span></td><td class="key">/v3/endpoints/save-progress</td><td class="key">76561198041327705</td><td class="num">200</td><td class="num">38 ms</td></tr>
<tr><td class="mut">2 min ago</td><td><span class="tag get">GET</span></td><td class="key">/v3/revision-init</td><td class="key">76561198041327705</td><td class="num">200</td><td class="num">9 ms</td></tr>
<tr><td class="mut">5 min ago</td><td><span class="tag post">POST</span></td><td class="key">/v3/endpoints/save-progress</td><td class="key">76561198112094471</td><td class="num" style="color:var(--danger)">409</td><td class="num">41 ms</td></tr>
<tr><td class="mut">7 min ago</td><td><span class="tag post">POST</span></td><td class="key">/v3/endpoints/inventory_craft</td><td class="key">76561198112094471</td><td class="num">200</td><td class="num">52 ms</td></tr></tbody></table></div></section>'''
mh_ov = mobile_head("Projects", "", f'<button class="icon-btn">{i("gear")}</button><button class="icon-btn">{i("more")}</button>')
page("project.html", "Island survival demo", overview, "overview", ["Projects", "Island survival demo"], mhead=mh_ov, tab="Overview")

# ---------- Collection ----------
players = [
    ("76561198041327705", "Scout", 7, 120, "1/2", 14, "2 min ago", 194),
    ("76561198112094471", "Builder", 10, 207, "1/2", 9, "5 min ago", 203),
    ("76561198003318820", "Miner", 13, 294, "1/2", 31, "11 min ago", 198),
    ("76561198254871093", "Ranger", 16, 381, "2/2", 22, "38 min ago", 202),
    ("76561198090662318", "Fisher", 19, 468, "2/2", 40, "1 h ago", 197),
    ("76561198176503342", "Captain", 22, 555, "2/2", 57, "3 h ago", 205),
    ("76561198012784410", "Smith", 4, 61, "0/2", 3, "5 h ago", 188),
    ("76561198301559826", "Medic", 11, 240, "1/2", 18, "Yesterday", 199),
    ("76561198067213954", "Trader", 27, 1902, "2/2", 112, "Yesterday", 211),
    ("76561198229940117", "Hunter", 8, 133, "1/2", 11, "2 days ago", 196),
]


def collection_body(selected=(), open_row=None, menu_row=None):
    rows = []
    for n, (key, dn, lvl, coins, q, ver, ch, size) in enumerate(players):
        cls = "sel" if n in selected else ("open" if n == open_row else "")
        cb = f'<span class="cb on">{i("check")}</span>' if n in selected else '<span class="cb"></span>'
        rows.append(f'''<tr class="m-row {cls}"><td class="w-cb m-hide">{cb}</td><td class="key m-hide">{key}</td><td class="m-hide">{dn}</td><td class="num m-hide">{lvl}</td><td class="num m-hide">{coins:,}</td><td class="num m-hide">{q}</td><td class="num m-hide mut">{ver}</td><td class="m-hide mut">{ch}</td><td class="num m-hide mut">{size} B</td><td class="w-act m-hide"><span class="rowmenu">{i("more")}</span></td>
<td class="m-main"><div class="mr"><span class="grow"><span class="l1">{dn}</span><span class="l2 mono">{key}</span></span><span class="rt">Lv {lvl} · {coins:,}<br>{ch}</span>{i("chev")}</div></td></tr>''')
    allcb = '<span class="cb mixed"></span>' if selected else '<span class="cb"></span>'
    if selected:
        bar = f'''<div class="bar selbar"><span class="cb mixed"></span><strong>{len(selected)} selected</strong><span class="sep"></span>
<button class="btn">{i("download")}Export JSON</button><button class="btn">{i("dup")}Copy keys</button><button class="btn danger">{i("trash")}Delete</button><span class="spacer"></span><button class="btn ghost">Clear</button></div>'''
    else:
        bar = f'''<div class="bar"><div class="search">{i("search")}<span>Filter by key prefix</span></div><button class="btn">{i("filter")}<span class="hide-m">Filter</span></button>
<span class="seg hide-m"><span class="on">{i("list")}Table</span><span>{i("braces")}JSON</span></span><span class="spacer hide-m"></span>
<button class="icon-btn hide-m" title="Columns">{i("columns")}</button><button class="icon-btn hide-m" title="Export collection">{i("download")}</button></div>'''
    menu_html = ""
    if menu_row is not None:
        top = 222 + 38 * menu_row
        menu_html = f'''<div class="menu" style="top:{top}px; right:34px"><a href="#" class="hl">{i("list")}Open<span class="sc">Enter</span></a><a href="#">{i("pencil")}Edit JSON<span class="sc">E</span></a><a href="#">{i("copy")}Copy key</a><a href="#">{i("play")}Test endpoint as player</a><a href="#">{i("user")}All records of player</a><hr><a href="#" class="danger">{i("trash")}Delete record…</a></div>'''
    return f'''<div class="head"><div class="titles"><h1>Player progress</h1>
<div class="meta">{chip("players")}<span class="tag">Per-player</span><span>1,284 records</span><span class="dot">·</span><span>247 KB</span><span class="sizebar" title="247 KB of 1 MB soft budget"><i style="width:24%"></i></span><span class="dot">·</span><span>5 fields</span><span class="dot">·</span><span>Last write 2 min ago</span></div></div>
<div class="tools"><button class="icon-btn bordered" title="Schema">{i("collection")}</button><button class="icon-btn bordered" title="More">{i("more")}</button><button class="btn primary">{i("plus")}New record</button></div></div>
<section class="panel">{bar}
<div class="tbl-wrap"><table class="t"><thead><tr><th class="w-cb">{allcb}</th><th><span class="sort">Key{i("chevd")}</span></th><th>displayName</th><th class="num">level</th><th class="num">coins</th><th class="num">quests</th><th class="num">Ver</th><th>Changed</th><th class="num">Size</th><th></th></tr></thead><tbody>
{"".join(rows)}</tbody></table></div>
<div class="foot"><span>1-10 of 1,284</span><span class="hide-m">·</span><span class="hide-m pp">10 per page {i("chevd")}</span><div class="pg"><button class="icon-btn">{i("chevl")}</button><span>1 / 129</span><button class="icon-btn">{i("chev")}</button></div></div></section>{menu_html}'''


mh_col = mobile_head("Data", "Player progress", f'<button class="icon-btn">{i("more")}</button><button class="icon-btn">{i("plus")}</button>')
crumbs_col = ["Projects", "Island survival demo", "Data", "Player progress"]
page("collection.html", "Player progress", collection_body(), "data", crumbs_col, mhead=mh_col, tab="Data")
page("collection-light.html", "Player progress", collection_body(), "data", crumbs_col, mhead=mh_col, tab="Data", theme="light")
page("collection-selected.html", "Player progress", collection_body(selected=(1, 2, 4)), "data", crumbs_col, mhead=mh_col, tab="Data")
page("collection-menu.html", "Player progress", collection_body(open_row=1, menu_row=1), "data", crumbs_col, mhead=mh_col, tab="Data")

record_sheet = f'''<div class="scrim" style="left:0"></div><aside class="sheet" role="dialog"><div class="sheet-h"><button class="icon-btn m-only" style="display:none">{i("chevl")}</button><div style="min-width:0"><h2>Builder</h2><div class="meta">{chip("76561198112094471")}<span>v9 · 203 B · 5 min ago</span></div></div><span class="spacer"></span><button class="icon-btn" title="Previous">{i("chevl")}</button><button class="icon-btn" title="Next">{i("chev")}</button><button class="icon-btn" title="Close">{i("x")}</button></div>
<div class="sheet-b"><span class="seg"><span class="on">{i("list")}Fields</span><span>{i("braces")}JSON</span></span>
<section class="panel m-keep mt"><ul class="list">
<li><span class="lbl">displayName</span><span class="val" style="color:var(--text)">Demo Builder</span></li>
<li><span class="lbl">level</span><span class="val" style="color:var(--text)">10</span></li>
<li><span class="lbl">coins</span><span class="val" style="color:var(--text)">207</span></li>
<li class="link"><span class="lbl">position</span><span class="val"><code>x 12.5 · y 0 · z -4.25</code>{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">quests</span><span class="val">2 items{i("chev", "i chev")}</span></li>
<li><span class="lbl">sampleData</span><span class="val" style="color:var(--text)">true</span></li></ul></section>
<div class="group-t">Player</div>
<section class="panel m-keep"><ul class="list">
<li class="link"><span class="lbl">Other records of this player</span><span class="val">3{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Recent requests</span><span class="val">12 today{i("chev", "i chev")}</span></li></ul></section></div>
<div class="sheet-f"><button class="btn primary">{i("pencil")}Edit</button><button class="btn">{i("play")}Test as player</button><span class="spacer"></span><button class="icon-btn" title="Download JSON">{i("download")}</button><button class="icon-btn" title="More">{i("more")}</button></div></aside>'''
page("record.html", "Player progress", collection_body(open_row=1), "data", crumbs_col, extra=record_sheet, mhead=mh_col, tab="Data")

delete_modal = f'''<div class="scrim"></div><div class="modal" role="dialog"><div class="modal-h"><h2>Delete record</h2></div>
<div class="modal-b"><p>Writes a tombstone for <code>76561198112094471</code> in <code>players</code>. The game sees the record as missing. Version-checked and audit-logged.</p>
<label class="field"><span>Type the key to confirm</span><div class="input focus">7656119811209<span class="caret"></span></div></label></div>
<div class="modal-f"><button class="btn ghost">Cancel</button><button class="btn danger">Delete record</button></div></div>'''
page("record-delete.html", "Player progress", collection_body(open_row=1), "data", crumbs_col, extra=delete_modal, mhead=mh_col, tab="Data")

# Mobile record page (full screen, not a sheet)
record_m = f'''<div class="head"><div class="titles"><h1>Builder</h1><div class="meta">{chip("76561198112094471")}<span>v9 · 203 B · 5 min ago</span></div></div></div>
<span class="seg"><span class="on">{i("list")}Fields</span><span>{i("braces")}JSON</span></span>
<section class="panel m-keep mt"><ul class="list">
<li><span class="lbl">displayName</span><span class="val" style="color:var(--text)">Demo Builder</span></li>
<li><span class="lbl">level</span><span class="val" style="color:var(--text)">10</span></li>
<li><span class="lbl">coins</span><span class="val" style="color:var(--text)">207</span></li>
<li class="link"><span class="lbl">position</span><span class="val"><code>12.5, 0, -4.25</code>{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">quests</span><span class="val">2 items{i("chev", "i chev")}</span></li>
<li><span class="lbl">sampleData</span><span class="val" style="color:var(--text)">true</span></li></ul></section>
<div class="group-t">Actions</div>
<section class="panel m-keep"><ul class="list">
<li class="link"><span class="lbl">Test endpoint as this player</span><span class="val">{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Other records of this player</span><span class="val">3{i("chev", "i chev")}</span></li>
<li class="link"><span class="lbl">Download JSON</span><span class="val">{i("download")}</span></li>
<li class="link danger"><span class="lbl">Delete record…</span></li></ul></section>'''
mh_rec = mobile_head("Players", "", f'<button class="icon-btn" style="width:auto;padding:0 8px;font-weight:600">Edit</button>')
page("record-mobile.html", "Builder", record_m, "data", crumbs_col + ["76561198112094471"], mhead=mh_rec, tab="Data")

# ---------- Endpoints ----------
eps = [
    ("save-progress", "POST", True, 1, "Public", 300, 2, "0.7%", "39.2 ms"),
    ("economy_earn", "POST", True, 2, "Public", 0, 0, None, None),
    ("economy_get_balance", "GET", True, 1, "Public", 0, 0, None, None),
    ("economy_spend", "POST", True, 4, "Public", 0, 0, None, None),
    ("inventory_consume", "POST", True, 4, "Public", 0, 0, None, None),
    ("inventory_craft", "POST", True, 4, "Public", 0, 0, None, None),
    ("inventory_get", "GET", True, 1, "Public", 0, 0, None, None),
    ("inventory_grant", "POST", True, 1, "Secret", 0, 0, None, None),
    ("leaderboards_submit", "POST", True, 4, "Public", 0, 0, None, None),
    ("progression_get_level", "GET", True, 1, "Public", 0, 0, None, None),
    ("progression_grant_xp", "POST", False, 5, "Public", 0, 0, None, None),
]
er = []
for name, m, en, steps, acc, calls, errs, rate, avg in eps:
    z = lambda v: f'<span class="zero">{v}</span>'
    er.append(f'''<tr class="m-row"><td class="w-cb m-hide"><span class="cb"></span></td><td class="key m-hide">{name}</td><td class="m-hide"><span class="tag {m.lower()}">{m}</span></td><td class="m-hide"><span class="tag {"ok" if en else ""}">{"Enabled" if en else "Disabled"}</span></td><td class="num m-hide">{steps}</td><td class="m-hide mut">{acc}</td><td class="num m-hide">{calls if calls else z(0)}</td><td class="num m-hide">{errs if errs else z(0)}</td><td class="num m-hide">{rate or z("Never")}</td><td class="num m-hide">{avg or z("Never")}</td><td class="w-act m-hide"><span class="rowmenu">{i("more")}</span></td>
<td class="m-main"><div class="mr"><span class="tag {m.lower()}">{m}</span><span class="grow"><span class="l1 mono">{name}</span><span class="l2">{steps} steps · {acc}</span></span><span class="rt">{calls} calls</span>{i("chev")}</div></td></tr>''')
endpoints = f'''<div class="head"><div class="titles"><h1>Endpoints</h1><div class="meta">11 endpoints<span class="dot">·</span>10 enabled<span class="dot">·</span>300 calls this month<span class="dot">·</span>0.7% errors</div></div>
<div class="tools"><button class="icon-btn bordered" title="Examples">{i("pages")}</button><button class="btn primary">{i("plus")}New endpoint</button></div></div>
<section class="panel"><div class="bar"><div class="search">{i("search")}<span>Filter by name or id</span></div><span class="seg hide-m"><span class="on">All</span><span>POST</span><span>GET</span><span>Disabled</span></span><span class="spacer"></span><button class="icon-btn hide-m" title="Columns">{i("columns")}</button></div>
<div class="tbl-wrap"><table class="t"><thead><tr><th class="w-cb"><span class="cb"></span></th><th><span class="sort">Name{i("chevd")}</span></th><th>Method</th><th>Status</th><th class="num">Steps</th><th>Access</th><th class="num">Calls</th><th class="num">Errors</th><th class="num">Error rate</th><th class="num">Avg</th><th></th></tr></thead><tbody>
{"".join(er)}</tbody></table></div><div class="foot"><span>11 endpoints</span><span class="hide-m">· Calls cover October 2026</span></div></section>'''
page("endpoints.html", "Endpoints", endpoints, "endpoints", ["Projects", "Island survival demo", "Endpoints"], mhead=mobile_head("Build", "Endpoints", f'<button class="icon-btn">{i("plus")}</button>'), tab="Build")

# ---------- Settings ----------
def row(label, val="", link=True, sub="", sw=None, danger=False):
    v = f'<span class="switch {"on" if sw else ""}"></span>' if sw is not None else f'{val}{i("chev", "i chev") if link else ""}'
    s = f'<span class="sub">{sub}</span>' if sub else ""
    return f'<li class="{"link" if link else ""} {"danger" if danger else ""}"><span class="lbl">{label}{s}</span><span class="val">{v}</span></li>'


settings = f'''<div class="head"><div class="titles"><h1>Settings</h1><div class="meta">Island survival demo<span class="dot">·</span>{chip("proj_eb58f3a13d02e6f5620a8979")}</div></div></div>
<div style="max-width:720px">
<div class="group-t">General</div><section class="panel m-keep"><ul class="list">{row("Name", "Island survival demo")}{row("Description", "Generated sample data")}{row("Colour", "Automatic")}{row("Hosting", "Player-hosted")}{row("Project enabled", sw=True, link=False, sub="Disabling rejects every game request")}</ul></section>
<div class="group-t">Security</div><section class="panel m-keep"><ul class="list">{row("Require s&amp;box authentication", sw=True, link=False, sub="A player can only act as their own Steam ID")}{row("Auth sessions", "Off")}{row("Encrypted requests", "Off")}{row("API keys", "2 keys")}</ul></section>
<div class="group-t">Game versions</div><section class="panel m-keep"><ul class="list">{row("Revision policy", "Notify, no minimum")}{row("Player key mode", "Player")}{row("Legacy player projections", sw=False, link=False)}</ul></section>
<div class="group-t">Data</div><section class="panel m-keep"><ul class="list">{row("Download project archive", i("download"), link=False)}{row("Backups", "sbox-ns db backup")}</ul></section>
<div class="group-t">Danger</div><section class="panel m-keep"><ul class="list">{row("Delete project…", "", link=False, danger=True)}</ul></section></div>'''
page("settings.html", "Settings", settings, "settings", ["Projects", "Island survival demo", "Settings"], mhead=mobile_head("More", "Settings"), tab="More")

# ---------- API keys ----------
keys = [
    ("Game client", "Public", "sbox_ns_7Hq2c0dVwz1Lk9", "Runtime", True, "2026-09-02"),
    ("Editor sync", "Secret", "sbox_sk_…a91f", "Full access", True, "2026-09-02"),
    ("Old build 0.4", "Public", "sbox_ns_Qe81mZt0Pn44", "Runtime", False, "2026-07-18"),
]
kr = []
for label, t, k, perm, en, created in keys:
    kr.append(f'''<tr class="m-row"><td class="m-hide"><b>{label}</b></td><td class="m-hide"><span class="tag">{t}</span></td><td class="m-hide">{chip(k) if t == "Public" else f'<span class="key mut">{k}</span>'}</td><td class="m-hide mut">{perm}</td><td class="m-hide"><span class="tag {"ok" if en else ""}">{"Enabled" if en else "Disabled"}</span></td><td class="m-hide mut">{created}</td><td class="w-act m-hide"><span class="rowmenu">{i("more")}</span></td>
<td class="m-main"><div class="mr"><span class="grow"><span class="l1">{label}</span><span class="l2 mono">{k}</span></span><span class="rt">{t}<br>{"Enabled" if en else "Disabled"}</span>{i("chev")}</div></td></tr>''')
keys_body = f'''<div class="head"><div class="titles"><h1>API keys</h1><div class="meta">3 keys<span class="dot">·</span>Public keys go in the game. Secret keys never do.</div></div>
<div class="tools"><button class="btn primary">{i("plus")}New key</button></div></div>
<section class="panel"><div class="tbl-wrap"><table class="t"><thead><tr><th>Label</th><th>Type</th><th>Key</th><th>Permissions</th><th>Status</th><th>Created</th><th></th></tr></thead><tbody>{"".join(kr)}</tbody></table></div></section>'''
new_key = f'''<div class="scrim"></div><div class="modal" role="dialog"><div class="modal-h"><h2>New API key</h2><span class="spacer"></span><button class="icon-btn hide-m">{i("x")}</button></div>
<div class="modal-b"><label class="field"><span>Label</span><div class="input focus">Game client 0.6<span class="caret"></span></div></label>
<div class="field"><span>Type</span><span class="seg" style="display:flex"><span class="on" style="flex:1;justify-content:center">Public</span><span style="flex:1;justify-content:center">Secret</span></span></div>
<p>Public keys are safe in game builds. The key is shown once after creation.</p></div>
<div class="modal-f"><button class="btn ghost">Cancel</button><button class="btn primary">Create key</button></div></div>'''
mh_keys = mobile_head("More", "API keys", f'<button class="icon-btn">{i("plus")}</button>')
page("keys.html", "API keys", keys_body, "keys", ["Projects", "Island survival demo", "API keys"], mhead=mh_keys, tab="More")
page("keys-new.html", "API keys", keys_body, "keys", ["Projects", "Island survival demo", "API keys"], extra=new_key, mhead=mh_keys, tab="More")
print("ok")
