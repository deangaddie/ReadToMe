import { BOOK_ACCEPT, BOOK_MAX_BYTES, CreateProjectRequest } from '@app/api';
import { RejectedFile } from '@app/ui/file-drop/file-drop';

/**
 * The New project dialog's state as plain data, so the auto-fill rule and validation are unit
 * tested without rendering: the project title follows the book title until the user types their
 * own, and clearing it hands control back to the book title.
 */
export interface NewProjectDraft {
  bookTitle: string;
  title: string;
  /** True once the user typed a non-empty project title of their own. */
  titleEdited: boolean;
  author: string;
  file: File | null;
  /** Inline message for the last rejected drop/pick; cleared by the next accepted file. */
  fileError: string | null;
}

export const EMPTY_DRAFT: NewProjectDraft = {
  bookTitle: '',
  title: '',
  titleEdited: false,
  author: '',
  file: null,
  fileError: null,
};

export function setBookTitle(draft: NewProjectDraft, bookTitle: string): NewProjectDraft {
  return { ...draft, bookTitle, title: draft.titleEdited ? draft.title : bookTitle };
}

export function setTitle(draft: NewProjectDraft, title: string): NewProjectDraft {
  const titleEdited = title.trim().length > 0;
  return { ...draft, titleEdited, title: titleEdited ? title : draft.bookTitle };
}

export function setAuthor(draft: NewProjectDraft, author: string): NewProjectDraft {
  return { ...draft, author };
}

export function setFile(draft: NewProjectDraft, file: File): NewProjectDraft {
  return { ...draft, file, fileError: null };
}

export function rejectFile(draft: NewProjectDraft, rejected: RejectedFile): NewProjectDraft {
  return { ...draft, fileError: describeRejection(rejected) };
}

export function describeRejection({ file, reason }: RejectedFile): string {
  return reason === 'type'
    ? `${file.name} is not an .epub or .txt file.`
    : `${file.name} is larger than ${Math.round(BOOK_MAX_BYTES / (1024 * 1024))} MB.`;
}

export type NewProjectErrors = Partial<Record<'bookTitle' | 'title' | 'author' | 'file', string>>;

export function validateDraft(draft: NewProjectDraft): NewProjectErrors {
  const errors: NewProjectErrors = {};
  if (!draft.bookTitle.trim()) errors.bookTitle = 'Book title is required';
  if (!draft.title.trim()) errors.title = 'Project title is required';
  if (!draft.author.trim()) errors.author = 'Author is required';
  if (!draft.file) errors.file = `Choose a book file (${BOOK_ACCEPT})`;
  return errors;
}

export function isDraftValid(draft: NewProjectDraft): boolean {
  return Object.keys(validateDraft(draft)).length === 0;
}

/** Draft → multipart request; only call once {@link isDraftValid}. */
export function toCreateRequest(draft: NewProjectDraft): CreateProjectRequest {
  return {
    title: draft.title.trim(),
    bookTitle: draft.bookTitle.trim(),
    author: draft.author.trim(),
    file: draft.file!,
  };
}
