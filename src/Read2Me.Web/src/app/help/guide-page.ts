import type { PipelineStepId } from '../api/dtos';

/**
 * A Pipeline step's guide page in docs/guide: its id in lower case (`Structure` → `structure.md`).
 * `bun run help` renders these files and the Overview's help links to them, so both name them here.
 */
export const guidePage = (id: PipelineStepId): string => `${id.toLowerCase()}.md`;
