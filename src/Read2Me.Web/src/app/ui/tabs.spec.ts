import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { tabPanel, tabs } from './tabs';

type Tab = 'jobs' | 'llm' | 'audio';
const TABS: { id: Tab; label: string }[] = [
  { id: 'jobs', label: 'Jobs' },
  { id: 'llm', label: 'LLM' },
  { id: 'audio', label: 'Audio' },
];

function mount(selected: Tab = 'jobs') {
  const host = document.createElement('div');
  document.body.append(host);
  const picked: Tab[] = [];
  const draw = (current: Tab): void => {
    render(
      [
        tabs({
          id: 'act',
          tabs: TABS,
          selected: current,
          label: 'Sections',
          // The app re-renders on selection; the spec does the same so `selected` follows.
          onSelect: (t) => {
            picked.push(t);
            draw(t);
          },
        }),
        tabPanel('act', current, 'body'),
      ],
      host,
    );
  };
  draw(selected);
  const tabEls = () => Array.from(host.querySelectorAll<HTMLElement>('[role="tab"]'));
  return { host, picked, draw, tabEls };
}

afterEach(() => document.body.replaceChildren());

describe('tabs()', () => {
  it('renders a tablist whose selected tab is the one tab stop and names its panel', () => {
    const { host, tabEls } = mount('llm');
    const list = host.querySelector('[role="tablist"]');
    expect(list?.getAttribute('aria-label')).toBe('Sections');
    expect(tabEls().map((t) => t.textContent?.trim())).toEqual(['Jobs', 'LLM', 'Audio']);
    expect(tabEls().map((t) => t.getAttribute('aria-selected'))).toEqual([
      'false',
      'true',
      'false',
    ]);
    expect(tabEls().map((t) => t.tabIndex)).toEqual([-1, 0, -1]);
    expect(tabEls().map((t) => t.dataset['tab'])).toEqual(['jobs', 'llm', 'audio']);
    const panel = host.querySelector('[role="tabpanel"]');
    expect(panel?.id).toBe('act-panel-llm');
    expect(panel?.getAttribute('aria-labelledby')).toBe('act-tab-llm');
    expect(tabEls()[1]?.getAttribute('aria-controls')).toBe('act-panel-llm');
  });

  it('selects on click', () => {
    const { picked, tabEls } = mount();
    tabEls()[2]!.click();
    expect(picked).toEqual(['audio']);
  });

  it('moves and selects with the arrow keys, wrapping', () => {
    const { picked, tabEls } = mount();
    tabEls()[0]!.focus();
    tabEls()[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    expect(document.activeElement).toBe(tabEls()[1]!);
    expect(picked).toEqual(['llm']);
    tabEls()[1]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    tabEls()[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    expect(picked).toEqual(['llm', 'jobs', 'audio']);
    expect(document.activeElement).toBe(tabEls()[2]!);
  });
});
