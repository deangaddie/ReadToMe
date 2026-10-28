import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { type SetupCheckReport, SetupCheckApi } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { settle } from '../../../../testing/fake-navigation';
import type { SetupCheckPage } from './setup-check-page';
import './setup-check-page';

const AT_START = '2026-10-28T14:02:00Z';
const LATER = '2026-10-28T14:09:00Z';

function report(quickCheckedAt: string, ffmpeg: 'passed' | 'failed' = 'failed'): SetupCheckReport {
  return {
    quickCheckedAt,
    fullCheckedAt: null,
    rows: [
      {
        id: 'workspace',
        group: 'workspaceAndTools',
        name: 'Workspace',
        tier: 'quick',
        result: 'passed',
        expected: 'An absolute, writable folder (Workspace:FolderPath)',
        found: 'D:\\WS',
        note: "The folder didn't exist, so the app created it.",
        fix: {
          text: 'Set Workspace:FolderPath, then restart the app.',
          link: null,
          copy: 'C:\\app\\appsettings.Local.json',
        },
      },
      {
        id: 'ffmpeg',
        group: 'workspaceAndTools',
        name: 'ffmpeg',
        tier: 'quick',
        result: ffmpeg,
        expected: 'ffmpeg -version and ffprobe -version from PATH',
        found: 'ffmpeg: The system cannot find the file specified.',
        note: null,
        fix: {
          text: 'Set the ffmpeg path to an ffmpeg with ffprobe beside it.',
          link: { path: 'settings/audio', label: 'Settings → Audio processing' },
          copy: null,
        },
      },
      {
        id: 'docker',
        group: 'workspaceAndTools',
        name: 'docker CLI',
        tier: 'quick',
        result: 'passed',
        expected: 'docker on PATH',
        found: 'Docker version 28.4.0',
        note: null,
        fix: { text: 'Install Docker Desktop.', link: null, copy: 'docker --version' },
      },
    ],
  };
}

let calls: string[];
let quickAnswer: SetupCheckReport;

beforeEach(() => {
  resetServices();
  calls = [];
  quickAnswer = report(AT_START);
  override(SetupCheckApi, {
    report: async () => {
      calls.push('report');
      return report(AT_START);
    },
    runQuick: async () => {
      calls.push('quick');
      return quickAnswer;
    },
  } as unknown as SetupCheckApi);
  override(ToastService, {
    success: () => undefined,
    warn: () => undefined,
    problem: () => undefined,
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

async function flush(page: SetupCheckPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
}

async function mount() {
  const page = document.createElement('r2m-setup-check-page') as SetupCheckPage;
  document.body.append(page);
  await flush(page);
  return page;
}

const rowOf = (page: Element, id: string) =>
  page.querySelector(`.setup-check__row[data-check=${id}]`);

describe('r2m-setup-check-page', () => {
  it('reads the report and runs a Quick check on open', async () => {
    await mount();
    expect(calls).toEqual(['report', 'quick']);
  });

  it('shows one row per check under its group, with expected, found and result', async () => {
    const page = await mount();

    expect(page.querySelector('.setup-check__group')?.textContent?.trim()).toBe(
      'Workspace and tools',
    );
    expect(
      Array.from(page.querySelectorAll('.setup-check__row')).map((r) =>
        r.getAttribute('data-check'),
      ),
    ).toEqual(['workspace', 'ffmpeg', 'docker']);
    const ffmpeg = rowOf(page, 'ffmpeg');
    expect(ffmpeg?.classList.contains('setup-check__row--failed')).toBe(true);
    expect(ffmpeg?.querySelector('.r2m-status-chip')?.textContent).toContain('Failed');
    expect(ffmpeg?.textContent).toContain('ffmpeg -version and ffprobe -version from PATH');
    expect(ffmpeg?.textContent).toContain('ffmpeg: The system cannot find the file specified.');
  });

  it('shows the fix hint on a failed row and a dash on a passed one, notes under Found', async () => {
    const page = await mount();

    expect(
      rowOf(page, 'ffmpeg')?.querySelector('a.setup-check__fix-link')?.getAttribute('href'),
    ).toBe('settings/audio');
    const workspace = rowOf(page, 'workspace');
    expect(workspace?.querySelector('.setup-check__fix')).toBeNull();
    expect(workspace?.textContent).toContain('—');
    expect(workspace?.querySelector('[data-role=note]')?.textContent).toContain(
      "The folder didn't exist",
    );
    expect(rowOf(page, 'docker')?.querySelector('.setup-check__fix')).toBeNull();
  });

  it('Run Quick check refreshes the rows and the summary time', async () => {
    const page = await mount();
    const before = page.querySelector('[data-role=summary]')?.textContent;
    expect(before).toContain('1 failed · 2 passed');

    quickAnswer = report(LATER, 'passed');
    page.querySelector<HTMLButtonElement>('[data-action=run-quick]')?.click();
    await flush(page);

    expect(calls).toEqual(['report', 'quick', 'quick']);
    const after = page.querySelector('[data-role=summary]')?.textContent;
    expect(after).toContain('3 passed');
    expect(after).not.toBe(before);
    expect(rowOf(page, 'ffmpeg')?.getAttribute('data-result')).toBe('passed');
  });
});
