import { TestBed } from '@angular/core/testing';
import { PromptService } from './text-prompt-dialog';

describe('PromptService / r2m-text-prompt-dialog', () => {
  let service: PromptService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(PromptService);
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  /** CDK overlays detect Escape via the legacy keyCode. */
  function escape(): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true });
    Object.defineProperty(event, 'keyCode', { value: 27 });
    return event;
  }

  async function settle() {
    await new Promise((r) => setTimeout(r, 0));
  }

  function type(el: HTMLInputElement | HTMLTextAreaElement, value: string) {
    el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }

  it('shows the initial value and resolves the trimmed text on OK', async () => {
    const result = service.text({ title: 'Rename chapter', label: 'Title', initial: 'Old' });
    await settle();

    const input = document.querySelector<HTMLInputElement>('input.r2m-text-prompt-dialog__input')!;
    expect(document.querySelector('.r2m-text-prompt-dialog__title')?.textContent?.trim()).toBe(
      'Rename chapter',
    );
    expect(input.value).toBe('Old');
    type(input, '  New title  ');
    await settle();
    document.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__confirm')!.click();

    expect(await result).toBe('New title');
  });

  it('disables OK while required and blank, and Enter submits a single-line value', async () => {
    const result = service.text({ title: 'T', required: true });
    await settle();

    const ok = document.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__confirm')!;
    const input = document.querySelector<HTMLInputElement>('input.r2m-text-prompt-dialog__input')!;
    expect(ok.disabled).toBe(true);

    type(input, '   ');
    await settle();
    expect(ok.disabled).toBe(true);

    type(input, 'Hardin');
    await settle();
    expect(ok.disabled).toBe(false);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(await result).toBe('Hardin');
  });

  it('renders a textarea when multiline and resolves null on cancel', async () => {
    const result = service.text({ title: 'Edit text', multiline: true, initial: 'a\nb' });
    await settle();

    const area = document.querySelector<HTMLTextAreaElement>(
      'textarea.r2m-text-prompt-dialog__input',
    );
    expect(area).not.toBeNull();
    expect(document.querySelector('input.r2m-text-prompt-dialog__input')).toBeNull();
    document.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__cancel')!.click();

    expect(await result).toBeNull();
  });

  it('resolves null on Escape', async () => {
    const result = service.text({ title: 'T' });
    await settle();
    document.querySelector('r2m-text-prompt-dialog')!.dispatchEvent(escape());
    expect(await result).toBeNull();
  });
});
