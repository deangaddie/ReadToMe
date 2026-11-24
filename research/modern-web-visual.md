# Visual design audit against modern-web-guidance

Research for #460 (map #444). This covers color, dark mode, scrollbars, typography and icons. It is the input to this area's triage.

**Method.** I read each guide from the local skill bundle (`~/.claude/skills/modern-web-guidance/guides/`, skill version `2026_11_10-47b4c80e`), never through `npx`. I read each one against the code it applies to, found through `docs/agents/web/primitives.md`. All paths below are under `src/Read2Me.Web/src/` and are as of `origin/main` at `13fc2cb2`. Contrast ratios are WCAG 2 relative-luminance ratios, worked out from the compiled `light-dark()` tokens in `styles/global.css` with a throwaway script (not committed). A user theme can change every surface token except the status colours, so the ratios hold for the built-in Light and Dark themes only.

**Filter** (map Notes). A finding is kept only if it changes behaviour or robustness: accessibility, forced colors, Firefox fallbacks or performance. It must also pass the browser gate: it ships in Chromium and Firefox, or has a fallback of about 20 lines or less with no dependency. Syntax-only swaps are dropped.

Guides read:

- visual-design: `color`, `dark-mode`, `contrast-color`, `browser-ui-theming`, `adapt-scrollbar-to-contrast-preferences`, `customize-scrollbar-color-and-thickness`, `typography`, `improve-text-layout-and-legibility`, `equal-width-text-lines`, `visually-stable-font-fallbacks`, `visually-stable-mixed-fonts`, `soft-edge-content-fade`, `custom-text-decorations`
- `ui-components/dark-mode-toggle`, `ui-atoms/icons`, `css/design-token-reactivity`

**Summary.** 10 keep, 9 drop, 4 guides with no surface. The three that matter most:

1. **K1**: `contrastText()` uses the wrong luminance threshold. Every theme colour with luminance between about 0.18 and 0.36 gets white text that fails 4.5:1, at worst 2.85:1, where black would pass.
2. **K2**: the `--r2m-outline` token is M3's *outline-variant*. Field borders, stroked buttons, the segmented control's edge and the off switch reach only 1.46 to 2.0:1, under the 3:1 non-text minimum.
3. **K3**: there is no forced-colors support at all. In Windows High Contrast the switch, the selected segment, the active nav item and the indeterminate progress bar lose their state, and dialogs and tooltips lose their edges.

---

## Keep

### K1. `contrastText()` picks white where black is needed (contrast-color)

- **Guide**: `contrast-color`: "The `contrast-color()` CSS function resolves to `black` … or `white` …, maximizing the contrast ratio (targeting at least 4.5:1 WCAG AA contrast)". Its fallback recipe uses "`--wcag-y-threshold: 0.17913`". `color` adds: "Maintain 4.5:1 for normal text".
- **Where**: `app/theme/theme-css.ts:174-176`. It reads `return luminance(hex) > 0.36 ? '#000000' : '#ffffff';`. The black/white crossover, where both give the same ratio, is a luminance of about 0.179. Between 0.179 and 0.36, white is chosen even though black wins. The function feeds `--r2m-on-accent`, `--r2m-on-secondary`, `--r2m-appbar-fg` and the `--r2m-nav-*` foregrounds (`theme-css.ts:196,200,225,229`), the theme editor's defaults (`pages/settings/themes/theme-editor-dialog.ts:72-83`) and the themes page swatch label (`themes-page.ts:162`).
- **What goes wrong**: suppose a user theme sets a common mid-tone primary or app bar colour. Then filled buttons, the app bar and the nav rail get white text below AA. Measured: `#e67e22` gives white at 2.85:1 (black would give 7.37), `#4a90e2` gives 3.29 (black 6.38), `#43a047` 3.30 (6.36), `#1e88e5` 3.68 (5.71), `#00897b` 4.32 (4.86). The built-in azure (`#005cbb`) is unaffected.
- **Firefox**: the fix is a constant (`0.36` to `0.179`, or compare the two ratios directly), so no browser support is involved. Native `contrast-color()` ships in Firefox 146 and Chrome 147, but the value is also used in TS (the editor defaults), so keep the TS function and correct it.
- **Size**: S. It touches the theme token mapping (shared), and `theme-css.spec.ts` needs cases in the 0.18 to 0.36 band.

### K2. The outline token is under 3:1 against every surface (color, non-text contrast)

- **Guide**: `color`: "Maintain a minimum contrast ratio of 3:1 for user interface component boundaries and states … (e.g., input field borders)". `browser-ui-theming`: "Use a contrast between adjacent colors of at least `3:1` for non-text content."
- **Where**: `styles/global.css:71`, `--r2m-outline: light-dark(#c4c6d0, #44474e)`. These are M3's *outline-variant* values, meant for dividers. M3's *outline*, the one meant for component boundaries, is `#74777f` / `#8e9099`. The token is used 106 times, and these uses are component boundaries:
  - field controls (`:248`) and `.r2m-field--inline` (`:273`)
  - `.r2m-button--stroked` (`:194`)
  - the segmented control's edge (`:373`)
  - the switch's unchecked track (`:318`)
  - the menu and the `::picker(select)` (`:504`, `:418`)
  - the file drop zone (`:1619`)
- **What goes wrong**: on `--r2m-surface`, the field border is 1.62:1 in light and 2.00:1 in dark. On `--r2m-surface-container`, the dialog and menu background, it is 1.46:1 and 1.76:1. Users with low vision can't find a text field's edge or a stroked button. The unchecked switch is the worst case: its track is 1.62:1 against the page, and its knob (`--r2m-surface`) is 1.62:1 against the track. Off, the control is close to invisible.
- **Fix shape**: split the token. `--r2m-outline` takes the M3 outline values for control boundaries, and a new `--r2m-outline-variant` keeps the current values for the dividers (`border-top` / `border-bottom` separators, `label + label`). Then sweep the 106 uses into the two groups.
- **Firefox**: plain colour values, no support question.
- **Size**: M. It touches a shared primitive token, and every use needs sorting into boundary or divider. `primitives.md` has no token row, so record the split in `conventions.md`.

### K3. No forced-colors support: state and edges painted only with backgrounds and shadows vanish (color)

- **Guide**: `color`, Forced Colors Mode: "Define system color fallbacks for color tokens using `@media (forced-colors: active)`". Also: "Don't rely on `background-image`, `box-shadow`, or `border-image` to convey borders, separators, or state … ensure there's an alternative in forced colors mode, such as `outline` or `border` with system color keywords".
- **Where**: the code has no `forced-colors` query and no `forced-color-adjust` anywhere. In Windows High Contrast, author backgrounds are forced to system colours and shadows and gradients are removed, so:
  - **Switch** (`global.css:306-344`): the track and knob are both `background` on an `appearance: none` input. Both become `Canvas`, so the control and its checked state disappear.
  - **Segmented control** (`:398`): the selected segment is shown only by `background: var(--r2m-secondary-container)`, so selection is lost.
  - **Active nav item** (`shell/shell.css:287-289`): shown only by background and colour. `aria-current` is set, so AT is fine, but sighted High Contrast users lose "where am I".
  - **Indeterminate `<progress>`** (`global.css:1080-1086`): the sweep is a `linear-gradient` background, which is removed, so a running job shows an empty bar. The determinate fill (`::-webkit-progress-value` / `::-moz-progress-bar` background) is forced too.
  - **Dialog** (`:433-441`, `border: 0` plus `--r2m-shadow-3`) and **tooltip** (`:577-585`, `border: 0`): the edge comes from the shadow and background, so the dialog merges into the page.
- **What goes wrong**: a High Contrast user can't tell a setting's on/off state, can't see which segment is chosen or that a job is running, and sees dialogs with no edge.
- **Fix shape**: one `@media (forced-colors: active)` block in `global.css`:
  - switch: track `border: 1px solid CanvasText`, knob `background: CanvasText`, checked track `Highlight`
  - selected segment and active nav item: `background: Highlight; color: HighlightText`, or an outline
  - progress: an outline, plus a `Highlight` value or band
  - dialog and tooltip: `border: 1px solid CanvasText`

  Selected rows in the config list, structure tree and Cast board probably need the same treatment, so audit them in the same PR.
- **Firefox**: `forced-colors` ships in Chromium and Firefox. Note that Firefox only applies it when High Contrast is on in the OS or in its own colour settings.
- **Size**: M. It touches shared primitives (switch, segmented, progress, dialog, tooltip) and the shell. The proof can use Playwright's `forcedColors: 'active'` emulation, which only works in Chromium. Firefox needs a manual check or `ui.useAccessibilityTheme`.

### K4. Status chip "ok" text is 4.39:1 on its soft background (color)

- **Guide**: `color`: "Maintain 4.5:1 for normal text".
- **Where**: `styles/global.css:89-90`, `--r2m-status-ok: #1b7f3b` on `--r2m-status-ok-soft: #dcf5e3` (light). The status chip (`:639`) sets its text at `--r2m-text-sm` (0.75rem, 12 px), which is normal-size text.
- **What goes wrong**: the light "Ready" / ok chips miss AA (4.39:1). The other pairs pass: warn 4.86, busy 6.16, and every dark pair 6.2 or more. Themes never override status colours (`theme-css.ts:6-8`), so this ratio is fixed.
- **Firefox**: n/a.
- **Size**: S. One token, a shade darker (for example `#17733a` gives about 5.2:1). Shared token.

### K5. The selected segment differs from the rest by 1.23:1 (color, state contrast)

- **Guide**: `color`: the 3:1 rule "also includes visual elements indicating active states within a component".
- **Where**: `styles/global.css:398-401`. The checked label is `--r2m-secondary-container` (`#dae2f9`) against the unselected `--r2m-surface` (`#f9f9ff`), which is **1.23:1** in light and 1.99:1 in dark. The text colour change is minor too. The active nav item is similar: `--r2m-nav-active-bg` against `--r2m-nav-bg` is 1.17:1 (`shell.css:287`).
- **What goes wrong**: in normal colours, low-vision users can't tell which option of a segmented control is chosen (the Projects Books/Series view, settings enums). The nav item at least has `aria-current`, and the page heading backs it up.
- **Fix shape**: add a leading check `icon()` on the checked segment (M3 does this, so it isn't colour alone), or raise the fill's contrast. For nav, add an indicator bar or bold weight. Pairs with K3's forced-colors rule.
- **Firefox**: `:has()` is already used here and ships in both.
- **Size**: S. Shared primitive (segmented). Nav is the shell.

### K6. Text-bearing primitives with a fixed `height` (typography, WCAG 1.4.12)

- **Guide**: `typography`: "**DON'T** set a fixed `height` or `block-size` on elements that contain text, such as buttons, badges, tabs and cards. Let the content set the height, with `min-block-size` if a minimum is needed".
- **Where**: `.r2m-button` `height: 36px` (`global.css:171`); `.r2m-segmented label` `height: 32px` (`:381`); `.r2m-status-chip` `height: 24px` (`:643`); `.shell__setup-shield-count` `height: 16px; line-height: 16px` (`shell/shell.css:210-216`).
- **What goes wrong**: a long button label wraps in a narrow dialog or at 320 px. So does any label once a user's text-spacing override (line-height 1.5, letter spacing 0.12em) or a larger default font applies. The second line then overflows the 36 px pill and overlaps the content below.
- **Fix shape**: change `height` to `min-height`. Keep the vertical centring, which `inline-flex` plus `align-items: center` already does.
- **Firefox**: n/a.
- **Size**: S. It touches shared primitives (button, segmented, chip). Watch for visual diffs in the existing browser suite.

### K7. The activity bar hides its scrollbar (browser-ui-theming)

- **Guide**: `browser-ui-theming`: "**DON'T** use the `scrollbar-width` with any value other than `auto`, unless another accessible option is provided to scroll content."
- **Where**: `app/activity/activity.css:21-28`. `.activity-bar__pills` has `overflow-x: auto; scrollbar-width: none`.
- **What goes wrong**: when several jobs run at once, the pills overflow the 40 px bar. A mouse user without a horizontal wheel or trackpad has no way to reach the hidden pills, and nothing shows that more exist. Keyboard focus does scroll a pill into view.
- **Fix shape**: drop `scrollbar-width: none`. Or keep it and add an accessible alternative: wrap, or a "+N more" pill that opens the drawer, which already lists every job.
- **Firefox**: `scrollbar-width` ships in both, so either fix is fine.
- **Size**: S. Not a shared primitive. Triage should pick the alternative.

### K8. No `<meta name="color-scheme">` in the document head (dark-mode)

- **Guide**: `dark-mode`: "MANDATORY: … place a `<meta>` tag in your `<head>` to ensure the browser knows which themes you support before it even starts rendering" (`<meta name="color-scheme" content="light dark">`).
- **Where**: `index.html:3-12`. There's no meta. `color-scheme: light dark` arrives only with `global.css` (`:29`).
- **What goes wrong**: under a dark OS, the initial canvas is white until the stylesheet has loaded. Each full load or refresh of `/app` can flash white.
- **Firefox**: the meta ships in both.
- **Size**: S, one line. Not a primitive. Do it in the same PR as K9 if K9 is kept.

### K9. A pinned theme that differs from the OS flashes on every load (dark-mode-toggle, persistence and FOUC)

- **Guide**: `dark-mode-toggle`: "To avoid a flash of the wrong scheme, apply the stored value in an inline script (NOT `type=module`, NOT `defer`) placed immediately after the `<meta>` element".
- **Where**: `main.ts:25` awaits `ThemeService.load()`, a host round trip. Only then does `theme.service.ts:122-137` set `data-theme` and inject the theme's `<style>`. Until then, `body` paints the compiled `light-dark()` defaults, which follow the OS.
- **What goes wrong**: suppose a user picks Dark (or a custom dark theme) on a light OS, or the reverse. Every load paints the page in the OS scheme, then snaps to the chosen one once `/api/settings/themes` answers. Custom surface colours snap the same way.
- **Fix shape**: the selection is shared on the host on purpose (`theme.service.ts:15`), so localStorage would be only a *cache* of the last effective theme. Save `isDark` and the built stylesheet text in `apply()`. In `index.html`, a small classic inline script after the meta sets `data-theme` (and optionally injects the cached `<style>`) before first paint. The host answer then corrects any staleness. Wrap it in `try`/`catch` as the guide says. The host sends no CSP today (I found no `Content-Security-Policy` in `Read2Me.App`), so an inline script isn't blocked. If a CSP is added later, it needs a hash.
- **Firefox**: plain DOM and localStorage, so both.
- **Size**: S to M (about 15 lines plus a `theme-css` helper). It touches the theme service, which is shared. The proof is a browser test that loads `/app` with a pinned dark theme under `colorScheme: 'light'` and reads the body background on first paint.

### K10. The theme editor accepts text and accent colours with no contrast check (color)

- **Guide**: `color`: the 4.5:1 and 3:1 minimums. `contrast-color`: "ensuring text remains readable against a variable background color".
- **Where**: `pages/settings/themes/theme-editor-dialog.ts`. `textPrimary`, `textSecondary` and `primary` are free hex inputs. `theme-css.ts:235-240` applies them as `--r2m-text`, `--r2m-text-muted` and `--r2m-accent` (also the link colour, `global.css:120-122`) against whatever `background` / `surface` the theme sets. Nothing measures the pair.
- **What goes wrong**: a user can save a theme where body text, muted text or links (for example a yellow primary on white, about 1.2:1) are unreadable. Because the selection is shared on the host, every client gets that theme.
- **Fix shape**: the editor already has `luminance()`. Show a warning line beside each pair under 4.5:1, or 3:1 for the accent used as non-text. Don't block the save. This is borderline new UI, so triage should decide whether it's a fix.
- **Firefox**: n/a.
- **Size**: S to M. It touches the theme editor only.

---

## Drop

| # | Guide | Where | Reason |
|---|---|---|---|
| D1 | `dark-mode`: "Any JS reading `matchMedia("(prefers-color-scheme: dark)").matches` MUST also handle its `change` event" | `theme/theme.service.ts:115-120` | **Already done.** It listens for `change`. The other `matchMedia` reads (`book-page.ts:795`, `speaker-chip.ts:132`, `(pointer: fine)`) are one-shot at open time, which is correct. |
| D2 | `dark-mode` / `dark-mode-toggle`: `color-scheme: light dark` on `:root`; "the base declaration MUST stay `light dark`" | `global.css:28-29,107-112` | **Already done.** The root stays `light dark`, and `data-theme` pins it the way the guide's `:has(meta)` mapping does. Every colour is `light-dark()`. |
| D3 | `dark-mode-toggle`: "Prefer displaying two states in space-constrained toggles, such as persistent toggles in the site chrome" | `shell/shell.ts:202-215,447-458` (Light / Dark / System `menuitemradio`) | **No failure.** It's a menu (progressive disclosure), not a bare toggle, and System is reachable, which the guide requires. The guide's extra "System (currently light)" hint is cosmetic. |
| D4 | `customize-scrollbar-color-and-thickness`, `adapt-scrollbar-to-contrast-preferences`: `scrollbar-color` with a 3:1 thumb, `prefers-contrast: more` override | none (no `scrollbar-color` in the app) | **No surface.** The app keeps native scrollbars, which follow `color-scheme` and meet OS contrast. Adding custom colours would be styling, not a fix. Only K7's `scrollbar-width: none` breaks a rule. |
| D5 | `improve-text-layout-and-legibility` / `typography`: "Use `text-wrap: balance` for … headlines", "`text-wrap: pretty` for long-form body text" | headings, `.r2m-item__text` (`book.css:400`) | **Cosmetic.** No behaviour or robustness gain. `pretty` is also unsupported in Firefox (degrades to `wrap`). |
| D6 | `visually-stable-font-fallbacks` / `visually-stable-mixed-fonts`: "`font-size-adjust: from-font`" | `--r2m-font-sans` / `--r2m-font-mono` (`global.css:48-49`) | **No measurable failure.** Inter is self-hosted and same-origin (`font-display: swap`), so the swap window is tiny. `from-font` takes the *first available* font's metric, so it doesn't normalise during the swap itself. The mono-in-Inter case (key/value `mono` rows) is cosmetic. |
| D7 | `typography`: "**DON'T** use `px` for font-size. Prefer `rem`" | about 40 `font-size: NNpx` rules, nearly all on `.r2m-icon` glyphs. One is text: `.shell__setup-shield-count` `11px` (`shell.css:215`) | **Low value.** Text tokens are already `rem` (`global.css:50-55`), and browser zoom scales `px` anyway. Only the default-font-size preference skips `px` icons. If K6 touches the shield count, change its `11px` to `var(--r2m-text-xs)` there. No ticket of its own. |
| D8 | `custom-text-decorations`: thickness and offset in `em`; "Where underlined text is not a link, make sure it cannot be mistaken for one" | `.r2m-pron-mark` `book.css:405-412` (`underline dotted`, `2px`, `3px`) | **No failure.** Dotted is the guide's own style "for terms and abbreviations", and `cursor: help` plus the tooltip tell it apart from a link. The `px` to `em` change is cosmetic. Hover underlines (`cast.css:333`, `book.css:447`) are native `text-decoration`, as the guide asks. |
| D9 | `ui-atoms/icons`: CSS-mask icon engine or SVG `<use>`; "make sure that all icons have a contrast of at least 3:1" | `ui/partials.ts:23` (Material Symbols ligature font, `aria-hidden="true"`) | **Already acceptable.** Glyphs are text, so they follow `currentColor` (and `CanvasText` in forced colors) with the text's contrast. Icon-only buttons carry `aria-label` (`primitives.md`, Icon button). Moving to masks or SVG would be a rewrite with no behaviour fix, and the guide's engine needs container style queries (Firefox 151, very new). |

## Guides with no surface in the app

- **`equal-width-text-lines`** (`text-fit` lockups): there are no poster-style headings. `text-fit` is also Chromium-only.
- **`soft-edge-content-fade`** (`mask-image` fades): no scroll-edge fades or overlays exist. The only gradients are the project-card placeholder art (`projects-page.css:77,204`) and the progress sweep.
- **`css/design-token-reactivity`** (container style queries): there are no higher-order tokens like density. Variants are BEM modifier classes, which is the guide's own recommended fallback, so there's nothing to fix.
- **`contrast-color`** as CSS: no element computes its text colour in CSS against a variable background. That work is done in TS by `contrastText()`, which K1 covers.

## Aside, outside these guides (verify before ticketing)

`styles/global.css:1088-1090` nests `&::-webkit-progress-bar, &::-moz-progress-bar { background: transparent; }` as **one selector list**. Selector lists are not forgiving: Chromium doesn't recognise `::-moz-progress-bar`, so it probably drops the whole rule. Firefox should keep it, since by the compat spec it accepts unknown `::-webkit-` pseudo-elements. If so, in Chromium the `::-webkit-progress-bar` keeps its solid `--r2m-surface-highest` background over the element's sweeping gradient, and the indeterminate bar shows a flat track with no motion. Check it in a Chromium browser. If confirmed, the fix is two separate rules, and it belongs with K3's progress work, which rewrites this block anyway.
