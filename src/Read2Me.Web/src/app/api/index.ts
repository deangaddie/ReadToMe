/**
 * The typed HTTP layer (ticket 05). Feature code imports from `@app/api`: one `ApiClient`, the
 * per-area services, the wire shapes in `dtos.ts`, the `BookCommand` union and `workspaceUrl`.
 * Nothing outside this folder imports `@angular/common/http` (ESLint rule).
 */
export * from './api-client';
export * from './api-error';
export * from './api-error-handler';
export * from './dtos';
export * from './book-commands';
export * from './workspace-url';
export * from './projects-api';
export * from './book-api';
export * from './attribution-api';
export * from './audio-api';
export * from './discovery-api';
export * from './voices-api';
export * from './assembly-api';
export * from './settings-api';
export * from './prompts-api';
export * from './audio-processing-api';
export * from './ai-services-api';
export * from './themes-api';
