/**
 * The typed HTTP layer. Feature code imports from `@app/api`: one `ApiClient`, the wire shapes and
 * the `BookCommand` union. Nothing outside this folder calls `fetch` (lint rule).
 *
 * The wire shapes are the generated ones in `schema.d.ts`, each under its C# name (ADR 0017), plus
 * the hand-written rest in `dtos.ts`. A hand-written export of a generated name is ambiguous under
 * the two star re-exports and fails typecheck (TS2308), so nobody aliases one by hand.
 */
export type * from './schema';
export * from './dtos';
// Refinements of a generated shape (a field the host types loosely, narrowed): named, so these win
// over the star re-export of `schema.d.ts`.
export type { PromptCatalogEntry, TryPronunciationRequest } from './dtos';
export * from './api-client';
export * from './api-error';
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
export * from './history-api';
export * from './pronunciations-api';
export * from './settings-api';
export * from './series-api';
export * from './setup-check-api';
export * from './prompts-api';
export * from './voice-editor-api';
export * from './workspace-url';
export * from './step-view';
