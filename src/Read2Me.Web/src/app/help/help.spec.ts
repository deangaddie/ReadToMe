import { describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { HELP } from './help.generated';
import { helpText } from './help';

function mount(step: Parameters<typeof helpText>[0]): HTMLElement {
  const host = document.createElement('div');
  render(helpText(step), host);
  return host;
}

describe('helpText', () => {
  it('renders a step entry: the title as the heading, the body as real markup', () => {
    const root = mount('pauses').querySelector('.r2m-help');
    expect(root?.querySelector('h2.r2m-help__title')?.textContent).toBe(HELP.pauses.title);
    expect(root?.getAttribute('data-step')).toBe('pauses');
    const body = root?.querySelector('.r2m-help__body');
    const markup = [...(body?.children ?? [])].map((el) => el.outerHTML).join('\n');
    expect(`${markup}\n`).toBe(HELP.pauses.html);
    expect(body?.querySelector('p')).not.toBeNull();
    expect(body?.textContent).not.toContain('<p>');
  });

  it('title: false leaves the heading to the host and drops the aria-labelledby that named it', () => {
    const host = document.createElement('div');
    render(helpText('pauses', { title: false }), host);
    const root = host.querySelector('.r2m-help')!;
    expect(root.querySelector('.r2m-help__title')).toBeNull();
    expect(root.hasAttribute('aria-labelledby')).toBe(false);
    expect(root.querySelector('.r2m-help__body p')).not.toBeNull();
  });

  it('covers every step with an entry that has a title and some body', () => {
    for (const [id, entry] of Object.entries(HELP)) {
      expect(entry.title.length, id).toBeGreaterThan(0);
      expect(entry.html, id).toContain('<h3>How</h3>');
    }
  });
});
