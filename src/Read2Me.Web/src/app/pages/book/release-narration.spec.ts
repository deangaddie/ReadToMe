import { describe, expect, it } from 'bun:test';
import type { NarrationReleasePreviewDto } from '@app/api';
import {
  releaseConfirm,
  releaseDoneMessage,
  releaseNeedsConfirm,
  releaseNothingMessage,
} from './release-narration';

function preview(
  paragraphs: number,
  itemsWithAudio: number,
  speechMarkers: NarrationReleasePreviewDto['speechMarkers'] = [],
): NarrationReleasePreviewDto {
  return { paragraphs, itemsWithAudio, speechMarkers };
}

const QA = [
  { marker: 'Q.', paragraphs: 21 },
  { marker: 'A.', paragraphs: 20 },
];

describe('releaseConfirm', () => {
  it('names the count, the node, the speech markers and the audio it discards', () => {
    expect(releaseConfirm('Chapter', 'Chapter 6', preview(41, 12, QA))).toEqual({
      title: 'Release narration',
      message:
        'Release 41 narration paragraphs in Chapter 6? Speech markers: Q. ×21, A. ×20. ' +
        '12 have audio, which will be discarded.',
      confirmLabel: 'Release',
      destructive: true,
    });
  });

  it('says nothing about audio when none is discarded, and is not destructive', () => {
    expect(
      releaseConfirm(
        'Part',
        'Dr. Seward’s Diary',
        preview(26, 0, [{ marker: '—…—', paragraphs: 26 }]),
      ),
    ).toEqual({
      title: 'Release narration',
      message: 'Release 26 narration paragraphs in Dr. Seward’s Diary? Speech markers: —…— ×26.',
      confirmLabel: 'Release',
      destructive: false,
    });
  });

  it('singular forms for one paragraph and one item with audio', () => {
    expect(
      releaseConfirm('Chapter', 'Chapter 1', preview(1, 1, [{ marker: 'Darcy:', paragraphs: 1 }]))
        ?.message,
    ).toBe(
      'Release 1 narration paragraph in Chapter 1? Speech markers: Darcy: ×1. ' +
        '1 has audio, which will be discarded.',
    );
  });

  it('is null when the scope has nothing to release', () => {
    expect(releaseConfirm('Chapter', 'Chapter 6', preview(0, 0))).toBeNull();
  });
});

describe('releaseConfirm for one paragraph', () => {
  it('names the paragraph, not its text, and the items whose audio goes', () => {
    expect(releaseConfirm('Paragraph', 'Q: Your name?', preview(1, 2))).toEqual({
      title: 'Release narration',
      message: 'Release this narration paragraph? 2 items have audio, which will be discarded.',
      confirmLabel: 'Release',
      destructive: true,
    });
    expect(releaseConfirm('Paragraph', 'Q: Your name?', preview(1, 1))?.message).toBe(
      'Release this narration paragraph? 1 item has audio, which will be discarded.',
    );
  });
});

describe('releaseNothingMessage', () => {
  it('a chapter or part has nothing speech-like', () => {
    expect(releaseNothingMessage('Chapter')).toBe(
      'No speech-like paragraphs here — nothing to release.',
    );
    expect(releaseNothingMessage('Part')).toBe(
      'No speech-like paragraphs here — nothing to release.',
    );
  });

  it('a paragraph keeps the narration-only wording', () => {
    expect(releaseNothingMessage('Paragraph')).toBe(
      'No narration-only paragraphs here — nothing to release.',
    );
  });
});

describe('releaseNeedsConfirm', () => {
  it('only a single paragraph without audio is released straight away', () => {
    expect(releaseNeedsConfirm('Paragraph', preview(1, 0))).toBe(false);
    expect(releaseNeedsConfirm('Paragraph', preview(1, 1))).toBe(true);
    expect(releaseNeedsConfirm('Chapter', preview(1, 0))).toBe(true);
    expect(releaseNeedsConfirm('Part', preview(1, 0))).toBe(true);
  });
});

describe('releaseDoneMessage', () => {
  it('reports what was released and what comes next', () => {
    expect(releaseDoneMessage(preview(41, 12, QA))).toBe(
      'Released 41 paragraphs. Assign their speakers or attribute them.',
    );
    expect(releaseDoneMessage(preview(1, 0))).toBe(
      'Released 1 paragraph. Assign its speaker or attribute it.',
    );
  });
});
