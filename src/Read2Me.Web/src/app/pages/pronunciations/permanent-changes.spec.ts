import { describe, expect, it } from 'bun:test';
import { deleteConfirm } from './permanent-changes';

describe('deleteConfirm', () => {
  it('always asks, red, naming the word', () => {
    expect(deleteConfirm('Kulgan', 0)).toEqual({
      title: 'Delete “Kulgan”?',
      message: 'Kulgan goes back to how the provider reads it.',
      confirmLabel: 'Delete',
      destructive: true,
    });
  });

  it.each([
    [1, 'Kulgan goes back to how the provider reads it. 1 item loses its audio.'],
    [12, 'Kulgan goes back to how the provider reads it. 12 items lose their audio.'],
  ])('adds the audio lost when %p items have it', (count, message) => {
    expect(deleteConfirm('Kulgan', count).message).toBe(message);
  });
});
