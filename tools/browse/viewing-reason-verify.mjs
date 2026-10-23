// Ticket 35: while a History step is viewed, the voice card's Advanced settings, its reference
// file drop, the overview's cover drop and the voice editor's ticks and dials are off with the
// viewing reason; on the current project they work as before. One-off check, not a suite.
import { api, launch, openWeb, post, throwawayProject } from './browse.mjs';

const { page, close } = await launch();
const project = await throwawayProject('I\n\n"Hello," said Alice.');
const f = project.folder;
const out = {};
try {
  const alice = (await post(`/api/projects/${f}/commands`, { type: 'CreateCharacter', name: 'Alice' }))
    .newEntityId;
  const voiceId = (
    await post(`/api/projects/${f}/commands`, {
      type: 'CreateVoice',
      characterId: alice,
      name: 'Main',
      isGenerated: false,
    })
  ).newEntityId;
  // One second of a 440 Hz tone as 16-bit mono WAV, uploaded through the card's own file drop.
  const rate = 16000;
  const wav = Buffer.alloc(44 + rate * 2);
  wav.write('RIFF', 0);
  wav.writeUInt32LE(36 + rate * 2, 4);
  wav.write('WAVEfmt ', 8);
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20);
  wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(rate, 24);
  wav.writeUInt32LE(rate * 2, 28);
  wav.writeUInt16LE(2, 32);
  wav.writeUInt16LE(16, 34);
  wav.write('data', 36);
  wav.writeUInt32LE(rate * 2, 40);
  for (let i = 0; i < rate; i++) wav.writeInt16LE(Math.round(8000 * Math.sin((2 * Math.PI * 440 * i) / rate)), 44 + i * 2);
  await openWeb(page, `/projects/${f}/cast/${alice}`);
  await page.locator('r2m-voice-card summary.voice-card__header').first().click();
  await page
    .locator('r2m-voice-card .r2m-file-drop__input')
    .setInputFiles({ name: 'tone.wav', mimeType: 'audio/wav', buffer: wav });
  await page.waitForSelector('r2m-voice-card r2m-audio-player', { timeout: 60_000 });

  // One more edit, so the step with the audio is not the tip and can be viewed.
  const withAudio = (await api(`/api/projects/${f}/history`)).headStepId;
  await post(`/api/projects/${f}/commands`, { type: 'AddCharacterAlias', characterId: alice, name: 'Al' });

  // Viewing lives in memory: enter it from the History sheet, then move by in-app navigation.
  let viewing = false;
  const go = async (path) => {
    if (!viewing) return openWeb(page, path);
    await page.evaluate((url) => navigation.navigate(url), `/app${path}`);
  };

  const state = (sel) =>
    page.evaluate((sel) => {
      const reasonOf = (el) => el?.closest('[data-tooltip]')?.getAttribute('data-tooltip') ?? null;
      return Array.from(document.querySelectorAll(sel)).map((el) => ({
        disabled: el.disabled,
        reason: reasonOf(el),
      }));
    }, sel);

  async function castCard() {
    await go(`/projects/${f}/cast/${alice}`);
    const card = page.locator('r2m-voice-card').first();
    await card.waitFor();
    await card.locator('summary.voice-card__header').click();
    await card.locator('[data-section=advanced] > summary').click();
    await page.waitForSelector('r2m-voice-card r2m-file-drop button');
    // The override editor loads the provider schema; wait for its form or its note.
    await page.waitForSelector('r2m-voice-override-editor r2m-settings-form, r2m-voice-override-editor .voice-override__note');
    return {
      chooseFile: await state('r2m-voice-card r2m-file-drop button'),
      formFields: (await state('r2m-voice-override-editor r2m-settings-form input, r2m-voice-override-editor r2m-settings-form select')).slice(0, 3),
      save: await state('[data-action=save-override]'),
      readOnlyClass: await page.locator('r2m-voice-card r2m-file-drop').evaluate((el) =>
        el.classList.contains('r2m-file-drop--read-only'),
      ),
    };
  }

  async function editor() {
    await go(`/projects/${f}/voices/${voiceId}/editor`);
    await page.waitForSelector('[data-tick]', { timeout: 10_000 }).catch(async (e) => {
      console.log('editor page:', (await page.locator('main').innerText()).slice(0, 600), page.errors);
      throw e;
    });
    await page.locator('.voice-editor__step').first().click();
    await page.waitForSelector('.voice-editor__dials r2m-settings-form, .voice-editor__dials p');
    return {
      ticks: (await state('[data-tick]')).slice(0, 2),
      dials: (await state('.voice-editor__dials r2m-settings-form input')).slice(0, 2),
    };
  }

  async function cover() {
    await go(`/projects/${f}`);
    await page.waitForSelector('r2m-project-details-panel r2m-file-drop button');
    return state('r2m-project-details-panel r2m-file-drop button');
  }

  out.current = { card: await castCard(), editor: await editor(), cover: await cover() };
  await page.locator('[aria-label="History"]').click();
  await page.locator(`.history-sheet__row[data-step-id="${withAudio}"] .history-sheet__step`).click();
  await page.waitForSelector(`.history-sheet__row--viewing[data-step-id="${withAudio}"]`);
  await page.locator('[aria-label="Close History"]').click();
  viewing = true;
  out.viewing = { cover: await cover(), card: await castCard(), editor: await editor() };
  console.log(JSON.stringify(out, null, 2));
  console.log('toasts', page.toasts, 'errors', page.errors);
} finally {
  await project.remove();
  await close();
}
