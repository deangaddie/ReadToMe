import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { ActivityStore } from '@app/activity/activity-store';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import type { JobView } from '@app/ui/job';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { settle } from '../../../../testing/fake-navigation';
import type { GeneratePromptDialog } from './generate-prompt-dialog';
import { openGeneratePromptDialog } from './generate-prompt-dialog';
import './generate-prompt-dialog';

const RENDER = '/api/projects/dune/characters/alice/design-prompt/render';
const GENERATE = '/api/projects/dune/characters/alice/design-prompt/generate';

class FakeFeed {
  acquired = 0;
  released = 0;
  readonly events = signal([]);
  readonly maxUnits = 50;
  acquire() {
    this.acquired++;
  }
  release() {
    this.released++;
  }
}

let api: FakeApi;
let feed: FakeFeed;
let jobs: JobView[];
let unregistered: number;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  feed = new FakeFeed();
  override(LlmStreamFeed, feed as unknown as LlmStreamFeed);
  jobs = [];
  unregistered = 0;
  override(ActivityStore, {
    registerLocalJob: (job: JobView) => {
      jobs.push(job);
      return () => unregistered++;
    },
  } as unknown as ActivityStore);
});
afterEach(() => document.body.replaceChildren());

async function open() {
  const result = openGeneratePromptDialog({
    folder: 'dune',
    characterId: 'alice',
    characterName: 'Alice',
  });
  await settle();
  const dialog = document.querySelector<GeneratePromptDialog>('r2m-generate-prompt-dialog')!;
  await dialog.rendered();
  return { result, dialog };
}

const phase = (dialog: Element) => dialog.querySelector('[data-phase]')?.getAttribute('data-phase');

describe('r2m-generate-prompt-dialog', () => {
  it('renders the prompt, lets the user edit it, and answers the generated design prompt', async () => {
    api.on('POST', RENDER, { prompt: 'Describe Alice' });
    api.on('POST', GENERATE, { designPrompt: 'A warm alto' });
    const { result, dialog } = await open();
    expect(feed.acquired).toBe(1);
    expect(api.calls('POST', RENDER)).toHaveLength(1);
    await settle();
    await dialog.rendered();
    expect(phase(dialog)).toBe('edit');
    const textarea = dialog.querySelector<HTMLTextAreaElement>('textarea')!;
    expect(textarea.value).toBe('Describe Alice');
    textarea.value = 'Describe Alice briefly';
    textarea.dispatchEvent(new Event('input'));
    dialog.querySelector<HTMLButtonElement>('[data-action=send]')!.click();
    expect(jobs.map((j) => [j.kind, j.label])).toEqual([['voicePrompt', 'Voice prompt · Alice']]);
    expect(await result).toEqual({ designPrompt: 'A warm alto' });
    expect(api.calls('POST', GENERATE)[0]?.body).toEqual({ prompt: 'Describe Alice briefly' });
    expect(unregistered).toBe(1);
    await settle();
    expect(feed.released).toBe(1);
  });

  it('Send is off on a blank prompt; a failed generation shows the error and returns to edit', async () => {
    api.on('POST', RENDER, { prompt: '' });
    api.on('POST', GENERATE, () => problem(422, 'LLM down'));
    const { dialog } = await open();
    await settle();
    await dialog.rendered();
    const send = () => dialog.querySelector<HTMLButtonElement>('[data-action=send]')!;
    expect(send().disabled).toBe(true);
    const textarea = dialog.querySelector<HTMLTextAreaElement>('textarea')!;
    textarea.value = 'x';
    textarea.dispatchEvent(new Event('input'));
    await dialog.rendered();
    expect(send().disabled).toBe(false);
    send().click();
    await dialog.rendered();
    expect(phase(dialog)).toBe('generating');
    await settle();
    await dialog.rendered();
    expect(phase(dialog)).toBe('edit');
    expect(dialog.querySelector('[role=alert]')?.textContent).toContain('LLM down');
  });

  it('a failed render still lands in edit with the error shown', async () => {
    api.on('POST', RENDER, () => problem(500, 'template broke'));
    const { dialog } = await open();
    await settle();
    await dialog.rendered();
    expect(phase(dialog)).toBe('edit');
    expect(dialog.querySelector('[role=alert]')?.textContent).toContain('template broke');
  });

  it('Cancel answers null and drops a late answer', async () => {
    let answer!: (v: unknown) => void;
    api.on('POST', RENDER, () => new Promise((r) => (answer = r)));
    const { result, dialog } = await open();
    Array.from(dialog.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click();
    expect(await result).toBeNull();
    answer({ prompt: 'late' });
    await settle();
    expect(dialog.prompt()).toBe('');
  });
});
