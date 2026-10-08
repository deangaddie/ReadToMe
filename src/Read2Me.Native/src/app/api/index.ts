/**
 * The typed HTTP layer. Feature code imports from `@app/api`: one `ApiClient`, the wire shapes in
 * `dtos.ts` and the `BookCommand` union. Nothing outside this folder calls `fetch` (lint rule).
 */
export * from './api-client';
export * from './api-error';
export * from './dtos';
export * from './book-commands';
export * from './themes-api';
export * from './projects-api';
export * from './attribution-api';
export * from './audio-api';
export * from './assembly-api';
export * from './voices-api';
export * from './ai-services-api';
export * from './preflight-api';
export * from './book-api';
export * from './workspace-url';
