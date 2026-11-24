# Audit: forms and controls against modern-web-guidance (#450)

Research ticket #450 under map #444. This file is the input to this area's triage (#451).

- **Source**: the bundled guide markdown under `~/.claude/skills/modern-web-guidance/guides/` (skill version `2026_11_10-47b4c80e`). No `npx`/CLI fetch was run. Browser support is quoted from each guide's own "Fallback strategies" data unless marked otherwise.
- **Code audited**: `src/Read2Me.Web` at `main` `13fc2cb2`. Line numbers are against that commit. Paths are relative to `src/Read2Me.Web/src/`.
- **Bar for a finding** (map #444, Notes): it changes behaviour or robustness (accessibility, forms, Firefox fallbacks), not just newer syntax. It ships in Chromium and Firefox, or it has a fallback of about 20 lines or less with no dependency.
- **Method**: each guide was read against the primitives in `docs/agents/web/primitives.md` that it applies to (Field, Checkbox, Switch, Radio group, Select, Range, Inline edit, Text prompt dialog, Settings form, File drop, Speaker menu, Config list), then the call sites were found by grep (`key === 'Enter'`, `?disabled=${!`, `role="alert"`, `role="switch"`, `type="search"`, `autocomplete=`, `<option value=""`). Nothing was run in a browser. Where a finding rests on rendering I haven't seen, it says so.

## What the app has today

- There are **no `<form>` elements** and no `type="submit"` buttons. Every dialog's primary button is `type="button"` with a click handler, and Enter-to-submit, where it exists, is a custom `keydown` handler.
- Validation is all in signals: a `touched` map plus a pure `validateDraft` (new project, theme editor), or a `computed` validity (text prompt, settings form, add voice rule). Nothing uses native constraint validation (`checkValidity`, `setCustomValidity`, `:user-invalid`). Errors render as `role="alert"` text, and the `field()` partial (`ui/field.ts:38`) wires `aria-describedby` and `aria-invalid` only while an error shows. That part already matches the guides' "link the error only while it's shown" rule.
- Already in line with the guides (see Drops): `accent-color` on checkbox, radio and range; `color-scheme: light dark`; `field-sizing: content` on textareas; `appearance: base-select` under `@supports`; `role="switch"` on every switch; `:focus-visible` rings on every control; native `<input type=range>` and `<select>`.
- No `isComposing` check anywhere in the app (`grep -r "isComposing\|keyCode\|composition"` is empty).

## Findings: keep

### K1. Enter-to-commit handlers fire during IME composition

- **Guide**: `forms/ime-safe-enter-submit`: "For a `<textarea>` or `<input>` with custom enter-to-submit, check the native `isComposing` property of the `KeyboardEvent` before submitting the content." It adds that this "applies to any custom `keydown` handler that acts on `Enter` … inline rename fields, modal dialog fields, and command palettes". `forms/forms.md` §7 repeats it as a DO.
- **Where**: six handlers on text inputs:
  - Text prompt dialog: `ui/dialogs.ts:141` (`e.key === 'Enter' && this.#submit()`).
  - Inline edit: `ui/inline-edit.ts:114` (Enter commits; Escape at `:117` aborts).
  - Speaker menu search: `ui/speaker-menu.ts:185` (Enter picks the active match, or creates a character from the query).
  - Add voice dialog name: `pages/cast/voices/add-voice-dialog.ts:36`.
  - Character alias input: `pages/cast/character-detail.ts:214` (Escape at `:218`).
  - Discovery alias input: `pages/cast/discovery-dialog.ts:299` (Escape at `:302`).
- **What goes wrong today**: a user typing Japanese, Chinese or Korean with an IME presses Enter to confirm a conversion. The handler treats that as commit. The name, alias or title is saved half-converted (inline edit, aliases), the dialog closes with it (text prompt, add voice), or the speaker menu creates a new character named after the unfinished query. Character names are the obvious case: a book's cast in Japanese is typed this way. The same goes for Escape in the three handlers that abort on it: Escape cancels an IME composition, and the handler also throws away the whole edit.
- **Fix**: add `if (e.isComposing || e.keyCode === 229) return;` at the top of each Enter (and Escape) branch, after `preventDefault()` where there is one. The guide recommends the `keyCode === 229` pairing for Safari. It isn't needed for Chromium or Firefox, but it costs nothing. Six one-line guards. Alternatively, the dialog cases go away with K2's `<form>` (the guide: "Native implicit submission … needs no explicit handling").
- **Firefox**: ships. The guide's data: "Supported by: Chrome 56, Edge 79, and Firefox 31. Unsupported in: Safari." No fallback is needed for the browser gate.
- **Size**: S. **Shared primitive**: yes (Inline edit, Text prompt dialog, Speaker menu), plus three feature inputs. Proof: dispatch a `keydown` Enter with `isComposing: true` into each input and assert no `save`/close/`create`, in both browsers. Playwright can't drive a real IME, so this proof uses a synthetic `KeyboardEvent`.
- **Verdict**: **keep**. It's the most important finding here: data is saved wrong and it's a one-line fix per handler.

### K2. Submit buttons are disabled while the form is invalid, so errors never show and nothing says why

- **Guide**: `forms/forms.md` §4: "**DON'T** disable submit buttons to block validation; let users submit and highlight errors. However, **DO** disable the button *after* a valid submission is clicked to prevent double-posts." Also: "**DO** move focus to the first invalid field (or an error summary) when a form submission fails validation." `forms/required-field-feedback`, "Other Considerations" 2: "Unlike `disabled` buttons, keep your submit button enabled."
- **Where**:
  - New project: `pages/projects/new-project-dialog.ts:127` (`?disabled=${!this.#valid() || busy}`).
  - Theme editor: `pages/settings/themes/theme-editor-dialog.ts:207` (`?disabled=${!this.valid()}`).
  - Text prompt dialog: `ui/dialogs.ts:158` (`?disabled=${!this.#valid()}`).
  - Add voice rule: `pages/cast/voice-rules/add-voice-rule-dialog.ts:168` (`?disabled=${!this.canSubmit()}`).
- **What goes wrong today**: on New project, opening the dialog shows a dead Create button. The user has no field error to read, because errors only show for touched fields, and the button gives no blocked reason. The code meant to handle this can't run: `create()` (`new-project-dialog.ts:202`) and `save()` (`theme-editor-dialog.ts:290`) start by marking every field touched to reveal its error, but they're only reachable from a button that is disabled whenever that would matter. A missing book file is the worst case: the three text fields are filled, Create is still off, and the "file required" chip only appears once `file` is touched, which never happens. For a screen reader user a disabled button is skipped or read as "dimmed" with no explanation. This also sits outside the app's own rule (`docs/agents/web/user-messages.md`: "A plain `?disabled` is fine only where no reason is ever shown"). These four buttons do have a reason, but they don't show it.
- **Fix**: keep the primary button enabled except while busy (`?disabled=${busy}`, which the guide endorses). On click, mark all fields touched, and if invalid, focus the first invalid field and return. Optionally wrap each dialog body in a `<form>` with `@submit=${(e) => { e.preventDefault(); … }}` and make the button `type="submit"`. That gives Enter-to-submit from any field for free, and the guide notes it's IME-safe natively, so the custom handlers in `ui/dialogs.ts:141` and `add-voice-dialog.ts:36` can be deleted. Triage should decide whether the `<form>` part is in scope or a follow-up. The core change is "enabled button, validate on click, focus the first error".
- **Firefox**: nothing new is needed; it's plain DOM and focus.
- **Size**: M (four dialogs, one is a shared primitive). **Shared primitive**: yes (Text prompt dialog). Proof: open New project, click Create with nothing filled, and assert focus is on Book title and the errors (including the file one) are visible, in both browsers.
- **Verdict**: **keep**. It conflicts with an established habit in this codebase, so triage should confirm it. The settings `config-editor-frame.ts:46` Save (`canSave`/`dirty`) is a different case, "nothing to save", and isn't included.

### K3. Errors appear and are announced while the user is still typing

- **Guide**: `forms/forms.md` §4: "**DON'T** trigger validation errors or `aria-live` error announcements on every keystroke while the user is typing (validate on submit or `blur`, and clear errors on `input` once resolved)." `forms/validate-input-after-interaction`: ":user-invalid … defer[s] the error state until the user has 'committed' to a value (by modifying and blurring the field) or attempted to submit the form." From the same guide: "Don't make each error message a live region: a live region that is shown, rather than changed, isn't announced reliably."
- **Where**:
  - Theme editor hex fields: `setColour` (`pages/settings/themes/theme-editor-dialog.ts:278-281`) marks the field touched on every `input`. Typing `#1` instantly shows "Use #rgb or #rrggbb" as `role="alert"` (`:261`). The hex `<input>` itself (`:237`) gets no `aria-invalid` and no `aria-describedby`, so the error isn't tied to it.
  - Theme name and New project fields: `setName` (`theme-editor-dialog.ts:269`) and `#setBookTitle`/`#setTitle`/`#setAuthor` (`new-project-dialog.ts:176-189`) touch on `input`. Clearing a field to retype it fires a "required" alert mid-edit (`new-project-dialog.ts:164`, `role=alert`).
  - Settings form: `ui/settings-form.ts:199` renders the number error as `role="alert"` from a validity computed on every `input`, so typing `-` or deleting digits announces "Must be a number ≥ …" mid-entry. The error isn't linked to the input either: there's no `aria-describedby`, and the control has no `aria-invalid`. Invalid shows only as a class on the wrapper (`:161`).
  - The Field partial (`ui/field.ts:47`) makes every error `role="alert"`. It's fine as a primitive, but it's what makes each case above an assertive interruption.
- **What goes wrong today**: sighted users see red error text flash up while they're halfway through a valid value. Screen reader users get an assertive interruption per invalid keystroke state, and in the theme editor and settings form, when they come back to the field later, the error isn't read with it.
- **Fix**: touch a field on `blur` (or `change`), not `input`; keep clearing on `input` (the `validDraft` recompute already does that once touched). In `settings-form.ts`, give the error an id, bind `aria-describedby` and `aria-invalid` on the control while it's shown, and drop `role="alert"` in favour of focus-on-submit (K2) where there is a submit. The guide's full pattern (native `:user-invalid` plus a single polite live region with a 500 ms delay) is about 60 lines. It's optional: the app's signal-based touched state does the same job once it's driven by blur. Native `:user-invalid` would need `required`/`pattern` on the inputs and a `<form>`, so it's a bigger change than needed.
- **Firefox**: blur/aria changes need nothing. If triage picks `:user-invalid`, the guide's data: "Widely available … Chrome 119, Firefox 88."
- **Size**: M. **Shared primitive**: yes (Settings form; Field if `role=alert` changes). Proof: type `#1` into a theme hex field, assert no error until Tab, then the error is shown and the input has `aria-invalid="true"` and `aria-describedby` pointing at it, in both browsers.
- **Verdict**: **keep**. Overlaps the accessibility area (error announcement; see map "Cross-area overlaps"). One triage should own it.

### K4. Settings form labels aren't `<label>`s; clicking a switch's label text does nothing

- **Guide**: `forms/forms.md` §2: "**DO** always associate `<label>` with its input using `for` and `id`." and "**DON'T** use `aria-label` when a visible `<label>` can be used". `ui-components/checkbox`: "each checkbox associated with its label, either by nesting the `<input>` inside the `<label>` or by matching the label's `for` attribute to the input's `id`."
- **Where**: `ui/settings-form.ts:177`. The visible name is a `<span class="r2m-settings-form__label">`, and every control names itself with `aria-label` instead (`:216`, `:229`, `:247`, `:257`, `:273`, `:283`). The switch's `<label class="r2m-switch">` (`:243`) wraps only the bare input, with no text.
- **What goes wrong today**: clicking the field's visible name doesn't focus the number box or toggle the switch, so the hit target is only the 36×20 px track. That's a pointer and touch problem on every provider, LLM and audio settings page, since they all render through this element. The names do match (aria-label = label text), so voice control and screen readers are fine. It's only the click target that's wrong.
- **Fix**: give each control an id (`${uid}-${field.key}`) and render the label as `<label for=…>`. Drop the duplicate `aria-label`s, except the range's "… slider", which needs its own name. Watch the E2E locators: primitives.md says the controls are "located by `aria-label` = the field label", and `getByLabel` will keep working with a real label.
- **Firefox**: nothing new.
- **Size**: S. **Shared primitive**: yes (Settings form). Proof: click the label text of a boolean field and assert the switch toggled; click a number field's label and assert focus, in both browsers.
- **Verdict**: **keep**.

### K5. The Switch primitive has no disabled look and may vanish in forced colours

- **Guide**: `ui-components/checkbox` (the `appearance: none` path): "**DO** provide a `:disabled` style variant that lowers opacity and sets `cursor: not-allowed` to convey inactive states clearly." Its example also carries `@media (forced-colors: active) { background-color: CanvasText; }` on the drawn indicator. `forms/switch` adds: "ensure visible focus styling with a contrast ratio of at least 3:1".
- **Where**: `styles/global.css:306-345` (`.r2m-switch input`: `appearance: none`, the track and knob drawn with `background`). There's no `:disabled` rule and no `forced-colors` rule for it (the only `forced-colors` block in the file is the splitter's, `:1751`).
- **What goes wrong today**: with `appearance: none` the browser no longer greys a disabled checkbox, so a switch disabled by an Edit gate (settings form while viewing History, the project details narrator-only switch through `gated`) looks exactly like a live one, still with `cursor: pointer`. In Windows High Contrast / forced colours, author `background` colours are replaced, and with no border the track, knob and checked state are likely drawn in the same system colour. I haven't rendered this, so the proof must confirm it.
- **Fix**: `&:disabled { opacity: 0.38; cursor: default; }` (matching `.r2m-icon-button:disabled`, `:230`), plus a forced-colors block: a `1px solid CanvasText` border on the track, `CanvasText` for the knob and `Highlight` for the checked track, with `forced-color-adjust: none` as the splitter does. About 12 lines of CSS.
- **Firefox**: `:disabled` ships everywhere. The guides give no compat data for `forced-colors`. From general knowledge (not these guides), it ships in Chromium 89+ and Firefox 89+. Playwright's `forcedColors: 'active'` emulation is Chromium-only, so the forced-colors half can only be proved in Chromium.
- **Size**: S. **Shared primitive**: yes (Switch). Proof: render a disabled switch and assert computed opacity < 1, in both browsers; screenshot under forced colours in Chromium.
- **Verdict**: **keep**.

### K6. Identifier fields keep spellcheck and autocapitalisation on

- **Guide**: `forms/forms.md` §3: "**DO** turn off text correction on fields that hold identifiers rather than prose: `autocorrect="off"` (plus `autocapitalize="none"` and `spellcheck="false"`) on usernames, emails, URLs, codes, and search inputs."
- **Where**: free-text settings that hold identifiers: model ids, base URLs and endpoint paths in `pages/settings/llm/llm-config-editor.ts:287-297` (`textInput`), `pages/settings/providers/provider-config-editor.ts:289`, and the Settings form string field `ui/settings-form.ts:280`. Two inputs already do it right (`theme-editor-dialog.ts:239` hex, `prompt-editor.ts:146`).
- **What goes wrong today**: red spellcheck squiggles under every model id and URL. On a touch keyboard, the first letter is capitalised (`Http://…`, `Qwen-28b`), and that capitalised value is what gets saved and sent to llama/audio.cpp.
- **Fix**: add `spellcheck="false" autocapitalize="none" autocorrect="off"` to those inputs. For the Settings form, do it on the `string` kind only, not `text` (which is prose such as prompts and sample text).
- **Firefox**: the guide gives no compat data. `spellcheck` is honoured by both browsers; `autocapitalize`/`autocorrect` only matter on mobile keyboards and are ignored otherwise. It's harmless either way.
- **Size**: S. **Shared primitive**: yes (Settings form). The proof is an attribute assertion, so a unit spec may be enough, without an E2E.
- **Verdict**: **keep (low)**. It's a small robustness gain, and the capitalisation half does change saved data on touch devices.

## Findings: drop

| # | Guide | What | Why dropped |
|---|---|---|---|
| D1 | `forms/switch` | Use `<input type=checkbox switch>` | The guide's data: "Supported by: Safari 17.4. Unsupported in: Chrome, Edge, and Firefox." The fallback is the `input-switch-polyfill` package, a dependency (ADR 0014). The app already does what the polyfill does: `role="switch"` on every switch plus `appearance: none` styling (`global.css:306`). Already done. |
| D2 | `html/form-associated-custom-elements` | Make custom controls FACE (`formAssociated`, `ElementInternals`) | No surface: the app has no `<form>`, so nothing submits, resets or restores. `r2m-inline-edit`, `r2m-splitter`, `r2m-settings-form` and `r2m-file-drop` hand values on through events, and disabling goes through the Edit gate's `block`, not `fieldset[disabled]`. If K2 adds `<form>`s, they'd hold native inputs only. |
| D3 | `ui-components/checkbox`, `forms/brand-consistent-forms` | `accent-color` on native checkbox/radio/range; `color-scheme` | Already done: `global.css:299` (checkbox), `:361` (radio), `:2113` (range), `:29` `color-scheme: light dark`, plus per-page uses (`book.css:272`, `structure-tree.css:53`). |
| D4 | `ui-components/checkbox` | 44 px hit target on the label | Style/density: the app is M3 density −1 by design (primitives.md). The real hit-target gap is the settings switch with no label, covered by K4. |
| D5 | `forms/form-fields-automatically-fit-contents` | `field-sizing: content` | Already done for textareas (`global.css:258` with `min-height: 3lh; max-height: 12lh`, and `:2290`). Single-line inputs fill their column on purpose. |
| D6 | `forms/slider` | Native range; styled filled track; `aria-valuetext` | Native `<input type=range>` is already used (`global.css:2107`). A filled-track gradient is style only (Chromium needs JS to sync a custom property). A unit for `aria-valuetext`: every slider sits beside its number box (`settings-form.ts:225`), so the value is already on screen. |
| D7 | `forms/branded-select-styling` | `appearance: base-select` | Already done under `@supports` (`global.css:412`); Firefox keeps the native picker. The guide says "Unsupported in: Firefox" and the degrade is graceful. |
| D8 | `forms/animated-select-picker` | Animate the picker with `@starting-style` | Motion, not behaviour, and only on base-select (Chromium). Belongs to the motion area if anywhere. |
| D9 | `forms/custom-select-picker-layouts`, `forms/rich-media-picker` | Grid or rich options in base-select | No surface: every `<option>` is plain text. The selected-option rule ("there must be a way to visibly identify which option is selected") is already met: the app keeps the default `::checkmark` and adds `option:checked` background (`global.css:427`). |
| D10 | `forms/select-menu-interaction` | `required` select with a placeholder plus `:user-invalid` | The placeholder selects exist (`add-voice-rule-dialog.ts:194`, `narrator-banner.ts:156`), but there's no form submit to trigger `:user-invalid`. Their real problem, a disabled Add rule with no reason, is K2. |
| D11 | `forms/autofill-highlight-inputs` | Style `:autofill` | No surface: no field is autofill-shaped (secrets use `autocomplete="new-password"`, the rest `off`), and the guide's data says "Unsupported in: Firefox … No workaround". |
| D12 | `forms/forms.md` §6 | Hide inputs with `.visually-hidden`, not `display: none` | `r2m-file-drop__input` is `display: none` (`global.css:1652`) with `aria-hidden` and `tabindex=-1`, but it's not the control: the visible "Choose file" button is, and it calls `.click()` on it (`file-drop.ts:120`). That's correct as is. |
| D13 | `forms/forms.md` §1 | Wrap search/filter controls in `<search>` | `cast-page.ts:227`, `pronunciations-page.ts:153` and `config-list.ts:86` are filter bars without a `<search>` landmark. It's a small landmark gain for screen reader navigation, but it fits better in the accessibility area's landmark review, and the guide gives no compat data for the browser gate. Hand it there rather than ticket it here. |
| D14 | `forms/forms.md` §2 | Don't rely on `placeholder` alone | The search boxes above use `placeholder="Search"` plus an `aria-label`, beside a search icon (config list, speaker menu). That's the common, accepted search-box pattern; no behaviour changes. |

## Guides read with no surface in the app

- `html/form-associated-custom-elements`: no `<form>` (D2).
- `forms/autofill-highlight-inputs`: no autofill fields (D11).
- `forms/custom-select-picker-layouts`, `forms/rich-media-picker`: text-only options (D9).
- `forms/forms.md` §8–§11 (identity/payments, address collection, analytics, multi-page forms): no sign-in, payment, address or wizard form. The Pipeline is a step list, not a multi-page form.
- Skipped as the ticket allowed, after a grep found no matching fields: `forms/autofill-address-form`, `autofill-payment-form`, `autofill-sign-in-form`, `autofill-sign-up-form`, `sms-otp-form`. The only password-type inputs are API-key secrets (`settings-form.ts:281`, `llm-config-editor.ts:185`), already `autocomplete="new-password"`.

## Notes for triage

- **Order by value**: K1 (data saved wrong, tiny fix) → K2 (dead Create button, M) → K3 (error timing and linkage, M) → K4, K5 (S, shared primitives) → K6 (low).
- **K2 and K3 touch the same dialogs** (new project, theme editor). Doing them in one PR would avoid touching those files twice, but that breaks "one guide area per PR" only if triage counts them as different areas. Both are `forms/forms.md` §4.
- **Cross-area overlaps**: K3's error announcement overlaps accessibility. D13 (`<search>` landmark) is handed to accessibility. K5's switch knob `transition` (`global.css:332`) has no reduced-motion handling, which the motion audit (#454) already lists.
- **Conventions to update with the fixes**: K2 changes a habit, so `docs/agents/web/user-messages.md` (blocked reasons) or `conventions.md` should say that a dialog's primary button stays enabled and validates on click. K4 changes how Settings form controls are located (primitives.md row).
