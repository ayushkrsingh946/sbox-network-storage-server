## Problem

The owner console stacks everything a page could do onto one scroll. Data, forms, tutorials and danger zones share the same column, so the thing you came for is rarely in the first viewport.

Measured on `main` (ee60d4f) in demo mode at 1440x900:

| Page | What is wrong |
| --- | --- |
| Project overview | 4,576px tall. `Project.cshtml` has 12 `<form>`s and 45 inputs, selects and textareas: settings, security, player keys, projections, revision policy (16 fields), key permissions inside `<details>`, archive, typed delete. The `NetworkStorage.Configure` line, the most copied thing on the page, sits under two notices. |
| Collection records | 4.5 rows above the fold, 1 row on mobile. Each row is about 107px because the JSON preview wraps. No selection, no bulk actions, no collection size, keys not copyable. |
| Projects | One project card, then three tutorial sections (Connect a game, Author your first backend, Use with a coding agent): 2,386px. |
| Record | Typed-confirmation delete form inline under the JSON editor. |
| Shell | Topbar renders about 128px tall. The Discord link in `_Layout.cshtml:122` has no CSS rule (`.topbar-links`, `.discord-link`, `.discord-icon` are unstyled), so its 24x24 SVG scales to about 70px. This is a bug on `main` independent of the redesign. |

| Collection today | Mobile today | Project page today (full height, scaled) |
| --- | --- | --- |
| <img src="before/collection-desktop.png" width="420"> | <img src="before/collection-mobile.png" width="180"> | <img src="before/project-desktop-fullpage.png" width="160"> |

## Proposal

Same accent, light theme and server-rendered Razor. What changes is the page structure, the control vocabulary, a darker default dark theme and solid icons.

### Visual style

- **Near-black, not gray.** The default dark theme moves to a near-black canvas. Panels, stat tiles and tables are outlined, not filled. Only overlays (modals, sheets, menus) get a raised surface.
- **Solid icons.** Heroicons 20 solid (MIT) replace the 1.6px stroke icons, vendored as inline SVG the same way `_Layout.cshtml` ships icons today. No CDN.

| Dark token | Today | Proposed |
| --- | --- | --- |
| `--bg` | `#111217` | `#0a0b0d` |
| `--card` | `#191c22` | `#0a0b0d` (outline only) |
| `--elev` (new) | | `#111216` (modals, sheets, menus) |
| `--surface-2` | `#22262f` | `#16171b` |
| `--border` | `#343b48` | `#24262c` |
| `--text` | `#e8edf3` | `#ececef` |
| `--muted` | `#9aa5b5` | `#8f95a1` |

Contrast on the new background: text 16.70, muted 6.55, accent 7.76, danger 8.91, warning 10.61, info 10.76. All pass AA.

### Rules (added to `docs/ui-guidelines.md`)

1. **One intent per page.** The first viewport shows that page's data. Everything else is one click away, not below it.
2. **No inline forms.** Anything you type into opens in a modal (up to about 4 fields) or its own page (record JSON editor, revision policy, definition editors). Two exceptions, both non-mutating or instantly reversible: table search and filter fields, and boolean switches that apply immediately with an undo toast.
3. **Actions are a toolbar.** One primary button, icon buttons with tooltips, a `...` overflow for the rest. Row actions live in a row `...` menu.
4. **Copy chips** for every ID, key, URL and command. Click copies the full value; long values truncate in the middle.
5. **Copy budget:** title plus one meta line. Explanations move to a help icon, a Guides page or docs links.
6. **Layering:** list, then sheet (detail), then page (edit), then modal (confirm). Breadcrumbs live in the topbar.
7. **Mobile:** bottom tab bar, large title, list rows instead of wide tables, modals become bottom sheets.

### Shell

48px topbar with breadcrumbs, `Ctrl K` jump, guides, theme and an owner menu (Discord, security, logout move there). 220px sidebar with a project switcher and 30px rows with counts.

### Collection: data first

<img src="after/collection-desktop.png" width="900">

- Meta line: collection id chip, type, record count, total size with a budget bar, field count, last write.
- Top-level schema fields become columns. A Table / JSON toggle keeps the raw preview one click away.
- 38px rows. Checkbox column, sortable headers, row `...` menu, page size picker.

| Selection and bulk actions | Row menu |
| --- | --- |
| <img src="after/collection-selected-desktop.png" width="440"> | <img src="after/collection-menu-desktop.png" width="440"> |

### Record: sheet, then page

Clicking a row opens a side sheet with typed fields (Fields / JSON toggle), prev/next, and player shortcuts. Edit goes to the full-page JSON editor. Delete is a confirm modal that keeps the typed-key check.

| Record sheet | Delete confirm |
| --- | --- |
| <img src="after/record-desktop.png" width="440"> | <img src="after/record-delete-desktop.png" width="440"> |

### Project overview

<img src="after/project-desktop.png" width="900">

- Header: ID chip, status, hosting, live revision. Settings and `...` are icons; Browse data is the primary action.
- Five stat tiles that link to their pages: collections, endpoints, records with size, requests 24h, errors 24h.
- Connect: the Configure line plus copy chips for project ID, public key and base URL. C# / Editor toggle.
- Health: the current checklist and authority check as one list with status words and drill-in rows.
- Recent requests: last few rows, link to the full log.

### Settings and API keys get their own pages

| Settings (grouped rows, each opens a modal or subpage) | New key modal |
| --- | --- |
| <img src="after/settings-desktop.png" width="440"> | <img src="after/keys-new-desktop.png" width="440"> |

### Projects

| List | New project modal | Overflow |
| --- | --- | --- |
| <img src="after/projects-desktop.png" width="290"> | <img src="after/projects-new-desktop.png" width="290"> | <img src="after/projects-menu-desktop.png" width="290"> |

### Build pages use the same template

<img src="after/endpoints-desktop.png" width="900">

### Mobile

| Projects | Overview | Collection | Record | New key |
| --- | --- | --- | --- | --- |
| <img src="after/projects-mobile.png" width="150"> | <img src="after/project-mobile.png" width="150"> | <img src="after/collection-mobile.png" width="150"> | <img src="after/record-mobile-mobile.png" width="150"> | <img src="after/keys-new-mobile.png" width="150"> |

### Light theme

<img src="after/collection-light-desktop.png" width="600">

### Where today's content goes

| Today | Proposed |
| --- | --- |
| Project settings, security, player keys, projections, revision policy forms | Settings page rows; small sets open a modal, revision policy is a subpage |
| Create key form and per-key permission forms | API keys page; New key modal; Permissions modal from the row menu |
| Checklist table and authority check section | Health list on the overview, drill-in for findings |
| Synced resources buttons and lists | Sidebar and stat tiles |
| Portable archive form | Settings, Data |
| Delete project typed form | Settings, Danger, confirm modal (typed ID kept) |
| Coding agent prompt (project) | Overflow menu: Copy agent prompt |
| Connect a game, Author your first backend, Coding agents (dashboard) | Guides page behind the help icon; shown inline only in the zero-projects empty state |
| Import, Export server buttons | Projects overflow menu |
| Find player form (data browser) | `Ctrl K` jump and the Data page search field |
| Record delete typed form | Record `...` menu, confirm modal |
| Discord link, theme select, version, logout | Owner menu |

### Implementation notes

- No new framework. Native `<dialog>`, `owner-dialog.js` and the `?dialog=` no-JS fallback already exist (`docs/ui-guidelines.md`, Dialogs and toasts); every modal here is a `<dialog>` around a plain POST form. The record sheet's no-JS fallback is the record page.
- Selection and bulk actions are progressive: without JavaScript, row actions stay on the record page. Bulk delete needs a new POST route (version-checked per key, audit-logged). Bulk export can reuse `/export` with a `keys` filter.
- Collection size needs a store aggregate over record sizes. `SizeBytes` is already computed per record.
- Page size: `OwnerDataController.DefaultPageSize` is 50 today, but rows are about 107px, which is why only 4.5 show. At 38px, 1440x900 fits about 16 rows. The mockup shows 10 per page; I recommend 25 as the default with 10/25/50/100 remembered per collection.
- Owner console only. Per `docs/compatibility.md` this needs no s&box editor run; the usual parity, SQLite/PostgreSQL and smoke evidence applies.

### Guideline amendments needed

- Toolbar icon buttons borderless. Today every button variant has a 2px border.
- Switch tracks fully rounded, and bottom sheets with 12px top corners on mobile, as the only exceptions to the 4/6px radius scale.
- Topbar 48px (from 56) holding the breadcrumbs.
- Dark theme token values from the table above; re-measure the Contrast table.
- Solid icon set replaces the stroke icons.

### Rollout

1. Shell: topbar with crumbs and owner menu (fixes the Discord bug), sidebar switcher, mobile tab bar.
2. Collection page: schema columns, compact rows, size meta, copy chips, row menu, record sheet.
3. Selection, bulk export, bulk delete route.
4. Project overview: stats, Connect, Health, Recent requests.
5. Settings and API keys pages; remove inline forms from `Project.cshtml`.
6. Projects page and Guides page.
7. Apply the template to Build, Observe and Manage pages.

## Alternatives considered

- **Collapse sections with `<details>`.** Shorter scroll, same problem: the page still has no single focus, and the forms are still inline.
- **SPA rewrite.** Breaks the no-CDN, offline and no-JavaScript rules for no gain; Razor plus `<dialog>` covers every interaction shown.
- **Inline-editable table cells.** Conflicts with schema validation and version-checked saves. Editing goes through the record editor.

## Client impact

None. Owner console only; no game-facing request or response changes.

Mockups: `docs/proposals/ui-redesign/mockup/` (static HTML, open in a browser, resize below 760px for mobile). Mockup data is illustrative, modeled on the demo seed.
