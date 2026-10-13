# Network Storage design system

One system for the owner console and sboxns.com. It replaces `docs/ui-guidelines.md` when the redesign lands. The mockups in `mockup/` implement it; `console.css` is the reference token set.

## Layout rules

1. **One intent per page.** The first viewport shows that page's data. Everything else is one click away, not below it.
2. **No inline forms.** Anything you type into opens in a modal (up to about 4 fields) or its own page. Exceptions: table search and filter fields, and switches that apply immediately with an undo toast.
3. **Actions are a toolbar.** One primary button, icon buttons with tooltips, a `...` overflow for the rest. Row actions live in a row `...` menu.
4. **Copy chips** for every ID, key, URL and command. Click copies the full value; long values truncate in the middle.
5. **Copy budget:** title plus one meta line. Explanations go behind a help icon, a Guides page or a docs link.
6. **Layering:** list, then sheet (detail), then page (edit), then modal (confirm). Breadcrumbs live in the topbar.
7. **Mobile (under 760px):** 48px nav bar, 64px tab bar, large title, list rows instead of tables, modals become bottom sheets.

## Themes

| Theme | Use |
| --- | --- |
| System (default) | Resolves to Dark or Light from `prefers-color-scheme`. |
| Dark | Near-black canvas. The default dark. |
| Light | White canvas, light gray sidebar. |
| High contrast | Pure black, white text, strong lines, thicker focus ring, underlined links. |

The console picks the theme in the owner menu; sboxns.com follows the system with a toggle in the nav. The choice is stored in `localStorage` and applied before first paint. Themes only redefine tokens; components never branch on the theme name.

Retired: Cool dark, Cool light, Mint and orchid, Slate, Warm. They were hue shifts of the same theme, and seven themes times every view is more than anyone tests. Stored keys map forward: `cooldark`, `slate`, `mint`, `warm` to `dark`; `coollight` to `light`. High contrast is the only extra theme because it does a different job.

## Tokens

| Token | Dark | Light | High contrast | Use |
| --- | --- | --- | --- | --- |
| `--bg` | `#0a0a0b` | `#ffffff` | `#000000` | Canvas |
| `--side` | `#0a0a0b` | `#fafafa` | `#000000` | Sidebar |
| `--bg-raised` | `#141416` | `#ffffff` | `#0d0d0d` | Overlays only |
| `--fill` | `#17171a` | `#f4f4f5` | `#1a1a1a` | Secondary buttons, chips, search, hover, current nav |
| `--fill-strong` | `#26262b` | `#ffffff` | `#3a3a3a` | Segmented thumb, pressed |
| `--track` | `#2c2c31` | `#e4e4e7` | `#555555` | Switch off, progress track, avatar |
| `--line` | `#222226` | `#e4e4e7` | `#6e6e6e` | Panel and tile outlines |
| `--line-strong` | `#313137` | `#d4d4d8` | `#a3a3a3` | Inputs, checkboxes |
| `--line-faint` | `#18181b` | `#efeff1` | `#3d3d3d` | Row and section rules |
| `--text` | `#ededef` | `#09090b` | `#ffffff` | Primary text |
| `--text-2` | `#8f8f98` | `#62626b` | `#d4d4d4` | Secondary text, labels, meta |
| `--text-3` | `#66666f` | `#86868f` | `#a3a3a3` | Placeholders, disabled, separators, icons beside a label |
| `--accent` | `#10b981` | `#10b981` | `#34d399` | Fills: primary button, checked, progress, selection tint |
| `--on-accent` | `#04120c` | `#04120c` | `#000000` | Text on accent fills |
| `--accent-fg` | `#10b981` | `#047857` | `#34d399` | Accent as text or icon: links, current nav icon, code strings |
| `--success` | `#4ade80` | `#15803d` | `#4ade80` | State only |
| `--warning` | `#fbbf24` | `#b45309` | `#fcd34d` | State only |
| `--danger` | `#f87171` | `#dc2626` | `#fca5a5` | State only |
| `--info` | `#60a5fa` | `#2563eb` | `#93c5fd` | State only, GET tags, code identifiers |
| `--code` | `#0f0f11` | `#f6f6f7` | `#0d0d0d` | Code and JSON blocks |

sboxns.com uses the same tokens with `--bg: #08090a` in Dark.

Rules:

- No raw colors outside theme blocks.
- The primary button is the same green in every theme. Use `--accent-fg`, not `--accent`, wherever green is text or an icon.
- `--text-3` never carries data or a label. It only marks placeholders, disabled controls, separators and icons that sit next to their own text label.
- State colors mean state, and always come with a word.

### Contrast (WCAG)

| Pair | Dark | Light | High contrast |
| --- | --- | --- | --- |
| Text on bg | 16.93 | 19.90 | 21.00 |
| Secondary text on bg | 6.17 | 6.04 | 14.17 |
| Secondary text on raised | 5.74 | 6.04 | 13.11 |
| Tertiary on bg (non-text minimum 3.0) | 3.48 | 3.61 | 8.33 |
| Accent text on bg | 7.80 | 5.48 | 10.92 |
| Button text on accent | 7.55 | 7.55 | 10.92 |
| Success on bg | 11.36 | 5.02 | 12.05 |
| Warning on bg | 11.85 | 5.02 | 14.56 |
| Danger on bg | 7.15 | 4.83 | 11.06 |
| Info on bg | 7.78 | 5.17 | 11.65 |

## Surfaces and depth

- **No gray canvases.** Near-black in Dark, white in Light, black in High contrast.
- **Content is outlined.** Panels, tiles, tables and cards are a 1px `--line` outline with no fill.
- **Fills are for controls.** Secondary buttons, chips, search, segmented tracks, hover and the current nav item.
- **Only overlays are raised.** Modals, sheets, menus and toasts use `--bg-raised` and `--shadow`. Nothing else casts a shadow.
- **Sticky bars blur.** Topbar, mobile nav bar, tab bar and the site nav use 80% `--bg` with `saturate(180%) blur(16px)`.
- **Sheets float.** Desktop sheets sit 8px off the window edge with a 14px radius. Mobile sheets are full screen; mobile modals are bottom sheets.

## Shape

| Radius | Used for |
| --- | --- |
| 4px | Checkboxes, keyboard hints, method tags |
| 6px | Buttons, inputs, chips, icon buttons, nav items |
| 10px | Panels, tiles, avatars, choice cards |
| 12px | Menus, site cards |
| 14px | Modals, sheets |
| 18px | Top corners of mobile bottom sheets |
| Pill | Status tags, switches, toasts, progress bars |

## Type

- System UI stack (SF Pro on Apple, Segoe UI Variable on Windows); `ui-monospace` for IDs, keys, commands, code and JSON.
- Sizes: 12px meta and table headers, 13px tables and secondary text, 14px body, 15px mobile body, 22px page titles, 28px mobile large titles, 44px site hero.
- Weights 400, 550, 600, 650. Titles track at -0.02em.
- Tabular numbers in tables, stats and counters.
- Sentence case everywhere; no uppercase labels.

## Icons

- Heroicons 20 solid (MIT): 16px in controls and nav, 14px in stat labels, 20 to 22px in mobile bars.
- Brand marks (GitHub, Discord) from Simple Icons (CC0).
- Vendored inline SVG (`mockup/icons.json`); no icon font, no CDN.
- Icons inherit `currentColor`: `--text-3` at rest beside a label, `--accent-fg` when current.
- Icon-only buttons need a tooltip and `aria-label`.

## Controls

| Control | Spec |
| --- | --- |
| Primary button | `--accent` fill, `--on-accent` text. One per view. |
| Secondary button | `--fill`, no border. |
| Icon and ghost buttons | Transparent, `--fill` on hover. No border. |
| Danger button | 14% danger tint, danger text. The action itself confirms in a modal. |
| Input | 36px, 1px `--line-strong`, transparent. Focus: accent border plus a 3px 25% accent halo. |
| Segmented control | `--fill` track, `--fill-strong` thumb with a 1px shadow. |
| Switch | Pill. `--track` off, `--accent` on, white knob. Applies immediately with an undo toast. |
| Status tag | Pill, 13% state tint, 6px dot, state word. |
| Method tag | Mono, 4px radius, tinted (GET info, POST accent). |
| Copy chip | Mono on `--fill`, copy icon, middle truncation. |
| Focus ring | `--ring`: a 2px canvas gap plus a 2px accent ring (3px in High contrast). Every interactive element. |

Heights: 32px desktop controls, 36px inputs, 44 to 48px mobile targets.

## Data

- **Tables:** 34px header, 38px rows, `--line-faint` rules. Hover is 70% `--fill`, selection a 10% accent tint, and the open row a 7% accent tint with a 2px accent edge. Numbers are right-aligned.
- **Columns:** collection schema fields become columns, with a Table / JSON toggle. Default page size 25, with a 10/25/50/100 picker remembered per collection.
- **Stat tiles:** icon and label in `--text-2`, a 20px semibold value, unit in small `--text-2`.

## Motion

- 120ms color and background transitions on interactive elements.
- Overlays: 160ms opacity plus scale from .98. Sheets: 200ms slide.
- None under `prefers-reduced-motion`.

## Kept from `docs/ui-guidelines.md`

- Copy rules: sentence case, no em dashes, no filler, errors state cause and fix, no emoji, no invented numbers in shipped UI.
- Native `<dialog>` with the `?dialog=` no-JavaScript fallback; destructive actions confirm in the shared dialog, typed confirmation for project and record deletes.
- No CDN; the console works offline on a LAN.
- No inline `<script>` or `<style>` in views.

## What changes from `docs/ui-guidelines.md`

| Rule | Before | Now |
| --- | --- | --- |
| Dark canvas | `#111217`, gray cards | `#0a0a0b`, outlined panels |
| Light canvas | `#f2f4f6` with white cards | White, outlined panels |
| Themes | 7 | System, Dark, Light, High contrast |
| Icons | 1.6px stroke | Solid |
| Radius | 4 and 6px only | 4 to 18px scale plus pill |
| Pill badges and status dots | Banned | Status tags are pills with a dot and a word |
| Button borders | 2px on every variant | No borders; fills carry hierarchy |
| Shadows | One shallow elevation | Overlays only, real depth |
| `backdrop-filter` | Banned | Sticky bars |
| `--accent-2` purple | Wayfinding marker | Removed; current nav uses `--accent-fg` |
| Pure `#000` / `#fff` | Banned | White Light canvas, black High contrast canvas |
| Topbar | 56px | 48px with breadcrumbs |
