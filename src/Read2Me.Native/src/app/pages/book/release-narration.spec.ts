import { describe, expect, it } from 'bun:test';
import { releaseConfirm, releaseDoneMessage, releaseNeedsConfirm } from './release-narration';

describe('releaseConfirm', () => {
  it('names the count, the node and the audio it discards', () => {
    expect(releaseConfirm('Chapter', 'Chapter 6', { paragraphs: 41, itemsWithAudio: 12 })).toEqual({
      title: 'Release narration',
      message:
        'Release 41 narration paragraphs in Chapter 6? 12 have audio, which will be discarded.',
      confirmLabel: 'Release',
      destructive: true,
    });
  });

  it('says nothing about audio when none is discarded, and is not destructive', () => {
    expect(
      releaseConfirm('Part', 'The Psychohistorians', { paragraphs: 3, itemsWithAudio: 0 }),
    ).toEqual({
      title: 'Release narration',
      message: 'Release 3 narration paragraphs in The Psychohistorians?',
      confirmLabel: 'Release',
      destructive: false,
    });
  });

  it('singular forms for one paragraph and one item with audio', () => {
    expect(
      releaseConfirm('Chapter', 'Chapter 1', { paragraphs: 1, itemsWithAudio: 1 })?.message,
    ).toBe('Release 1 narration paragraph in Chapter 1? 1 has audio, which will be discarded.');
  });

  it('is null when the scope has nothing to release', () => {
    expect(releaseConfirm('Chapter', 'Chapter 6', { paragraphs: 0, itemsWithAudio: 0 })).toBeNull();
  });
});

describe('releaseConfirm for one paragraph', () => {
  it('names the paragraph, not its text, and the items whose audio goes', () => {
    expect(
      releaseConfirm('Paragraph', 'Q: Your name?', { paragraphs: 1, itemsWithAudio: 2 }),
    ).toEqual({
      title: 'Release narration',
      message: 'Release this narration paragraph? 2 items have audio, which will be discarded.',
      confirmLabel: 'Release',
      destructive: true,
    });
    expect(
      releaseConfirm('Paragraph', 'Q: Your name?', { paragraphs: 1, itemsWithAudio: 1 })?.message,
    ).toBe('Release this narration paragraph? 1 item has audio, which will be discarded.');
  });
});

describe('releaseNeedsConfirm', () => {
  it('only a single paragraph without audio is released straight away', () => {
    expect(releaseNeedsConfirm('Paragraph', { paragraphs: 1, itemsWithAudio: 0 })).toBe(false);
    expect(releaseNeedsConfirm('Paragraph', { paragraphs: 1, itemsWithAudio: 1 })).toBe(true);
    expect(releaseNeedsConfirm('Chapter', { paragraphs: 1, itemsWithAudio: 0 })).toBe(true);
    expect(releaseNeedsConfirm('Part', { paragraphs: 1, itemsWithAudio: 0 })).toBe(true);
  });
});

describe('releaseDoneMessage', () => {
  it('reports what was released and what comes next', () => {
    expect(releaseDoneMessage({ paragraphs: 41, itemsWithAudio: 12 })).toBe(
      'Released 41 paragraphs. Assign their speakers or attribute them.',
    );
    expect(releaseDoneMessage({ paragraphs: 1, itemsWithAudio: 0 })).toBe(
      'Released 1 paragraph. Assign its speaker or attribute it.',
    );
  });
});
