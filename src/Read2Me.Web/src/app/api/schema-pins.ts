/**
 * Compile-time pins between the hand-written shapes in `dtos.ts` and the generated OpenAPI schema.
 * Each entry asserts the hand-written type is assignable to the generated one, so a field the host
 * renames or drops fails `bun run typecheck` once `pwsh scripts/api-types.ps1` regenerates the
 * contract (the PR gate's API contract step always does).
 *
 * What is pinned is a hand-written shape whose endpoint has not converted yet (its slice of #171
 * hasn't landed); a converted endpoint's shapes are `Schema` aliases in `dtos.ts`, with nothing to pin.
 */
import type {
  AudioPostProcessStepConfig,
  AudioProcessingUpdateRequest,
  FfmpegTestRequest,
  ItemsEnqueueRequest,
  ParagraphsEnqueueRequest,
  PauseDurations,
  PreflightRunRequest,
  Schema,
  SetupCheckReport,
  StepPreviewRequest,
} from './dtos';

type Pin<THand extends TGenerated, TGenerated> = THand;

export type SchemaPins = [
  Pin<PreflightRunRequest, Schema['PreflightRunRequest']>,
  Pin<AudioProcessingUpdateRequest, Schema['AudioProcessingUpdateRequest']>,
  Pin<AudioPostProcessStepConfig, Schema['AudioPostProcessStepConfig']>,
  Pin<PauseDurations, Schema['PauseDurationsDto']>,
  Pin<FfmpegTestRequest, Schema['FfmpegTestRequest']>,
  Pin<StepPreviewRequest, Schema['StepPreviewRequest']>,
  Pin<ParagraphsEnqueueRequest, Schema['ParagraphsEnqueueRequest']>,
  Pin<ItemsEnqueueRequest, Schema['ItemsEnqueueRequest']>,
  Pin<SetupCheckReport, Schema['SetupCheckReport']>,
];
