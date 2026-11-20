import { describe, expect, it } from 'bun:test';
import {
  ACTION_DEEP_LINK_NAMES,
  type BookAddress,
  bookLink,
  bookQuery,
  parseBookAddress,
} from './book-address';
import { READER_MODES } from './reader-rows';

/** The query half of a Book link, parsed back as the Book page reads it. */
function parse(link: string): ReturnType<typeof parseBookAddress> {
  return parseBookAddress(new URL(link, 'http://localhost/app/').searchParams);
}

describe('bookLink', () => {
  it('writes the empty address as the bare Book path, with the folder encoded', () => {
    expect(bookLink('my book', {})).toBe('projects/my%20book/book');
  });

  it('writes the default Reader mode as no mode, so each address has exactly one link', () => {
    expect(bookLink('x', { mode: 'read' })).toBe('projects/x/book');
    expect(bookLink('x', { mode: 'speakers', chapter: 'c1' })).toBe(
      'projects/x/book?mode=speakers&chapter=c1',
    );
  });

  it.each<[string, BookAddress]>([
    ['the empty address', {}],
    ...READER_MODES.map((mode): [string, BookAddress] => [`mode ${mode}`, { mode }]),
    ['a chapter', { chapter: 'c 1&2' }],
    ...ACTION_DEEP_LINK_NAMES.map((action): [string, BookAddress] => [
      `action ${action}`,
      { action },
    ]),
    ['a mode, a chapter and an action', { mode: 'audio', chapter: 'c9', action: 'titles' }],
  ])('round-trips %s', (_, address) => {
    expect(parse(bookLink('x', address))).toEqual({
      mode: address.mode ?? 'read',
      chapter: address.chapter ?? null,
      action: address.action ?? null,
    });
  });
});

describe('parseBookAddress', () => {
  it('falls back to read for an unknown mode', () => {
    expect(parse('projects/x/book?mode=combined').mode).toBe('read');
  });

  it("reads an action name the page doesn't know as unknown, and no action as null", () => {
    expect(parse('projects/x/book?action=pauses').action).toBe('unknown');
    expect(parse('projects/x/book').action).toBeNull();
  });
});

describe('bookQuery', () => {
  it('patches one key, writing the default Reader mode as a deleted mode', () => {
    expect(bookQuery({ mode: 'read' })).toEqual({ mode: null });
    expect(bookQuery({ mode: 'audio' })).toEqual({ mode: 'audio' });
    expect(bookQuery({ chapter: 'c2' })).toEqual({ chapter: 'c2' });
    expect(bookQuery({ chapter: null })).toEqual({ chapter: null });
    expect(bookQuery({ action: null })).toEqual({ action: null });
  });
});
