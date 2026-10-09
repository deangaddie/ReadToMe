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
export * from './audio-processing-api';
export * from './voices-api';
export * from './characters-api';
export * from './ai-services-api';
export * from './preflight-api';
export * from './book-api';
export * from './book-edits-api';
export * from './discovery-api';
export * from './settings-api';
export * from './voice-editor-api';
export * from './workspace-url';
