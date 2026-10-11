import { describe, expect, it } from 'bun:test';
import { html, render } from 'lit-html';
import { type ConfigEditorFrameOptions, configEditorFrame } from './config-editor-frame';

function mount(overrides: Partial<ConfigEditorFrameOptions> = {}) {
  const host = document.createElement('div');
  const calls: string[] = [];
  const options: ConfigEditorFrameOptions = {
    title: 'Gemma 26B',
    subtitle: 'llama',
    dirty: false,
    saving: false,
    onSave: () => calls.push('save'),
    onCancel: () => calls.push('cancel'),
    ...overrides,
  };
  render(configEditorFrame(options, html`<input id="body" />`), host);
  const buttons = Array.from(host.querySelectorAll<HTMLButtonElement>('footer button'));
  return { host, buttons, calls };
}

describe('configEditorFrame', () => {
  it('projects the body and disables both actions while clean', () => {
    const { host, buttons } = mount();
    expect(host.querySelector('.r2m-config-editor-frame__body #body')).not.toBeNull();
    expect(host.querySelector('h2')?.textContent).toBe('Gemma 26B');
    expect(host.querySelector('.r2m-config-editor-frame__subtitle')?.textContent).toBe('llama');
    expect(buttons.map((b) => b.disabled)).toEqual([true, true]);
    expect(host.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
  });

  it('enables Save/Cancel when dirty and calls back', () => {
    const { host, buttons, calls } = mount({ dirty: true });
    expect(buttons.map((b) => b.disabled)).toEqual([false, false]);
    expect(host.querySelector('.r2m-config-editor-frame__dirty')).not.toBeNull();
    buttons[1]!.click();
    buttons[0]!.click();
    expect(calls).toEqual(['save', 'cancel']);
  });

  it('gates Save on canSave', () => {
    const { buttons } = mount({ dirty: true, canSave: false });
    expect(buttons.map((b) => b.disabled)).toEqual([false, true]);
  });

  it('shows a spinner and disables both while saving', () => {
    const { host, buttons } = mount({ dirty: true, saving: true });
    expect(buttons.map((b) => b.disabled)).toEqual([true, true]);
    expect(host.querySelector('footer .r2m-spinner')).not.toBeNull();
    expect(buttons[1]!.textContent).toContain('Saving…');
  });
});
