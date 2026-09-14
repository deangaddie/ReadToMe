import { bulkConfirm, bulkDoneMessage } from './bulk-assign';

describe('bulkConfirm', () => {
  it('quotes the character and the preview figures, pluralised', () => {
    const options = bulkConfirm('Hardin', { paragraphsWithCharacterItems: 3, characterItems: 5 }, 3);
    expect(options).toEqual({
      title: 'Assign Hardin to selection',
      message:
        'Hardin becomes the speaker for 5 dialog lines in 3 paragraphs. Existing speakers are replaced.',
      confirmLabel: 'Assign',
      destructive: false,
    });
  });

  it('singular forms for one line in one paragraph', () => {
    const options = bulkConfirm('Hardin', { paragraphsWithCharacterItems: 1, characterItems: 1 }, 1);
    expect(options?.message).toBe(
      'Hardin becomes the speaker for 1 dialog line in 1 paragraph. Existing speakers are replaced.',
    );
  });

  it('names the selected paragraphs the write will skip', () => {
    const options = bulkConfirm('Hardin', { paragraphsWithCharacterItems: 2, characterItems: 2 }, 5);
    expect(options?.message).toContain('3 selected paragraphs have no dialog and stay unchanged.');
    const one = bulkConfirm('Hardin', { paragraphsWithCharacterItems: 2, characterItems: 2 }, 3);
    expect(one?.message).toContain('1 selected paragraph have no dialog');
  });

  it('a clear is worded as a loss and confirmed destructively', () => {
    const options = bulkConfirm(null, { paragraphsWithCharacterItems: 2, characterItems: 4 }, 2);
    expect(options).toEqual({
      title: 'Clear speakers in selection',
      message: '4 dialog lines in 2 paragraphs lose their speaker and need attributing again.',
      confirmLabel: 'Clear',
      destructive: true,
    });
  });

  it('is null when the selection holds no dialog', () => {
    expect(bulkConfirm('Hardin', { paragraphsWithCharacterItems: 0, characterItems: 0 }, 4)).toBeNull();
  });
});

describe('bulkDoneMessage', () => {
  it('reports what was written', () => {
    expect(bulkDoneMessage('Hardin', { paragraphsWithCharacterItems: 2, characterItems: 3 })).toBe(
      'Assigned Hardin to 3 lines in 2 paragraphs.',
    );
    expect(bulkDoneMessage(null, { paragraphsWithCharacterItems: 1, characterItems: 1 })).toBe(
      'Cleared speakers on 1 line in 1 paragraph.',
    );
  });
});
