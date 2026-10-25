import { describe, expect, it } from 'bun:test';
import { deleteConfirm } from './permanent-changes';

describe('deleteConfirm', () => {
  it('always asks, red, naming the word', () => {
    expect(deleteConfirm('Watson', 0)).toEqual({
      title: 'Delete “Watson”?',
      message: 'Watson goes back to how the provider reads it.',
      confirmLabel: 'Delete',
      destructive: true,
    });
  });

  it.each([
    [1, 'Watson goes back to how the provider reads it. 1 item loses its audio.'],
    [12, 'Watson goes back to how the provider reads it. 12 items lose their audio.'],
  ])('adds the audio lost when %p items have it', (count, message) => {
    expect(deleteConfirm('Watson', count).message).toBe(message);
  });
});
