import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { SetupFixHint } from '@app/api';
import { fixHint } from './fix-hint';

afterEach(() => document.body.replaceChildren());

function mount(fix: SetupFixHint, onCopy: (text: string) => void = () => undefined) {
  const host = document.createElement('div');
  document.body.append(host);
  render(fixHint(fix, onCopy), host);
  return host;
}

describe('fixHint', () => {
  it('renders an in-app fix as the sentence and a link to its Settings section', () => {
    const host = mount({
      text: 'Set the ffmpeg path.',
      link: { path: 'settings/audio', label: 'Settings → Audio processing' },
      copy: null,
    });

    expect(host.querySelector('.setup-check__fix-text')?.textContent).toBe('Set the ffmpeg path.');
    const link = host.querySelector('a.setup-check__fix-link');
    expect(link?.getAttribute('href')).toBe('settings/audio');
    expect(link?.textContent).toContain('Settings → Audio processing');
    expect(host.querySelector('[data-action=copy]')).toBeNull();
  });

  it('renders a fix outside the app as a copy button holding the command or path', () => {
    const copied: string[] = [];
    const host = mount(
      {
        text: 'Set Workspace:FolderPath, then restart the app.',
        link: null,
        copy: 'C:\\app\\appsettings.Local.json',
      },
      (text) => copied.push(text),
    );

    expect(host.querySelector('a')).toBeNull();
    const button = host.querySelector<HTMLButtonElement>('[data-action=copy]');
    expect(button?.querySelector('code')?.textContent).toBe('C:\\app\\appsettings.Local.json');
    expect(button?.getAttribute('aria-label')).toBe('Copy C:\\app\\appsettings.Local.json');
    button?.click();
    expect(copied).toEqual(['C:\\app\\appsettings.Local.json']);
  });

  it('renders both when a fix has a link and something to copy', () => {
    const host = mount({
      text: 'The appsettings path answers: use it as the ffmpeg path.',
      link: { path: 'settings/audio', label: 'Settings → Audio processing' },
      copy: 'D:\\ffmpeg\\bin\\ffmpeg.exe',
    });

    expect(host.querySelector('a.setup-check__fix-link')).not.toBeNull();
    expect(host.querySelector('[data-action=copy] code')?.textContent).toBe(
      'D:\\ffmpeg\\bin\\ffmpeg.exe',
    );
  });
});
