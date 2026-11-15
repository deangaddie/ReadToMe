import { describe, expect, it } from 'bun:test';
import { decideActionDeepLink } from '../book/action-deep-link';
import { type BookAddress, bookLink, parseBookAddress } from '../book/book-address';
import { STEP_BOOK_ADDRESSES } from './step-runner';

describe('STEP_BOOK_ADDRESSES (the Book links StepRunner emits)', () => {
  it.each(Object.entries(STEP_BOOK_ADDRESSES))(
    '%s parses to what decideActionDeepLink accepts with a loaded book',
    (_, address: BookAddress) => {
      const { action } = parseBookAddress(
        new URL(bookLink('dracula', address), 'http://localhost/app/').searchParams,
      );
      const outcome = decideActionDeepLink({
        action,
        overview: { hasContent: true },
        pipelineLoaded: true,
        viewing: false,
        blocked: false,
      });
      expect(outcome).toEqual(address.action ? { open: address.action } : 'wait');
    },
  );
});
