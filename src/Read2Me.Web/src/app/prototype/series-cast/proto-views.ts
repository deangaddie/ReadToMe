/**
 * PROTOTYPE — throwaway (#208). A fake Cast page for one book of the Sherlock Holmes series, with
 * three variants (?variant=A|B|C) of where series linking lives. Fake data only.
 */
import { html, nothing } from 'lit-html';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { icon } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import * as S from './proto-store';
import protoCss from './proto.css' with { type: 'text' };
import './proto-switcher';

adoptStyles(protoCss);

const confirm = () => use(ConfirmService);
const toast = () => use(ToastService);
const list = (xs: string[]) =>
  xs.length <= 1 ? (xs[0] ?? '') : `${xs.slice(0, -1).join(', ')} and ${xs.at(-1)}`;

// --- Flows -------------------------------------------------------------------------------------

async function confirmLink(folder: string, c: S.Character, scId: string): Promise<boolean> {
  const sc = S.seriesChar(scId);
  const { bookGains, seriesGains } = S.mergePreview(folder, c, scId);
  const others = S.linkedPairs(scId).filter((p) => p.book.folder !== folder);
  const names = (vs: S.Voice[]) =>
    vs.length ? vs.map((x) => `"${x.name}"`).join(', ') : 'nothing';
  return confirm().confirm({
    title: `Link ${c.name} to ${sc.label}?`,
    message:
      `${c.name} will share Voices with ${list(others.map((p) => `${p.character.name} in ${p.book.title}`))}. ` +
      `This book gains ${names(bookGains)}; the other books gain ${names(seriesGains)}. ` +
      `Names, aliases, delivery cues and Voice Rules stay this book's own.`,
    confirmLabel: 'Link',
  });
}

async function linkTo(folder: string, c: S.Character, scId: string): Promise<void> {
  if (!(await confirmLink(folder, c, scId))) return;
  S.link(folder, c.id, scId);
  toast().success(`${c.name} is linked to ${S.seriesChar(scId).label}`);
}

async function pickAndLink(folder: string, c: S.Character): Promise<void> {
  const d = document.createElement('r2m-proto-pick-series-char');
  d.folder = folder;
  d.characterId = c.id;
  const choice = await openDialog<string>(d);
  if (!choice) return;
  if (choice === '__new') {
    const ok = await confirm().confirm({
      title: `Share ${c.name} across the series?`,
      message: `A series character "${c.name}" is made, owning this Character's ${c.voices.length} Voices. Characters in the other books can then link to it.`,
      confirmLabel: 'Share',
    });
    if (ok) S.shareAsNew(folder, c.id);
    return;
  }
  await linkTo(folder, c, choice);
}

async function unlink(folder: string, c: S.Character): Promise<void> {
  const others = S.linkedPairs(c.link!).filter((p) => p.book.folder !== folder);
  const ok = await confirm().confirm({
    title: `Unlink ${c.name} from ${S.seriesChar(c.link!).label}?`,
    message:
      `This book keeps its own copy of ${c.voices.length} Voices, so nothing here breaks. ` +
      `From now on, Voice edits here no longer reach ${list(others.map((p) => p.book.title)) || 'other books'}, and theirs no longer reach this book.`,
    confirmLabel: 'Unlink',
  });
  if (ok) S.unlink(folder, c.id);
}

async function renameVoice(folder: string, c: S.Character, voice: S.Voice): Promise<void> {
  const name = await use(PromptService).text({
    title: 'Rename Voice',
    label: 'Name',
    initial: voice.name,
    required: true,
    confirmLabel: 'Rename',
    ...(c.link
      ? { hint: `Shared: saved in ${list(S.linkedPairs(c.link).map((p) => p.book.title))}.` }
      : {}),
  });
  if (!name || name === voice.name) return;
  const titles = S.editVoice(folder, c.id, voice.id, name);
  toast().success(titles.length > 1 ? `Saved in ${titles.length} books` : 'Saved');
}

async function deleteVoice(folder: string, c: S.Character, voice: S.Voice): Promise<void> {
  const blockers = S.deleteBlockers(folder, c, voice.id);
  if (blockers.length) {
    // Normally unreachable: the button is disabled with this advice. Shown for completeness.
    toast().error(`Can't delete "${voice.name}": Voice Rules use it in ${list(blockers)}.`);
    return;
  }
  const titles = c.link ? S.linkedPairs(c.link).map((p) => p.book.title) : [S.book().title];
  const ok = await confirm().confirm({
    title: `Delete "${voice.name}"${titles.length > 1 ? ` from ${titles.length} books` : ''}?`,
    message:
      (titles.length > 1 ? `It is a series Voice, so it is removed from ${list(titles)}. ` : '') +
      "Each book's History can bring it back in that book only. Any default rule using it moves to that book's oldest remaining Voice.",
    destructive: true,
    confirmLabel: 'Delete Voice',
  });
  if (ok) S.deleteVoice(folder, c.id, voice.id);
}

async function push(folder: string, c: S.Character): Promise<void> {
  const others = S.linkedPairs(c.link!).filter((p) => p.book.folder !== folder);
  const ok = await confirm().confirm({
    title: "Push this book's Voices to the series?",
    message: `${c.name}'s Voices in ${list(others.map((p) => p.book.title))} are replaced by this book's. Each of those books records a History step.`,
    destructive: true,
    confirmLabel: 'Push to series',
  });
  if (ok) S.push(folder, c.id);
}

async function take(folder: string, c: S.Character): Promise<void> {
  const ok = await confirm().confirm({
    title: "Take the series' Voices?",
    message: `${c.name}'s Voices in this book are replaced by the series' copies. This book records a History step, so you can Restore it.`,
    destructive: true,
    confirmLabel: "Take series' Voices",
  });
  if (ok) S.take(folder, c.id);
}

// --- Pieces ------------------------------------------------------------------------------------

function rowBadges(folder: string, c: S.Character) {
  const sugg = S.suggestionsFor(folder).some((s) => s.character.id === c.id);
  return html`${c.link ? html`<span class="sx-chip sx-chip--link" data-tooltip=${`Series: ${S.seriesChar(c.link).label}`}>${icon('link')}</span>` : nothing}
  ${S.differs(folder, c) ? html`<span class="sx-chip sx-chip--warn" data-tooltip="Differs from series">${icon('sync_problem')}</span>` : nothing}
  ${sugg && S.variant() !== 'B' ? html`<span class="sx-chip sx-chip--sugg" data-tooltip="Suggested series link">${icon('auto_awesome')}</span>` : nothing}`;
}

function voiceCard(folder: string, c: S.Character, voice: S.Voice) {
  const blockers = S.deleteBlockers(folder, c, voice.id);
  const shared = c.link ? S.linkedPairs(c.link).length : 0;
  const isDefault = c.rules.some((r) => r.kind === 'default' && r.voiceId === voice.id);
  return html`<article class="sx-voice">
    <div class="sx-voice__head">
      ${icon('record_voice_over')}
      <strong>${voice.name}</strong>
      ${isDefault ? html`<span class="sx-tag">Default</span>` : nothing}
      ${shared > 1 ? html`<span class="sx-tag sx-tag--series" data-tooltip=${`Same Voice in ${list(S.linkedPairs(c.link!).map((p) => p.book.title))}`}>${icon('library_books')} Series · ${shared} books</span>` : nothing}
      <span class="sx-spacer"></span>
      <button type="button" class="r2m-icon-button" aria-label="Play">${icon('play_arrow')}</button>
      <button
        type="button"
        class="r2m-icon-button"
        aria-label="Rename"
        @click=${() => void renameVoice(folder, c, voice)}
      >
        ${icon('edit')}
      </button>
      <button
        type="button"
        class="r2m-icon-button"
        aria-label="Delete"
        ?disabled=${blockers.length > 0}
        @click=${() => void deleteVoice(folder, c, voice)}
      >
        ${icon('delete')}
      </button>
    </div>
    <p class="sx-muted">${voice.description}</p>
    ${blockers.length ? html`<p class="sx-advice">${icon('info')} Can't delete: Voice Rules in ${list(blockers)} use it. Remove those rules first.</p>` : nothing}
  </article>`;
}

function rulesSection(c: S.Character) {
  const name = (id: string) => c.voices.find((x) => x.id === id)?.name ?? '?';
  return html`<section class="sx-section">
    <h3 class="sx-h3">Voice rules <span class="sx-muted">this book only</span></h3>
    ${
      c.rules.length
        ? html`<ul class="sx-rules">
            ${c.rules.map((r) => html`<li>${r.kind === 'default' ? html`<strong>Default</strong>` : html`<strong>${r.where}</strong>`} → ${name(r.voiceId)}</li>`)}
          </ul>`
        : html`<p class="sx-muted">No Voice yet.</p>`
    }
  </section>`;
}

function differsCallout(folder: string, c: S.Character) {
  if (!S.differs(folder, c)) return nothing;
  return html`<div class="sx-callout sx-callout--warn">
    ${icon('sync_problem')}
    <div>
      <strong>Differs from series.</strong> This book's copy of ${c.name}'s Voices no longer matches
      the other books (a Restore or a failed write). Nothing resolves it silently.
      <div class="sx-callout__actions">
        <button type="button" class="r2m-button" @click=${() => void push(folder, c)}>
          Push this book's Voices
        </button>
        <button type="button" class="r2m-button" @click=${() => void take(folder, c)}>
          Take the series' Voices
        </button>
      </div>
    </div>
  </div>`;
}

function seriesSectionA(folder: string, c: S.Character) {
  if (c.link) {
    const others = S.linkedPairs(c.link).filter((p) => p.book.folder !== folder);
    return html`<section class="sx-section sx-series">
      <h3 class="sx-h3">
        ${icon('library_books')} Series character: ${S.seriesChar(c.link).label}
      </h3>
      <p class="sx-muted">
        Shares its Voices with
        ${others.length ? list(others.map((p) => `${p.character.name} (Book ${p.book.position}, ${p.book.title})`)) : 'no other book yet'}.
      </p>
      ${differsCallout(folder, c)}
      <button type="button" class="r2m-button" @click=${() => void unlink(folder, c)}>
        ${icon('person_off')} Unlink
      </button>
    </section>`;
  }
  const sugg = S.suggestionsFor(folder).find((s) => s.character.id === c.id);
  return html`<section class="sx-section sx-series">
    <h3 class="sx-h3">${icon('library_books')} Series</h3>
    ${
      sugg
        ? html`<div class="sx-callout sx-callout--sugg">
            ${icon('auto_awesome')}
            <div>
              Same person as <strong>${sugg.via}</strong>?
              <div class="sx-callout__actions">
                <button
                  type="button"
                  class="r2m-button r2m-button--filled"
                  @click=${() => void linkTo(folder, c, sugg.sc.id)}
                >
                  Link to ${sugg.sc.label}
                </button>
                <button type="button" class="r2m-button" @click=${() => S.dismiss(folder, c.id)}>
                  Not the same
                </button>
              </div>
            </div>
          </div>`
        : html`<p class="sx-muted">Not linked: its Voices are this book's alone.</p>`
    }
    <button type="button" class="r2m-button" @click=${() => void pickAndLink(folder, c)}>
      ${icon('link')} Link to a series character…
    </button>
  </section>`;
}

/** B and C: the detail keeps only a compact line; the work happens elsewhere. */
function seriesLineCompact(folder: string, c: S.Character) {
  if (!c.link)
    return S.variant() === 'C'
      ? html`<p class="sx-muted sx-small">
          Not linked to the series.
          <button type="button" class="sx-link" @click=${() => void pickAndLink(folder, c)}>
            Link…
          </button>
        </p>`
      : html`<p class="sx-muted sx-small">
          Not linked to the series. Link it on the series cast board.
        </p>`;
  return html`<div class="sx-series-line">
      <span class="sx-tag sx-tag--series"
        >${icon('library_books')} ${S.seriesChar(c.link).label} · ${S.linkedPairs(c.link).length}
        books</span
      >
      ${S.variant() === 'C' ? html`<button type="button" class="sx-link" @click=${() => void unlink(folder, c)}>Unlink</button>` : html`<span class="sx-muted sx-small">Manage on the series cast board</span>`}
    </div>
    ${differsCallout(folder, c)}`;
}

function detail(folder: string, c: S.Character) {
  return html`<div class="sx-detail">
    <header class="sx-detail__head">
      <h2>${c.name}</h2>
      <span class="sx-muted">${c.lines} lines</span>
      <span class="sx-spacer"></span>
      <button type="button" class="r2m-button r2m-button--stroked">${icon('merge')} Merge</button>
      <button type="button" class="r2m-button r2m-button--stroked">${icon('delete')} Delete</button>
    </header>
    ${S.variant() === 'A' ? nothing : seriesLineCompact(folder, c)}
    <section class="sx-section">
      <h3 class="sx-h3">Aliases <span class="sx-muted">this book only</span></h3>
      <div class="sx-aliases">
        ${c.aliases.map((a) => html`<span class="sx-alias">${a}</span>`)}
      </div>
    </section>
    ${S.variant() === 'A' ? seriesSectionA(folder, c) : nothing}
    <section class="sx-section">
      <h3 class="sx-h3">
        Voices ${c.link ? html`<span class="sx-muted">shared with the series</span>` : nothing}
      </h3>
      ${c.voices.length ? c.voices.map((x) => voiceCard(folder, c, x)) : html`<p class="sx-muted">No Voices yet.</p>`}
      <button type="button" class="r2m-button">${icon('add')} Add Voice</button>
    </section>
    ${rulesSection(c)}
  </div>`;
}

function suggestionsBannerC(folder: string) {
  const sugg = S.suggestionsFor(folder);
  if (!sugg.length) return nothing;
  return html`<div class="sx-callout sx-callout--sugg sx-banner">
    ${icon('auto_awesome')}
    <span
      ><strong>${sugg.length} suggested series ${sugg.length === 1 ? 'link' : 'links'}</strong>:
      Characters here look like ones already linked in other books of Sherlock Holmes.</span
    >
    <span class="sx-spacer"></span>
    <button
      type="button"
      class="r2m-button r2m-button--filled"
      @click=${() => void openDialog(document.createElement('r2m-proto-review-links'))}
    >
      Review
    </button>
  </div>`;
}

function castPage() {
  const folder = S.currentBook();
  const b = S.book();
  const c = S.character();
  return html`
    ${S.variant() === 'C' ? suggestionsBannerC(folder) : nothing}
    <div class="sx-cast">
      <ul class="sx-rows">
        ${b.characters.map(
          (x) =>
            html`<li>
              <button
                type="button"
                class="sx-row ${x.id === c.id ? 'sx-row--selected' : ''}"
                @click=${() => S.selected.set(x.id)}
              >
                ${icon(x.name === 'Narrator' ? 'auto_stories' : 'person')}
                <span class="sx-row__name">${x.name}</span>
                ${rowBadges(folder, x)}
                <span class="sx-muted sx-small">${x.lines}</span>
              </button>
            </li>`,
        )}
      </ul>
      ${detail(folder, c)}
    </div>
  `;
}

// --- Variant B: the series cast board ----------------------------------------------------------

function boardB() {
  const books = S.books();
  const scs = S.seriesChars();
  return html`<section class="sx-board">
    <p class="sx-muted">
      Shown in the Sherlock Holmes panel on the Projects page (Series view) as a "Characters" tab
      beside "Books". Rows are series characters, columns are books in Series position order.
    </p>
    <table class="sx-table">
      <thead>
        <tr>
          <th>Series character</th>
          ${books.map((b) => html`<th>Book ${b.position}<br /><span class="sx-muted sx-small">${b.title}</span></th>`)}
        </tr>
      </thead>
      <tbody>
        ${scs.map(
          (sc) =>
            html`<tr class=${S.isEditing(sc.id) ? 'sx-tr--open' : ''}>
                <th scope="row">
                  <button
                    type="button"
                    class="sx-rowhead"
                    aria-expanded=${S.isEditing(sc.id)}
                    @click=${() => S.toggleEditing(sc.id)}
                  >
                    ${icon(S.isEditing(sc.id) ? 'expand_less' : 'expand_more')} ${sc.label}
                    <span class="sx-muted sx-small"
                      >${S.seriesVoices(sc.id).voices.length} Voices</span
                    >
                  </button>
                </th>
                ${books.map((b) => {
                  const linked = b.characters.find((c) => c.link === sc.id);
                  if (linked)
                    return html`<td>
                      <span class="sx-cell"
                        >${icon('link')} ${linked.name}
                        ${S.differs(b.folder, linked) ? html`<span class="sx-chip sx-chip--warn" data-tooltip="Differs from series">${icon('sync_problem')}</span>` : nothing}
                        <button
                          type="button"
                          class="r2m-icon-button sx-cell__x"
                          aria-label=${`Unlink ${linked.name}`}
                          @click=${() => void unlink(b.folder, linked)}
                        >
                          ${icon('close')}
                        </button>
                      </span>
                    </td>`;
                  const sugg = S.suggestionsFor(b.folder).find((s) => s.sc.id === sc.id);
                  if (sugg)
                    return html`<td class="sx-td--sugg">
                      ${icon('auto_awesome')} ${sugg.character.name}?
                      <button
                        type="button"
                        class="r2m-button"
                        @click=${() => void linkTo(b.folder, sugg.character, sc.id)}
                      >
                        Link
                      </button>
                      <button
                        type="button"
                        class="r2m-icon-button"
                        aria-label="Not the same"
                        @click=${() => S.dismiss(b.folder, sugg.character.id)}
                      >
                        ${icon('close')}
                      </button>
                    </td>`;
                  return html`<td>
                    <select
                      class="sx-select"
                      aria-label=${`Link a ${b.title} Character to ${sc.label}`}
                      @change=${(e: Event) => {
                        const id = (e.target as HTMLSelectElement).value;
                        (e.target as HTMLSelectElement).value = '';
                        const c = b.characters.find((x) => x.id === id);
                        if (c) void linkTo(b.folder, c, sc.id);
                      }}
                    >
                      <option value="">— link…</option>
                      ${b.characters.filter((x) => !x.link).map((x) => html`<option value=${x.id}>${x.name}</option>`)}
                    </select>
                  </td>`;
                })}
              </tr>
              ${
                S.isEditing(sc.id)
                  ? html`<tr class="sx-tr-editor">
                      <td colspan=${books.length + 1}>${seriesVoiceEditor(sc.id)}</td>
                    </tr>`
                  : nothing
              }`,
        )}
      </tbody>
    </table>
    <p class="sx-muted sx-small">
      To add a row, share a Character:
      <button
        type="button"
        class="sx-link"
        @click=${() => void pickAndLink(S.currentBook(), S.character())}
      >
        Share ${S.character().name} (${S.book().title})…
      </button>
    </p>
  </section>`;
}

// --- Series Voice editor (B) -------------------------------------------------------------------

async function addSeriesVoice(scId: string): Promise<void> {
  const name = await use(PromptService).text({
    title: `New Voice for ${S.seriesChar(scId).label}`,
    label: 'Name',
    required: true,
    confirmLabel: 'Add Voice',
    hint: `Added to ${list(S.linkedPairs(scId).map((p) => p.book.title))}. Design or upload its audio next, as on the Cast page.`,
  });
  if (!name) return;
  const titles = S.addSeriesVoice(scId, name, 'New Voice: no audio yet');
  toast().success(`Added to ${titles.length} books`);
}

async function renameSeriesVoice(scId: string, voice: S.Voice): Promise<void> {
  const name = await use(PromptService).text({
    title: 'Rename Voice',
    label: 'Name',
    initial: voice.name,
    required: true,
    confirmLabel: 'Rename',
    hint: `Saved in ${list(S.linkedPairs(scId).map((p) => p.book.title))}.`,
  });
  if (!name || name === voice.name) return;
  toast().success(`Saved in ${S.renameSeriesVoice(scId, voice.id, name).length} books`);
}

async function deleteSeriesVoice(scId: string, voice: S.Voice): Promise<void> {
  const titles = S.linkedPairs(scId).map((p) => p.book.title);
  const ok = await confirm().confirm({
    title: `Delete "${voice.name}" from ${titles.length} books?`,
    message: `It is removed from ${list(titles)}. Each book's History can bring it back in that book only. A book whose default it is moves to its oldest remaining Voice.`,
    destructive: true,
    confirmLabel: 'Delete Voice',
  });
  if (ok) S.deleteSeriesVoice(scId, voice.id);
}

function seriesVoiceEditor(scId: string) {
  const sc = S.seriesChar(scId);
  const { voices, from } = S.seriesVoices(scId);
  const pairs = S.linkedPairs(scId);
  const differing = pairs.filter((p) => S.differs(p.book.folder, p.character));
  return html`<section class="sx-editor" aria-label=${`${sc.label} Voices`}>
    <header class="sx-editor__head">
      <h3 class="sx-h3">${icon('record_voice_over')} ${sc.label}: series Voices</h3>
      <span class="sx-muted sx-small"
        >Every change is saved in each linked book, as a History step there.</span
      >
      <span class="sx-spacer"></span>
      <button
        type="button"
        class="r2m-button r2m-button--filled"
        @click=${() => void addSeriesVoice(scId)}
      >
        ${icon('add')} Add Voice
      </button>
      <button
        type="button"
        class="r2m-icon-button"
        aria-label="Close"
        @click=${() => S.toggleEditing(scId)}
      >
        ${icon('close')}
      </button>
    </header>
    ${
      differing.length
        ? html`<div class="sx-callout sx-callout--warn">
            ${icon('sync_problem')}
            <div>
              <strong>${list(differing.map((p) => p.book.title))} differs from series.</strong>
              Shown here is the copy in ${from?.title}. Edits here are written to every book, which
              also brings the differing book back in line.
              <div class="sx-callout__actions">
                ${differing.map(
                  (p) =>
                    html`<button
                      type="button"
                      class="r2m-button"
                      @click=${() => void push(p.book.folder, p.character)}
                    >
                      Use ${p.book.title}'s copy for all
                    </button>`,
                )}
              </div>
            </div>
          </div>`
        : nothing
    }
    <table class="sx-table sx-vtable">
      <thead>
        <tr>
          <th>Voice</th>
          ${pairs.map((p) => html`<th class="sx-center">Default in Book ${p.book.position}<br /><span class="sx-muted sx-small">as ${p.character.name}</span></th>`)}
          <th></th>
        </tr>
      </thead>
      <tbody>
        ${voices.map((voice) => {
          const blockers = S.seriesDeleteBlockers(scId, voice.id);
          return html`<tr>
            <td>
              <strong>${voice.name}</strong>
              <div class="sx-muted sx-small">${voice.description}</div>
              ${blockers.length ? html`<div class="sx-advice sx-small">${icon('info')} Can't delete: Voice Rules in ${list(blockers)} use it.</div>` : nothing}
            </td>
            ${pairs.map((p) => {
              const has = p.character.voices.some((x) => x.id === voice.id);
              const isDefault = p.character.rules.some(
                (r) => r.kind === 'default' && r.voiceId === voice.id,
              );
              return html`<td class="sx-center">
                <input
                  type="radio"
                  name=${`default-${p.book.folder}-${scId}`}
                  aria-label=${`Default in ${p.book.title}`}
                  .checked=${isDefault}
                  ?disabled=${!has}
                  @change=${() => S.setDefault(p.book.folder, p.character.id, voice.id)}
                />
              </td>`;
            })}
            <td class="sx-nowrap">
              <button type="button" class="r2m-icon-button" aria-label="Play">
                ${icon('play_arrow')}
              </button>
              <button type="button" class="r2m-icon-button" aria-label="Edit">
                ${icon('tune')}
              </button>
              <button
                type="button"
                class="r2m-icon-button"
                aria-label="Rename"
                @click=${() => void renameSeriesVoice(scId, voice)}
              >
                ${icon('edit')}
              </button>
              <button
                type="button"
                class="r2m-icon-button"
                aria-label="Delete"
                ?disabled=${blockers.length > 0 || voices.length === 1}
                @click=${() => void deleteSeriesVoice(scId, voice)}
              >
                ${icon('delete')}
              </button>
            </td>
          </tr>`;
        })}
      </tbody>
    </table>
    <p class="sx-muted sx-small">
      Each book's default is that book's own Voice Rule, set here for convenience. Positional rules
      (e.g. "Part 1, chapter 7") stay on each book's Cast page.
    </p>
  </section>`;
}

// --- Dialogs -----------------------------------------------------------------------------------

export class PickSeriesChar extends R2mElement {
  folder!: string;
  characterId!: string;
  protected template() {
    const b = S.bookByFolder(this.folder);
    const c = b.characters.find((x) => x.id === this.characterId)!;
    const free = S.seriesChars().filter((sc) => !b.characters.some((x) => x.link === sc.id));
    const taken = S.seriesChars().filter((sc) =>
      b.characters.some((x) => x.link === sc.id && x.id !== c.id),
    );
    return html`
      <h2 class="r2m-dialog__title">Link ${c.name} to a series character</h2>
      <div class="r2m-dialog__content sx-pick">
        ${free.map(
          (sc) =>
            html`<button
              type="button"
              class="sx-pick__row"
              @click=${() => this.emit('r2m-close', sc.id)}
            >
              ${icon('library_books')}<span class="sx-pick__name">${sc.label}</span>
              <span class="sx-muted sx-small"
                >${list(S.linkedPairs(sc.id).map((p) => `${p.character.name} (Book ${p.book.position})`))}</span
              >
            </button>`,
        )}
        ${
          taken.length
            ? html`<p class="sx-muted sx-small">
                Already linked in this book:
                ${list(taken.map((sc) => `${sc.label} (${b.characters.find((x) => x.link === sc.id)!.name})`))}.
                If ${c.name} is the same person, merge the two Characters first.
              </p>`
            : nothing
        }
        <button
          type="button"
          class="sx-pick__row sx-pick__row--new"
          @click=${() => this.emit('r2m-close', '__new')}
        >
          ${icon('add')}<span class="sx-pick__name">Share ${c.name} as a new series character</span>
        </button>
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', undefined)}>
          Cancel
        </button>
      </div>
    `;
  }
}
define('r2m-proto-pick-series-char', PickSeriesChar);

export class ReviewLinks extends R2mElement {
  protected template() {
    const folder = S.currentBook();
    const sugg = S.suggestionsFor(folder);
    return html`
      <h2 class="r2m-dialog__title">Suggested series links: ${S.book().title}</h2>
      <div class="r2m-dialog__content sx-review">
        ${sugg.length === 0 ? html`<p>All reviewed.</p>` : nothing}
        ${sugg.map((s) => {
          const { bookGains, seriesGains } = S.mergePreview(folder, s.character, s.sc.id);
          return html`<div class="sx-review__row">
            <div class="sx-review__pair">
              <strong>${s.character.name}</strong> ${icon('compare_arrows')}
              <strong>${s.sc.label}</strong>
              <div class="sx-muted sx-small">matched ${s.via}</div>
              <div class="sx-small">
                Gains here: ${bookGains.map((x) => x.name).join(', ') || 'nothing'} · Gains in the
                other books: ${seriesGains.map((x) => x.name).join(', ') || 'nothing'}
              </div>
            </div>
            <button
              type="button"
              class="r2m-button r2m-button--filled"
              @click=${() => {
                S.link(folder, s.character.id, s.sc.id);
                toast().success(`${s.character.name} linked`);
              }}
            >
              Link
            </button>
            <button
              type="button"
              class="r2m-button"
              @click=${() => S.dismiss(folder, s.character.id)}
            >
              Not the same
            </button>
          </div>`;
        })}
        <p class="sx-muted sx-small">
          Linking here is the confirm: each row shows what both sides gain before you click.
        </p>
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', undefined)}>
          Done
        </button>
      </div>
    `;
  }
}
define('r2m-proto-review-links', ReviewLinks);

// --- Page --------------------------------------------------------------------------------------

export class ProtoSeriesCast extends R2mElement {
  private readonly tab = signal<'cast' | 'board'>('cast');
  protected template() {
    const b = S.book();
    const showBoard = S.variant() === 'B' && this.tab() === 'board';
    return html`
      <header class="r2m-page-header sx-header">
        <h1 class="r2m-page-header__title">
          ${showBoard ? 'Sherlock Holmes · Characters' : 'Cast'}
        </h1>
        <p class="r2m-page-header__subtitle">
          ${showBoard ? 'Series panel on the Projects page' : html`${b.title} · Sherlock Holmes, Book ${b.position}`}
        </p>
        <span class="sx-header__actions">
          ${
            S.variant() === 'B'
              ? html`<div class="sx-toggle">
                  <button
                    type="button"
                    class=${this.tab() === 'cast' ? 'on' : ''}
                    @click=${() => this.tab.set('cast')}
                  >
                    Book cast
                  </button>
                  <button
                    type="button"
                    class=${this.tab() === 'board' ? 'on' : ''}
                    @click=${() => this.tab.set('board')}
                  >
                    Series cast board
                  </button>
                </div>`
              : nothing
          }
          <label class="sx-muted sx-small"
            >Book
            <select
              class="sx-select"
              @change=${(e: Event) => {
                S.currentBook.set((e.target as HTMLSelectElement).value);
                S.selected.set(S.book().characters[1]!.id);
              }}
            >
              ${S.books().map((x) => html`<option value=${x.folder} ?selected=${x.folder === b.folder}>Book ${x.position}: ${x.title}</option>`)}
            </select>
          </label>
        </span>
      </header>
      ${showBoard ? boardB() : castPage()}
      <r2m-proto-series-cast-switcher></r2m-proto-series-cast-switcher>
    `;
  }
}
define('r2m-proto-series-cast', ProtoSeriesCast);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-proto-series-cast': ProtoSeriesCast;
    'r2m-proto-pick-series-char': PickSeriesChar;
    'r2m-proto-review-links': ReviewLinks;
  }
}
