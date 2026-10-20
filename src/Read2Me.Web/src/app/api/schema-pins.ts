/**
 * Compile-time pins between the hand-written shapes in `dtos.ts` and the generated OpenAPI schema.
 * Each entry asserts the hand-written type is assignable to the generated one, so a field the host
 * renames or drops fails `bun run typecheck` as soon as `bun run api:types` is re-run (and
 * `bun run api:check` fails `bun run check` while the checked-in schema is stale).
 *
 * Only shapes the host's document actually declares can be pinned: request bodies, the settings
 * entities and the book overview. Other response DTOs have no OpenAPI counterpart (see `dtos.ts`).
 */
import type {
  AppTheme,
  ApplyPreviewRequest,
  AttributionChainRequest,
  AudioPostProcessStepConfig,
  AudioProcessingUpdateRequest,
  BookOverviewDto,
  BulkAssignPreviewRequest,
  FfmpegTestRequest,
  GenerateDesignPromptRequest,
  GenerateVoiceAudioRequest,
  ItemsEnqueueRequest,
  LlmServerConfig,
  LlmTestRequest,
  ManualImportRequest,
  NarrationReleasePreviewRequest,
  NarratorOnlyModeRequest,
  ParagraphTtsServiceConfig,
  ParagraphsEnqueueRequest,
  PauseDurations,
  PlanBookEditRequest,
  PreflightRunRequest,
  PreviewRequest,
  PromptPreviewRequest,
  ProposeBookEditRequest,
  ProposeOneBookEditRequest,
  Schema,
  SemanticSimilarityServiceConfig,
  SetActiveRequest,
  SetCheckpointRequest,
  SimilarityTestRequest,
  SplitRuleRequest,
  StepPreviewRequest,
  StructureRuleDto,
  TextSubstitutionStep,
  ThemeSelectionUpdate,
  ToSentenceCaseConfig,
  TranscriptionServiceConfig,
  UpdateProjectRequest,
  VoiceDesignSampleTextRequest,
  VoiceDesignServiceConfig,
  VoiceDesignTestRequest,
} from './dtos';
import type { paths } from './schema';

type Pin<THand extends TGenerated, TGenerated> = THand;

export type SchemaPins = [
  Pin<LlmServerConfig, Schema['LlmServerConfig']>,
  Pin<PreflightRunRequest, Schema['PreflightRunRequest']>,
  Pin<ParagraphTtsServiceConfig, Schema['ParagraphTtsServiceConfig']>,
  Pin<TextSubstitutionStep, Schema['TextSubstitutionStep']>,
  Pin<ToSentenceCaseConfig, Schema['ToSentenceCaseConfig']>,
  Pin<VoiceDesignServiceConfig, Schema['VoiceDesignServiceConfig']>,
  Pin<TranscriptionServiceConfig, Schema['TranscriptionServiceConfig']>,
  Pin<SemanticSimilarityServiceConfig, Schema['SemanticSimilarityServiceConfig']>,
  Pin<SetActiveRequest, Schema['SetActiveRequest']>,
  Pin<AudioProcessingUpdateRequest, Schema['AudioProcessingUpdateRequest']>,
  Pin<AudioPostProcessStepConfig, Schema['AudioPostProcessStepConfig']>,
  Pin<PauseDurations, Schema['PauseDurationsDto']>,
  Pin<FfmpegTestRequest, Schema['FfmpegTestRequest']>,
  Pin<StepPreviewRequest, Schema['StepPreviewRequest']>,
  Pin<AppTheme, Schema['AppTheme']>,
  Pin<ThemeSelectionUpdate, Schema['ThemeSelectionUpdateRequest']>,
  Pin<UpdateProjectRequest, Schema['UpdateProjectRequest']>,
  Pin<NarratorOnlyModeRequest, Schema['NarratorOnlyModeRequest']>,
  Pin<SetCheckpointRequest, Schema['SetCheckpointRequest']>,
  Pin<ManualImportRequest, Schema['ManualImportRequest']>,
  Pin<SplitRuleRequest, Schema['SplitRuleRequest']>,
  Pin<ParagraphsEnqueueRequest, Schema['ParagraphsEnqueueRequest']>,
  Pin<BulkAssignPreviewRequest, Schema['BulkAssignPreviewRequest']>,
  Pin<NarrationReleasePreviewRequest, Schema['NarrationReleasePreviewRequest']>,
  Pin<ItemsEnqueueRequest, Schema['ItemsEnqueueRequest']>,
  Pin<GenerateDesignPromptRequest, Schema['GenerateDesignPromptRequest']>,
  Pin<PreviewRequest, Schema['PreviewRequest']>,
  Pin<PromptPreviewRequest, Schema['PromptPreviewRequest']>,
  Pin<ApplyPreviewRequest, Schema['ApplyPreviewRequest']>,
  Pin<PlanBookEditRequest, Schema['PlanBookEditRequest']>,
  Pin<ProposeBookEditRequest, Schema['ProposeBookEditRequest']>,
  Pin<ProposeOneBookEditRequest, Schema['ProposeOneBookEditRequest']>,
  Pin<LlmTestRequest, Schema['LlmTestRequest']>,
  Pin<AttributionChainRequest, Schema['AttributionChainRequest']>,
  Pin<VoiceDesignSampleTextRequest, Schema['VoiceDesignSampleTextRequest']>,
  Pin<VoiceDesignTestRequest, Schema['VoiceDesignTestRequest']>,
  Pin<SimilarityTestRequest, Schema['SimilarityTestRequest']>,
  Pin<BookOverviewDto, Schema['BookOverviewDto']>,
  Pin<StructureRuleDto, Schema['StructureRuleDto']>,
  // Inline in the document (the handler reads it by hand), so pinned to its operation.
  Pin<
    GenerateVoiceAudioRequest,
    NonNullable<
      paths['/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio']['post']['requestBody']
    >['content']['application/json']
  >,
];
