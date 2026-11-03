import { describe, expect, it } from 'bun:test';
import {
  deleteProjectConfirm,
  deleteSeriesConfirm,
  leaveConfirm,
  linkConfirm,
  moveConfirm,
  pushSeriesVoicesConfirm,
  removePosterConfirm,
  shareConfirm,
  takeSeriesVoicesConfirm,
  unlinkConfirm,
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

describe('deleteSeriesConfirm() with Series characters', () => {
  it('names each Series character whose links go', () => {
    expect(
      deleteSeriesConfirm({
        name: 'Sherlock Holmes',
        titles: ['A Study in Scarlet', 'The Sign of the Four'],
        linked: ['Sherlock Holmes', 'Dr. Watson'],
      }).message,
    ).toBe(
      'Every book is kept, with its Characters and Voices: A Study in Scarlet and ' +
        'The Sign of the Four. The series’ name, book numbers and the links of Sherlock Holmes ' +
        'and Dr. Watson are removed, and there is no History to bring them back.',
    );
  });
});

describe('linkConfirm()', () => {
  it('names who it shares with and what each side gains, and is neutral', () => {
    expect(
      linkConfirm({
        name: 'John Watson',
        title: 'The Sign of the Four',
        label: 'Dr. Watson',
        others: [{ name: 'Dr. Watson', title: 'A Study in Scarlet' }],
        bookGains: ['Army doctor'],
        seriesGains: ['Biographer', 'Older'],
      }),
    ).toEqual({
      title: 'Link John Watson to Dr. Watson?',
      message:
        'John Watson will share Voices with Dr. Watson in A Study in Scarlet. ' +
        'The Sign of the Four gains “Army doctor”; the other book gains “Biographer” and “Older”. ' +
        'Each is a History step in its book. Names, aliases, delivery cues and Voice Rules stay ' +
        'each book’s own.',
      confirmLabel: 'Link',
      destructive: false,
    });
  });

  it('says when a side gains nothing, and counts the other books', () => {
    expect(
      linkConfirm({
        name: 'Narrator',
        title: 'The Adventures of Sherlock Holmes',
        label: 'Narrator',
        others: [
          { name: 'Narrator', title: 'A Study in Scarlet' },
          { name: 'Narrator', title: 'The Sign of the Four' },
        ],
        bookGains: [],
        seriesGains: ['Narration'],
      }).message,
    ).toBe(
      'Narrator will share Voices with Narrator in A Study in Scarlet and Narrator in ' +
        'The Sign of the Four. The Adventures of Sherlock Holmes gains no Voices; the other ' +
        'books gain “Narration”. Each is a History step in its book. Names, aliases, delivery ' +
        'cues and Voice Rules stay each book’s own.',
    );
  });
});

describe('unlinkConfirm()', () => {
  it('says the book keeps its copy and edits stop crossing books', () => {
    expect(
      unlinkConfirm({
        name: 'John Watson',
        title: 'The Sign of the Four',
        label: 'Dr. Watson',
        otherTitles: ['A Study in Scarlet'],
      }),
    ).toEqual({
      title: 'Unlink John Watson from Dr. Watson?',
      message:
        'The Sign of the Four keeps its own copy of John Watson’s Voices, so nothing in it ' +
        'breaks. From now on, Voice edits there no longer reach A Study in Scarlet, and theirs ' +
        'no longer reach it.',
      confirmLabel: 'Unlink',
      destructive: false,
    });
  });

  it('says the Series character goes with its last link', () => {
    expect(
      unlinkConfirm({
        name: 'Dr. Watson',
        title: 'A Study in Scarlet',
        label: 'Dr. Watson',
        otherTitles: [],
      }).message,
    ).toBe(
      'A Study in Scarlet keeps its own copy of Dr. Watson’s Voices, so nothing in it breaks. ' +
        'No other book is linked, so the series character Dr. Watson is removed.',
    );
  });
});

describe('shareConfirm()', () => {
  it('says a Series character is made from this Character’s Voices', () => {
    expect(
      shareConfirm({
        name: 'Dr. Watson',
        title: 'A Study in Scarlet',
        seriesName: 'Sherlock Holmes',
      }),
    ).toEqual({
      title: 'Share Dr. Watson across Sherlock Holmes?',
      message: 'A series character is made from the Voices of Dr. Watson in A Study in Scarlet.',
      confirmLabel: 'Share as new',
      destructive: false,
    });
  });
});

describe('leaveConfirm() without the cast read', () => {
  it('names no Characters when the links couldn’t be read', () => {
    expect(leaveConfirm({ title: 'X', seriesName: 'S', linked: null }).message).toBe(
      'Any of its Characters linked to the series lose their link. This book keeps its own ' +
        'copy of their Voices. The other books in S keep their numbers.',
    );
  });
});

describe('pushSeriesVoicesConfirm()', () => {
  it('names what is replaced in which books and where the History steps go', () => {
    expect(
      pushSeriesVoicesConfirm({
        label: 'Dr. Watson',
        title: 'A Study in Scarlet',
        otherTitles: ['The Sign of the Four', 'The Adventures of Sherlock Holmes'],
      }),
    ).toEqual({
      title: 'Use A Study in Scarlet’s copy for all?',
      message:
        'Dr. Watson’s Voices in The Sign of the Four and The Adventures of Sherlock Holmes are ' +
        'replaced by the copy in A Study in Scarlet. Each of those books records a “Push series ' +
        'voices” History step, which can bring its own copy back. Voice Rules stay each book’s own.',
      confirmLabel: 'Use this copy for all',
      destructive: false,
    });
  });
});

describe('takeSeriesVoicesConfirm()', () => {
  it('names what is replaced, from where, and the History step in this book', () => {
    expect(
      takeSeriesVoicesConfirm({
        label: 'Dr. Watson',
        title: 'A Study in Scarlet',
        copyTitle: 'The Sign of the Four',
      }),
    ).toEqual({
      title: 'Use the series copy in A Study in Scarlet?',
      message:
        'Dr. Watson’s Voices in A Study in Scarlet are replaced by the series copy, from The Sign ' +
        'of the Four. A Study in Scarlet records a “Take series voices” History step, which can ' +
        'bring its own copy back. Voice Rules stay each book’s own.',
      confirmLabel: 'Use the series copy',
      destructive: false,
    });
  });
});

describe('push and take confirms with losses', () => {
  it('name each book that loses Voices and the positional rules that go with them', () => {
    const push = pushSeriesVoicesConfirm({
      label: 'Dr. Watson',
      title: 'A Study in Scarlet',
      otherTitles: ['The Sign of the Four', 'The Adventures of Sherlock Holmes'],
      losses: [
        { folder: 'four', title: 'The Sign of the Four', voices: ['Pipe', 'Violin'], ruleCount: 2 },
        {
          folder: 'adv',
          title: 'The Adventures of Sherlock Holmes',
          voices: ['Retired'],
          ruleCount: 0,
        },
      ],
    });
    expect(push.message).toEndWith(
      'The Sign of the Four loses “Pipe” and “Violin”, and the 2 Voice Rules on them. ' +
        'The Adventures of Sherlock Holmes loses “Retired”. Other Voice Rules stay each book’s own.',
    );

    const take = takeSeriesVoicesConfirm({
      label: 'Dr. Watson',
      title: 'The Sign of the Four',
      copyTitle: 'A Study in Scarlet',
      losses: [{ folder: 'four', title: 'The Sign of the Four', voices: ['Pipe'], ruleCount: 1 }],
    });
    expect(take.message).toEndWith(
      'The Sign of the Four loses “Pipe”, and the Voice Rule on it. Other Voice Rules stay each book’s own.',
    );
  });
});
