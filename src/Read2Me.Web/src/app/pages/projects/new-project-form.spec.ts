import { describe, expect, it } from 'bun:test';
import {
  EMPTY_DRAFT,
  describeRejection,
  isDraftValid,
  rejectFile,
  setAuthor,
  setBookTitle,
  setFile,
  setTitle,
  toCreateRequest,
  validateDraft,
} from './new-project-form';

const epub = new File(['x'], 'book.epub');

describe('new project form', () => {
  it('auto-fills the project title from the book title until the user edits it', () => {
    let d = setBookTitle(EMPTY_DRAFT, 'Pride and Prejudice');
    expect(d.title).toBe('Pride and Prejudice');

    d = setBookTitle(d, 'Sense and Sensibility');
    expect(d.title).toBe('Sense and Sensibility');

    d = setTitle(d, 'S&S audiobook');
    expect(d.titleEdited).toBe(true);
    d = setBookTitle(d, 'Emma');
    expect(d.title).toBe('S&S audiobook');
  });

  it('clearing an edited project title hands control back to the book title', () => {
    let d = setTitle(setBookTitle(EMPTY_DRAFT, 'Dracula'), 'Mine');
    d = setTitle(d, '  ');
    expect(d.titleEdited).toBe(false);
    expect(d.title).toBe('Dracula');
    d = setBookTitle(d, "Dracula's Guest");
    expect(d.title).toBe("Dracula's Guest");
  });

  it('requires book title, project title, author and a file', () => {
    expect(validateDraft(EMPTY_DRAFT)).toEqual({
      bookTitle: 'Book title is required',
      title: 'Project title is required',
      author: 'Author is required',
      file: 'Choose a book file (.epub,.txt)',
    });
    const full = setFile(setAuthor(setBookTitle(EMPTY_DRAFT, 'Dracula'), 'Stoker'), epub);
    expect(isDraftValid(full)).toBe(true);
    expect(isDraftValid({ ...full, author: '   ' })).toBe(false);
  });

  it('records and clears the file rejection message', () => {
    let d = rejectFile(EMPTY_DRAFT, { file: new File([''], 'x.pdf'), reason: 'type' });
    expect(d.fileError).toBe('x.pdf is not an .epub or .txt file.');
    expect(describeRejection({ file: new File([''], 'big.epub'), reason: 'size' })).toBe(
      'big.epub is larger than 100 MB.',
    );
    d = setFile(d, epub);
    expect(d.fileError).toBeNull();
    expect(d.file).toBe(epub);
  });

  it('trims the text fields into the create request', () => {
    const d = setFile(setAuthor(setBookTitle(EMPTY_DRAFT, ' Dracula '), ' Stoker '), epub);
    expect(toCreateRequest(d)).toEqual({
      title: 'Dracula',
      bookTitle: 'Dracula',
      author: 'Stoker',
      file: epub,
    });
  });
});
