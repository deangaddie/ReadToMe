/**
 * Compile-time pins between the hand-written shapes in `dtos.ts` and the generated OpenAPI schema.
 * Each entry asserts the hand-written type is assignable to the generated one, so a field the host
 * renames or drops fails `bun run typecheck` once `pwsh scripts/api-types.ps1` regenerates the
 * contract (the PR gate's API contract step always does).
 *
 * Only shapes the host's document actually declares can be pinned: request bodies, the settings
 * entities and the book overview. Other response DTOs have no OpenAPI counterpart (see `dtos.ts`).
 */
import type {
  AudioPostProcessStepConfig,
  AudioProcessingUpdateRequest,
  BookOverviewDto,
  BulkAssignPreviewRequest,
  FfmpegTestRequest,
  ItemsEnqueueRequest,
  NarrationReleasePreviewRequest,
  ParagraphsEnqueueRequest,
  PauseDurations,
  PlanBookEditRequest,
  PreflightRunRequest,
  PronunciationDeletedDto,
  PronunciationDraftRequest,
  PronunciationImpactDto,
  PronunciationSavedDto,
  PronunciationSpanReport,
  ProposeBookEditRequest,
  ProposeOneBookEditRequest,
  Schema,
  SetPronunciationRequest,
  SetupCheckReport,
  StepPreviewRequest,
  StructureRuleDto,
  TryPronunciationRequest,
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
  Pin<BulkAssignPreviewRequest, Schema['BulkAssignPreviewRequest']>,
  Pin<NarrationReleasePreviewRequest, Schema['NarrationReleasePreviewRequest']>,
  Pin<ItemsEnqueueRequest, Schema['ItemsEnqueueRequest']>,
  Pin<PlanBookEditRequest, Schema['PlanBookEditRequest']>,
  Pin<ProposeBookEditRequest, Schema['ProposeBookEditRequest']>,
  Pin<ProposeOneBookEditRequest, Schema['ProposeOneBookEditRequest']>,
  Pin<BookOverviewDto, Schema['BookOverviewDto']>,
  Pin<StructureRuleDto, Schema['StructureRuleDto']>,
  Pin<SetPronunciationRequest, Schema['SetPronunciationRequest']>,
  Pin<PronunciationSavedDto, Schema['PronunciationSaved']>,
  Pin<PronunciationDeletedDto, Schema['PronunciationDeleted']>,
  Pin<PronunciationDraftRequest, Schema['PronunciationDraftRequest']>,
  Pin<PronunciationImpactDto, Schema['PronunciationImpactReport']>,
  Pin<TryPronunciationRequest, Schema['TryPronunciationRequest']>,
  Pin<PronunciationSpanReport, Schema['PronunciationSpanReport']>,
  Pin<SetupCheckReport, Schema['SetupCheckReport']>,
];
