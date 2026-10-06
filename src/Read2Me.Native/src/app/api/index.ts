/**
 * The typed HTTP layer. Feature code imports from `@app/api`: one `ApiClient`, the wire shapes in
 * `dtos.ts` and the `BookCommand` union. Nothing outside this folder calls `fetch` (lint rule).
 */
export * from './api-client';
export * from './api-error';
export * from './dtos';
export * from './book-commands';
