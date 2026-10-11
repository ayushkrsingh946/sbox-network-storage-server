# Dashboard UI guidelines

Rules for the owner dashboard (`src/SboxNetworkStorage.Server/Views/Owner`,
`wwwroot/`). They remove the patterns that make an interface read as
generated ("AI slop") and keep the dashboard plain, dense and predictable.

Sources: the [Gesso anti-slop rule catalog](https://github.com/Gesso-Build/skills/blob/main/skills/anti-slop/references/rules.md),
[anti-slop-design](https://github.com/Ferousco-dev/anti-slop-design) visual and
component patterns, and the sboxcool.com design guidelines. Rule ids in
brackets match the Gesso catalog so findings can be cross-referenced.

## Copy

- No em dashes (`—`) or spaced en dashes in any visible string, including
  tooltips, alt text and error messages [em-dash-copy]. Use a period, comma,
  colon or parentheses. Ranges use a plain hyphen (`1-50`).
- No eyebrows, kickers or "supertext": no small uppercase or accent-colored
  label sitting above a heading [hero-kicker-eyebrow, row-kicker-eyebrow].
  The heading carries the section. A page title may have one plain sentence
  under it, nothing above it.
- No uppercase body or label text [all-caps-body]. Table headers and form
  labels are sentence case at normal letter spacing.
- No emoji as icons [emoji-icon]. Use text, or nothing.
- No filler or benefit-speak ("seamless", "powerful", "effortless") and no
  "not X, but Y" cadence [benefit-speak, not-x-but-y-cadence]. Say what the
  control does.
- Error copy states what failed and what to do. No apologies
  [apologetic-error-copy].
- No invented numbers or placeholder data in shipped UI [fabricated-precision,
  lorem-ipsum]. Empty states say what is empty and how to fill it.

## Shape and borders

- No colored side rails and never a one-side accent border on a rounded box:
  no `border-top`/`border-left`/`border-right`/`border-bottom` stripe of 2px
  or more used as decoration on an element that also has `border-radius`
  [edge-stripe]. Notices and alerts use a full 1px border in the state color,
  or a tinted background, not a stripe. A selected row or the current nav
  item may use a rail (state, not decoration).
- One radius scale, used everywhere: `--radius-sm` (4px) for inputs, buttons,
  chips, badges, icons and other small controls; `--radius` (6px) for cards,
  notices, dialogs and toasts. No other radii, no pill badges, nothing larger
  [over-rounded-card]. Hairline list rows and dividers inside a rounded panel
  keep square corners.
- No cards inside cards [nested-cards]. No card per table row
  [row-as-card]. Lists separate rows with spacing or one hairline, never a
  border on every side of every row [redundant-border].
- No heavy or colored drop shadows, glows or neon [heavy-box-shadow,
  dark-glow]. Elevation is a 1px border plus a slightly lighter surface. Any
  shadow is tinted to the background hue, never pure black.

## Color

- One primary accent per theme (emerald green), used for primary actions,
  links, focus, code and the brand mark only.
- One restrained secondary token (`--accent-2`, muted purple) for small
  wayfinding markers only: project avatars, the current section icon, pinned
  project pins and the demo notice marker. No purple washes, gradients or
  large fills [indigo-accent, purple-violet-wash].
- State colors (success, warning, danger, info) only for state. Never decorative.
  Every state is also conveyed in words, never by color alone.
- No pure `#000` or `#fff` surfaces. Text contrast meets WCAG AA in every
  theme, including muted text. Measured ratios are recorded under
  [Contrast](#contrast) below.
- No decorative status dots or fake visualizations [fake-dot-viz]. A status is
  a word ("Healthy", "Disabled"), optionally colored.

## Contrast

Minimum is WCAG AA (4.5 for text). Accent pairs are checked both ways because
the accent is used as link text on surfaces and as a button background.

| Pair | Dark | Mint | Light | Slate | Warm |
| --- | --- | --- | --- | --- | --- |
| Text on background | 16.23 | 16.19 | 14.62 | 13.46 | 13.52 |
| Muted text on background | 7.65 | 7.63 | 5.52 | 8.41 | 8.23 |
| Muted text on card | 7.02 | 6.89 | 5.99 | 7.47 | 7.35 |
| Accent link text on background | 7.32 (`#10b981`) | 9.60 (`#34d399`) | 4.62 (`#15803d`) | 9.01 (`#a7c5b5`) | 9.09 (`#b5c5a7`) |
| Button text on accent background | 7.55 (`#04120c` on `#10b981`) | 9.96 (`#04120c` on `#34d399`) | 4.79 (`#f0fdf4` on `#15803d`) | 8.09 | 8.05 |
| Success on background | 10.10 | 10.03 | 5.58 | 9.45 | 9.35 |
| Warning on background | 10.01 | 9.95 | 5.06 | 9.03 | 8.94 |
| Danger on background | 8.41 | 8.35 | 5.19 | 7.58 | 7.50 |
| Info (`#9cc4e8` dark themes, `#1f5fa8` light) on background | 10.15 | 10.08 | 5.93 | 9.15 | 9.06 |

## Dialogs and toasts

- One modal vocabulary: a native `<dialog>` element with a title, a body, a
  primary action and a cancel action. `owner-dialog.js` opens it with
  `showModal` (native focus trap, Escape and top layer) and returns focus to
  the opener on close. No custom overlay divs.
- Destructive and irreversible actions ask in the shared confirm dialog, which
  names the target and the consequence on a danger-styled button. Typed-name
  confirmation stays for project and record deletes, checked server-side.
- Never call `window.confirm`. Destructive buttons carry `data-confirm` (and
  `data-confirm-title`); without JavaScript the form POSTs directly, which is
  the same fallback those buttons always had.
- Every dialog form is a plain server `<form method="post">`, reachable
  without JavaScript (a `?dialog=` link renders the same form with the `open`
  attribute). A server validation failure re-renders the page with that
  dialog open, values preserved except secrets, error next to the field.
- Success toasts name what changed ("Settings saved"), render from one-shot
  TempData flashes in a polite live region, stay at least 5 seconds, pause on
  hover/focus, and are dismissible. Errors appear next to their cause, never
  only as a toast. One-time secrets use a persistent panel, never a toast.
- Dialogs and toasts animate only `opacity` or `transform`, in 150 ms or
  less, and not at all under `prefers-reduced-motion`.

## Type and layout

- One sans stack for UI, one monospace stack for code, IDs, keys and JSON
  [monospace-body]. Body 14-15px, line height 1.5 or more [tiny-body-text,
  tight-line-height]. No tracking tweaks on body text.
- Numbers are tabular (`font-variant-numeric: tabular-nums`) in tables and
  counters. No oversized hero numbers [oversized-number].
- No section numbering ("01", "02") [numbered-section-markers], no icon-topped
  feature cards, no three identical cards in a row.
- Motion: none by default. If used, transition only `opacity`, `color`,
  `background-color` or `transform`, under 150 ms, never `transition: all`,
  never bounce easing, and honor `prefers-reduced-motion`
  [transition-all, bounce-easing].

## Themes

Theme list, in picker order: System (default), Dark, Light, Mint and orchid,
Slate, Warm. Storage keys are `system`, `dark`, `light`, `mint`, `slate`,
`warm`. The `mint` key is kept stable so stored preferences survive the
"Mint and orchid" relabel.

| Token | Dark "Deep emerald" (default dark) | Mint "Mint and orchid" | Light "Paper" (default light) |
| --- | --- | --- | --- |
| `--bg` | `#0b1512` | `#101512` | `#f4f6f3` |
| `--card` | `#101f1a` | `#182019` | `#ffffff` |
| `--surface-2` | `#16281f` | `#222c22` | `#e9ede6` |
| `--border` | `#2a4436` | `#3a4a3c` | `#c9d1c4` |
| `--text` | `#e8f2ec` | `#ebf2ea` | `#1d241c` |
| `--muted` | `#93aca0` | `#9aab9c` | `#5c665a` |
| `--accent` | `#10b981` | `#34d399` | `#15803d` |
| `--accent-text` | `#04120c` | `#04120c` | `#f0fdf4` |
| `--accent-2` | `#8b7fd4` | `#c084fc` | `#7c3aed` |

Slate and Warm stay as alternate dark themes using the same token names.
State tokens (`--success`, `--warning`, `--danger`, `--info`, `--code-bg`,
`--danger-bg`) are derived per scheme in the same hue family and AA-checked
in [Contrast](#contrast). No raw hex outside theme blocks.
- Themes are `[data-theme="…"]` blocks on `<html>` that only redefine tokens.
  Components never branch on the theme name. Dark is also the `:root`
  default, with an explicit `[data-theme="dark"]` alias block.
- System is the default selection: with nothing stored, a dark color scheme
  resolves to Dark and a light scheme resolves to Light. A user choice is
  stored in `localStorage` and applied before first paint from an external
  script (no inline script).

## Assets

- No inline `<script>`, `<style>` or `style=""` in views. CSS in
  `wwwroot/css`, JS in `wwwroot/js`.
- No CDN dependencies. The dashboard must work offline on a LAN.

## Checklist before merging dashboard changes

1. `rg -n '—|–' src/SboxNetworkStorage.Server/Views src/SboxNetworkStorage.Server/Owner/*.cs`
   is empty. Controller messages are visible text too.
2. No `text-transform: uppercase`, `border-left`/`border-right` rails,
   `linear-gradient`, `backdrop-filter`, `box-shadow` beyond the single shallow
   elevation shadow, or raw hex outside theme token blocks in `wwwroot/css`.
3. Every new view checked in the light theme and one dark theme.
4. Empty, loading and error states exist for every new list or form.
5. `rg -n 'window\.confirm' src/SboxNetworkStorage.Server/wwwroot/js` is empty:
   destructive actions use the shared confirm dialog, and every dialog form
   works with JavaScript disabled. Toasts render from TempData flashes and
   stay visible as inline notices without JavaScript.


## Shell conventions (compact owner shell)

- Topbar: 56px, sticky, brand mark 28px. Contains only the brand, theme
  picker, version and logout. A hamburger toggle (`#nav-toggle`) shows
  below 760px and opens `#owner-nav`; behavior lives in `owner-theme.js`.
- Sidebar: 232px (200px below 1100px), sticky under the topbar with its
  own scroll. Groups use plain sentence-case labels (Workspace, Project,
  Build, Manage, Observe) that stay visible on mobile; the panel opens
  and closes instead of collapsing to unlabeled chips. The current item
  keeps surface-2 plus an inset 2px `accent-2` rail (state, not
  decoration).
- Content: `main` is fluid and left-aligned against the sidebar, full width
  with a consistent 24-32px gutter (`clamp(1.5rem, 2.5vw, 2rem)`), 1rem on
  mobile. No page-wide max-width cap. Only long prose and narrow forms take
  a readable cap (`.prose`, max 72ch; the login page stays capped at 720px).
  Page titles are 1.45rem with one plain supporting sentence. Cards use 1rem
  padding. Vertical rhythm follows a 4/8px scale: page heading and filter
  blocks use 1rem bottom margins, grids use 1rem or .75rem gaps.
- Server notices render once as the compact dismissible alert strip above the
  shell (demo, plain HTTP, update). The update item keeps the version, the
  `sbox-ns update` hint and the changelog link.
- Projects list uses `.project-grid` (auto-fill, 300px minimum), not
  Bootstrap grid classes. Bootstrap vendor files stay linked for
  compatibility but views must not add new Bootstrap layout classes.
- Buttons: 32px desktop controls, 28px source tools and 44px mobile targets.
  Use 2px borders on every variant (primary, secondary, danger, icon) with
  1px-slimmer vertical padding so heights do not change. 600 weight and no
  lift on hover. Primary hover is a token mix of accent and accent text;
  secondary hover is surface-2. Focus is a 2px accent outline with 2px
  offset everywhere.
- Tables: headers .76rem muted, cells .83rem with .6rem padding,
  tabular numbers, row hover only.
- Motion: no smooth scroll, no card lift. One `prefers-reduced-motion`
  block disables remaining transitions.
- `workbench.css` loads in `<head>`; `workbench.js` loads deferred at
  the end of `<body>` so page editors initialize before it wraps
  `textarea.code-editor`.
- Resource pages use a 180px resource list and an editor/integration split.
  The integration guide sits beside the editor on wide screens and below it
  under 1250px. At 760px the resource list and workspace stack.
- Code retains a native authoritative textarea. The syntax overlay and
  clickable line-number gutter never enter copied source or submitted forms.
  Tab modes are progressively enhanced; source and examples remain readable
  without JavaScript.
- Table sorting is opt-in with `data-sortable`. Keep workflow execution,
  pending writes and editable value rows in semantic order. Sort controls use
  native buttons, `aria-sort` and a live announcement. Display-only sorting
  must never imply reordering an entire paginated database.

### Block editor

- One block vocabulary across collection, endpoint, workflow, query and game
  values: collapsed blocks, typed option controls, add dialogs, duplicate,
  remove, rename and handle reorder.
- Guided never shows the YAML textarea. The textarea lives in Raw and stays
  authoritative for selection, undo, form posts and the no-JavaScript
  fallback.
- Never serialize the whole document from Guided. Every Guided edit is a
  targeted source splice that preserves comments, unknown fields, quoting
  and numeric lexemes.
- Route unsupported edits to Raw with a message. Invalid YAML, `sourceText`
  wrappers and YAML aliases or anchors say where to go and leave the source
  untouched.
- Keyboard reorder matches drag: move buttons plus Alt+Up and Alt+Down on
  the handle, with focus kept on the moved block.
- Secret keys never appear in generated game examples. Game code uses the
  public key; secret endpoints and queries say to call from a dedicated
  server.
