import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { AiServiceStatus } from '@app/api';
import { dockerControls } from './docker-controls';

/** Ported from the Angular r2m-docker-controls TestBed spec, case for case. */
function mount(status: AiServiceStatus, busy = false, statusOnly = false) {
  const host = document.createElement('div');
  document.body.append(host);
  const log: string[] = [];
  const draw = (s: AiServiceStatus, b = busy, only = statusOnly) =>
    render(
      dockerControls({
        serviceName: 'llama',
        status: s,
        busy: b,
        statusOnly: only,
        onStart: () => log.push('start'),
        onRestart: () => log.push('restart'),
        onShutdown: () => log.push('shutdown'),
        onRefresh: () => log.push('refresh'),
      }),
      host,
    );
  draw(status);
  const buttons = () => Array.from(host.querySelectorAll<HTMLButtonElement>('button'));
  const chip = () => host.querySelector('.r2m-status-chip')!;
  return { host, log, draw, buttons, chip };
}

afterEach(() => document.body.replaceChildren());

describe('dockerControls()', () => {
  it('enables Start only when stopped and emits it', () => {
    const { log, buttons, chip } = mount('Stopped');
    expect(chip().textContent).toContain('Stopped');
    expect(buttons().map((b) => b.disabled)).toEqual([false, true, true, false]);
    buttons()[0]!.click();
    buttons()[3]!.click();
    expect(log).toEqual(['start', 'refresh']);
  });

  it('enables Restart/Shutdown when ready or starting', () => {
    const { draw, buttons, chip } = mount('Ready');
    expect(buttons().map((b) => b.disabled)).toEqual([true, false, false, false]);
    expect(chip().classList).toContain('r2m-status-chip--ok');

    draw('Starting');
    expect(buttons().map((b) => b.disabled)).toEqual([true, false, false, false]);
    expect(chip().classList).toContain('r2m-status-chip--busy');
  });

  it('shows the recovering and down statuses the host reports', () => {
    const { draw, chip } = mount('Recovering');
    expect(chip().textContent).toContain('Recovering');
    expect(chip().classList).toContain('r2m-status-chip--busy');

    draw('Down');
    expect(chip().textContent).toContain('Down');
    expect(chip().classList).toContain('r2m-status-chip--error');
  });

  it('status-only keeps the chip and Refresh and drops the container buttons', () => {
    const { log, buttons } = mount('Ready', false, true);
    expect(buttons().map((b) => b.getAttribute('aria-label'))).toEqual(['Refresh status']);
    buttons()[0]!.click();
    expect(log).toEqual(['refresh']);
  });

  it('disables everything while busy and flags NotFound as a warning', () => {
    const { buttons, chip } = mount('NotFound', true);
    expect(buttons().every((b) => b.disabled)).toBe(true);
    expect(chip().classList).toContain('r2m-status-chip--warn');
  });
});
