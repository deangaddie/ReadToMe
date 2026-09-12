import { TestBed } from '@angular/core/testing';
import { ConfirmService } from './confirm-dialog';

describe('ConfirmService / r2m-confirm-dialog', () => {
  let service: ConfirmService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ConfirmService);
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

  it('renders title and message and resolves true on confirm', async () => {
    const result = service.confirm({ title: 'Reread book?', message: 'This replaces the text.' });
    await settle();

    expect(document.querySelector('.r2m-confirm-dialog__title')?.textContent?.trim()).toBe(
      'Reread book?',
    );
    expect(document.querySelector('.r2m-confirm-dialog__message')?.textContent?.trim()).toBe(
      'This replaces the text.',
    );
    const confirm = document.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!;
    expect(confirm.textContent?.trim()).toBe('OK');
    confirm.click();

    expect(await result).toBe(true);
  });

  it('resolves false on cancel', async () => {
    const result = service.confirm({ title: 'T', message: 'M', cancelLabel: 'Keep' });
    await settle();
    const cancel = document.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__cancel')!;
    expect(cancel.textContent?.trim()).toBe('Keep');
    cancel.click();
    expect(await result).toBe(false);
  });

  it('styles destructive confirms with a delete icon and label', async () => {
    const result = service.confirm({
      title: 'Delete project?',
      message: 'Gone.',
      destructive: true,
    });
    await settle();
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    expect(dialog.classList.contains('r2m-confirm-dialog--destructive')).toBe(true);
    const confirm = dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!;
    expect(confirm.querySelector('mat-icon')?.textContent).toBe('delete_forever');
    expect(confirm.textContent).toContain('Delete');
    confirm.click();
    expect(await result).toBe(true);
  });

  it('resolves false on Escape', async () => {
    const result = service.confirm({ title: 'T', message: 'M' });
    await settle();
    document.querySelector('r2m-confirm-dialog')!.dispatchEvent(escape());
    expect(await result).toBe(false);
  });
});
