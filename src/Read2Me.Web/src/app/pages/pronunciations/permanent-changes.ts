import type { ConfirmOptions } from '@app/ui/dialogs';

/**
 * The Pronunciations permanent-change confirm (conventions 12). Delete keeps its own title and body,
 * as Reread's does, since the spec words it: "Delete “Watson”?", the word going back to how the
 * provider reads it, then the audio it costs only when any item loses some. Always red: it removes
 * an entry the user made. Save asks nothing; the filled-in editor is its own confirm, its cost on
 * the advice line before the click.
 */
export function deleteConfirm(writtenForm: string, audioDiscardCount: number): ConfirmOptions {
  const cost =
    audioDiscardCount === 1
      ? ' 1 item loses its audio.'
      : audioDiscardCount > 1
        ? ` ${audioDiscardCount} items lose their audio.`
        : '';
  return {
    title: `Delete “${writtenForm}”?`,
    message: `${writtenForm} goes back to how the provider reads it.${cost}`,
    confirmLabel: 'Delete',
    destructive: true,
  };
}
