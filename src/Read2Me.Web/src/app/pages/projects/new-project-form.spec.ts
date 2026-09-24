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
    let d = setBookTitle(EMPTY_DRAFT, 'Foundation');
    expect(d.title).toBe('Foundation');

    d = setBookTitle(d, 'Foundation and Empire');
    expect(d.title).toBe('Foundation and Empire');

    d = setTitle(d, 'F&E audiobook');
    expect(d.titleEdited).toBe(true);
    d = setBookTitle(d, 'Second Foundation');
    expect(d.title).toBe('F&E audiobook');
  });

  it('clearing an edited project title hands control back to the book title', () => {
    let d = setTitle(setBookTitle(EMPTY_DRAFT, 'Dune'), 'Mine');
    d = setTitle(d, '  ');
    expect(d.titleEdited).toBe(false);
    expect(d.title).toBe('Dune');
    d = setBookTitle(d, 'Dune Messiah');
    expect(d.title).toBe('Dune Messiah');
  });

  it('requires book title, project title, author and a file', () => {
    expect(validateDraft(EMPTY_DRAFT)).toEqual({
      bookTitle: 'Book title is required',
      title: 'Project title is required',
      author: 'Author is required',
      file: 'Choose a book file (.epub,.txt)',
    });
    const full = setFile(setAuthor(setBookTitle(EMPTY_DRAFT, 'Dune'), 'Herbert'), epub);
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
    const d = setFile(setAuthor(setBookTitle(EMPTY_DRAFT, ' Dune '), ' Herbert '), epub);
    expect(toCreateRequest(d)).toEqual({
      title: 'Dune',
      bookTitle: 'Dune',
      author: 'Herbert',
      file: epub,
    });
  });
});
