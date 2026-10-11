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

- No colored side rails: no `border-left`/`border-right` of 2px or more used
  as decoration, and never a side rail on a rounded box [edge-stripe]. Notices
  and alerts use a full 1px border in the state color, or a tinted
  background, not a stripe. A selected row or the current nav item may use a
  rail (state, not decoration).
- One radius scale: `--radius-sm` (4px) for inputs, buttons and chips,
  `--radius` (6px) for panels. Nothing larger [over-rounded-card]. No pill
  badges with heavy fills.
- No cards inside cards [nested-cards]. No card per table row
  [row-as-card]. Lists separate rows with spacing or one hairline, never a
  border on every side of every row [redundant-border].
- No heavy or colored drop shadows, glows or neon [heavy-box-shadow,
  dark-glow]. Elevation is a 1px border, a slightly lighter surface and one
  shallow neutral shadow. No single-side accent bars: with rounded corners
  they read as slop, not identity.

## Color

- Neutral silver surfaces. One primary accent per theme (dull green), used for primary actions, links, focus, code and the current nav item only.
- One restrained secondary token (`--accent-2`, muted purple) for small wayfinding markers only: project avatars, the current section icon. No purple washes, gradients or large fills [indigo-accent, purple-violet-wash].
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

| Pair | Dark | Light | Slate | Warm |
| --- | --- | --- | --- | --- |
| Text on background | 15.45 | 14.06 | 14.18 | 13.52 |
| Muted text on background | 8.74 | 6.42 | 8.96 | 8.23 |
| Accent link text on background | 7.24 (`#bb91f5`) | 6.73 (`#6633c7`) | 10.13 (`#b6cee5`) | 10.51 (`#decbb4`) |
| Button text on accent background | 7.24 | 7.03 (`#fafafa` on `#6633c7`) | 10.13 | 10.51 |
| Success on background | 10.77 | 6.52 | 9.87 | 9.97 |
| Warning on background | 10.50 | 6.58 | 9.61 | 9.71 |
| Danger on background | 9.02 | 6.97 | 8.26 | 8.35 |
| Info (`#9cc4e8` dark themes, `#1f5fa8` light) on background | 9.80 | 5.91 | 8.97 | 9.06 |

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

- Every color in CSS comes from a token (`--bg`, `--card`, `--surface`, `--surface-2`,
  `--border`, `--text`, `--muted`, `--accent`, `--accent-text`, `--accent-2`, `--green`, `--success`,
  `--warning`, `--danger`, `--info`, `--code-bg`). No raw hex outside theme blocks.
- Themes are `[data-theme="…"]` blocks on `<html>` that only redefine tokens.
  Components never branch on the theme name.
- The default theme follows `prefers-color-scheme`. A user choice is stored in
  `localStorage` and applied before first paint from an external script (no
  inline script).

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
- Content: `main` caps at 1320px with compact padding. Page titles are
  1.45rem with one plain supporting sentence. Cards use 1rem padding.
- Projects list uses `.project-grid` (auto-fill, 300px minimum), not
  Bootstrap grid classes. Bootstrap vendor files stay linked for
  compatibility but views must not add new Bootstrap layout classes.
- Buttons: 32px desktop controls, 28px source tools and 44px mobile targets.
  Use 1px borders, 600 weight and no lift on hover. Primary hover is a token
  mix of accent and accent text; secondary hover is surface-2.
  Focus is a 2px accent outline with 2px offset everywhere.
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
