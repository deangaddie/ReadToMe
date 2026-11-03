import { describe, expect, it } from 'bun:test';
import {
  deleteProjectConfirm,
  deleteSeriesConfirm,
  leaveConfirm,
  moveConfirm,
  removePosterConfirm,
} from './permanent-changes';

describe('leaveConfirm()', () => {
  it('says no Character loses a link when none is linked', () => {
    expect(
      leaveConfirm({ title: 'A Study in Scarlet', seriesName: 'Sherlock Holmes', linked: [] }),
    ).toEqual({
      title: 'Remove A Study in Scarlet from Sherlock Holmes?',
      message:
        'No Characters in this book are linked to the series. ' +
        'The other books in Sherlock Holmes keep their numbers.',
      confirmLabel: 'Remove from series',
      destructive: false,
    });
  });

  it('names each linked Character that loses its link', () => {
    expect(
      leaveConfirm({
        title: 'A Study in Scarlet',
        seriesName: 'Sherlock Holmes',
        linked: ['Sherlock Holmes', 'John Watson', 'Inspector Lestrade'],
      }).message,
    ).toBe(
      'Sherlock Holmes, John Watson and Inspector Lestrade lose their series link. ' +
        'This book keeps its own copy of their Voices. ' +
        'The other books in Sherlock Holmes keep their numbers.',
    );
    expect(
      leaveConfirm({ title: 'X', seriesName: 'S', linked: ['John Watson'] }).message,
    ).toStartWith('John Watson loses its series link. This book keeps its own copy of its Voices.');
  });
});

describe('moveConfirm()', () => {
  it('is Leave’s confirm, titled and labelled as the move', () => {
    expect(
      moveConfirm({
        title: 'The Sign of the Four',
        seriesName: 'Sherlock Holmes',
        to: 'Holmes novels',
        linked: [],
      }),
    ).toEqual({
      title: 'Move The Sign of the Four to Holmes novels?',
      message:
        'It leaves Sherlock Holmes. No Characters in this book are linked to the series. ' +
        'The other books in Sherlock Holmes keep their numbers.',
      confirmLabel: 'Move to Holmes novels',
      destructive: false,
    });
  });
});

describe('deleteSeriesConfirm()', () => {
  it('names the member books and says each keeps its Voices', () => {
    expect(
      deleteSeriesConfirm({
        name: 'Sherlock Holmes',
        titles: ['A Study in Scarlet', 'The Sign of the Four'],
      }),
    ).toEqual({
      title: 'Delete series Sherlock Holmes?',
      message:
        'Every book is kept, with its Characters and Voices: A Study in Scarlet and ' +
        'The Sign of the Four. The series’ name, book numbers and links are removed, and there ' +
        'is no History to bring them back.',
      confirmLabel: 'Delete series',
      destructive: true,
    });
  });

  it('says so when the series has no books', () => {
    expect(deleteSeriesConfirm({ name: 'Austen', titles: [] }).message).toBe(
      'The series has no books. Its name is removed, and there is no History to bring it back.',
    );
  });

  it('names the poster among what goes when one was uploaded', () => {
    expect(
      deleteSeriesConfirm({
        name: 'Sherlock Holmes',
        titles: ['A Study in Scarlet'],
        hasPoster: true,
      }).message,
    ).toBe(
      'Every book is kept, with its Characters and Voices: A Study in Scarlet. ' +
        'The series’ name, poster, book numbers and links are removed, and there is no History ' +
        'to bring them back.',
    );
    expect(deleteSeriesConfirm({ name: 'Austen', titles: [], hasPoster: true }).message).toBe(
      'The series has no books. Its name and poster are removed, and there is no History to ' +
        'bring them back.',
    );
  });
});

describe('removePosterConfirm()', () => {
  it('is red, says there is no History, and names the cover that shows instead', () => {
    expect(removePosterConfirm({ name: 'Sherlock Holmes', fallbackPosition: 1 })).toEqual({
      title: 'Remove the poster of Sherlock Holmes?',
      message:
        'The uploaded image is deleted, and there is no History to bring it back. ' +
        'The series shows the cover of Book 1 instead.',
      confirmLabel: 'Remove poster',
      destructive: true,
    });
  });

  it('says the placeholder shows when the lowest book has no cover', () => {
    expect(removePosterConfirm({ name: 'Austen', fallbackPosition: null }).message).toBe(
      'The uploaded image is deleted, and there is no History to bring it back. ' +
        'The series shows a placeholder with its name instead.',
    );
  });
});

describe('deleteProjectConfirm()', () => {
  const base =
    'The project folder and everything in it are removed: the book file, characters, ' +
    'voices and generated audio. This cannot be undone.';

  it('is today’s confirm for a book in no series', () => {
    expect(deleteProjectConfirm('Emma', undefined)).toEqual({
      title: 'Delete Emma?',
      message: base,
      destructive: true,
      confirmLabel: 'Delete project',
    });
  });

  it('adds the series line for a member', () => {
    expect(
      deleteProjectConfirm('The Sign of the Four', {
        seriesId: 'h',
        name: 'Sherlock Holmes',
        position: 2,
      }).message,
    ).toBe(`${base} Part of Sherlock Holmes (Book 2); the other books keep their Voices.`);
  });
});
