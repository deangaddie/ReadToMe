import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { LlmStreamFeed } from '@app/activity/stream-feed';
import { streamDisclosure } from './stream-disclosure';

const feed = { events: () => [], maxUnits: 50 } as unknown as LlmStreamFeed;

function mount(open: boolean) {
  const host = document.createElement('div');
  document.body.append(host);
  render(streamDisclosure({ id: 'test-activity', feed, open, onToggle: () => {} }), host);
  return {
    toggle: host.querySelector('[data-action="toggle-stream"]')!,
    body: host.querySelector('.r2m-stream-disclosure__body')!,
  };
}

afterEach(() => document.body.replaceChildren());

describe('streamDisclosure', () => {
  it.each([false, true])('ties the toggle to the body it controls (open: %p)', (open) => {
    const { toggle, body } = mount(open);
    expect(body.id).toBe('test-activity');
    expect(toggle.getAttribute('aria-controls')).toBe('test-activity');
    expect(toggle.getAttribute('aria-expanded')).toBe(String(open));
    expect(body.querySelector('r2m-stream-llm')!.shown).toBe(open);
  });
});
