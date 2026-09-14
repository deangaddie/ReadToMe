import {
  DEFAULT_MANUAL_REREAD_FORM,
  ManualRereadForm,
  activeLevels,
  toManualImportRequest,
  validateManualReread,
} from './manual-reread-form';

function form(overrides: Partial<ManualRereadForm> = {}): ManualRereadForm {
  return { ...DEFAULT_MANUAL_REREAD_FORM, ...overrides };
}

describe('manual reread form', () => {
  it('starts as chapters-only by text prefix, which needs a prefix before it can be sent', () => {
    expect(activeLevels(DEFAULT_MANUAL_REREAD_FORM)).toEqual(['chapter']);
    expect(validateManualReread(DEFAULT_MANUAL_REREAD_FORM)).toBe('Chapter prefix cannot be empty.');
    expect(validateManualReread(form({ chapter: { mode: 'Prefix', prefix: '  ' } }))).toBe(
      'Chapter prefix cannot be empty.',
    );
    expect(validateManualReread(form({ chapter: { mode: 'Prefix', prefix: 'Chapter' } }))).toBeNull();
  });

  it('number and roman modes need no prefix', () => {
    expect(validateManualReread(form({ chapter: { mode: 'Arabic', prefix: '' } }))).toBeNull();
    expect(validateManualReread(form({ chapter: { mode: 'Roman', prefix: '' } }))).toBeNull();
  });

  it('switched-on levels are validated in book order; switched-off ones are ignored', () => {
    const chapter = { mode: 'Roman', prefix: '' } as const;
    expect(
      validateManualReread(form({ hasVolumes: true, hasParts: true, chapter })),
    ).toBe('Volume prefix cannot be empty.');
    expect(
      validateManualReread(
        form({ hasVolumes: true, hasParts: true, volume: { mode: 'Prefix', prefix: 'Book' }, chapter }),
      ),
    ).toBe('Part prefix cannot be empty.');
    expect(validateManualReread(form({ hasParts: false, chapter }))).toBeNull();
    expect(activeLevels(form({ hasVolumes: true, hasParts: true }))).toEqual([
      'volume',
      'part',
      'chapter',
    ]);
  });

  it('builds the request with trimmed prefixes and null rules for switched-off levels', () => {
    expect(
      toManualImportRequest(
        form({
          hasVolumes: true,
          volume: { mode: 'Prefix', prefix: ' Book ' },
          part: { mode: 'Prefix', prefix: 'Part' },
          chapter: { mode: 'Roman', prefix: 'stale' },
        }),
      ),
    ).toEqual({
      hasMultipleVolumes: true,
      hasMultipleParts: false,
      volume: { mode: 'Prefix', prefix: 'Book' },
      part: null,
      chapter: { mode: 'Roman', prefix: null },
    });
  });
});
