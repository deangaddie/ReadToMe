/**
 * Compile-time pins between the hand-written shapes in `dtos.ts` and the generated OpenAPI schema.
 * Each entry asserts the hand-written type is assignable to the generated one, so a field the host
 * renames or drops fails `npm run typecheck` as soon as `npm run api:types` is re-run (and
 * `npm run api:check` fails `npm run check` while the checked-in schema is stale).
 *
 * Only shapes the host's document actually declares can be pinned: request bodies and the settings
 * entities. Response DTOs have no OpenAPI counterpart (see `dtos.ts`).
 */
import type {
  AppTheme,
  ApplyPreviewRequest,
  AttributionChainRequest,
  AudioProcessingUpdateRequest,
  BulkAssignPreviewRequest,
  GenerateDesignPromptRequest,
  ItemsEnqueueRequest,
  LlmServerConfig,
  LlmTestRequest,
  ManualImportRequest,
  NarratorOnlyModeRequest,
  ParagraphTtsServiceConfig,
  ParagraphsEnqueueRequest,
  PlanBookEditRequest,
  PreviewRequest,
  ProposeBookEditRequest,
  ProposeOneBookEditRequest,
  Schema,
  SemanticSimilarityServiceConfig,
  SetActiveRequest,
  SplitRuleRequest,
  TextSubstitutionStep,
  ThemeSelectionUpdate,
  ToSentenceCaseConfig,
  TranscriptionServiceConfig,
  UpdateProjectRequest,
  VoiceDesignServiceConfig,
} from './dtos';

type Pin<THand extends TGenerated, TGenerated> = THand;

export type SchemaPins = [
  Pin<LlmServerConfig, Schema['LlmServerConfig']>,
  Pin<ParagraphTtsServiceConfig, Schema['ParagraphTtsServiceConfig']>,
  Pin<TextSubstitutionStep, Schema['TextSubstitutionStep']>,
  Pin<ToSentenceCaseConfig, Schema['ToSentenceCaseConfig']>,
  Pin<VoiceDesignServiceConfig, Schema['VoiceDesignServiceConfig']>,
  Pin<TranscriptionServiceConfig, Schema['TranscriptionServiceConfig']>,
  Pin<SemanticSimilarityServiceConfig, Schema['SemanticSimilarityServiceConfig']>,
  Pin<SetActiveRequest, Schema['SetActiveRequest']>,
  Pin<AudioProcessingUpdateRequest, Schema['AudioProcessingUpdateRequest']>,
  Pin<AppTheme, Schema['AppTheme']>,
  Pin<ThemeSelectionUpdate, Schema['ThemeSelectionUpdateRequest']>,
  Pin<UpdateProjectRequest, Schema['UpdateProjectRequest']>,
  Pin<NarratorOnlyModeRequest, Schema['NarratorOnlyModeRequest']>,
  Pin<ManualImportRequest, Schema['ManualImportRequest']>,
  Pin<SplitRuleRequest, Schema['SplitRuleRequest']>,
  Pin<ParagraphsEnqueueRequest, Schema['ParagraphsEnqueueRequest']>,
  Pin<BulkAssignPreviewRequest, Schema['BulkAssignPreviewRequest']>,
  Pin<ItemsEnqueueRequest, Schema['ItemsEnqueueRequest']>,
  Pin<GenerateDesignPromptRequest, Schema['GenerateDesignPromptRequest']>,
  Pin<PreviewRequest, Schema['PreviewRequest']>,
  Pin<ApplyPreviewRequest, Schema['ApplyPreviewRequest']>,
  Pin<PlanBookEditRequest, Schema['PlanBookEditRequest']>,
  Pin<ProposeBookEditRequest, Schema['ProposeBookEditRequest']>,
  Pin<ProposeOneBookEditRequest, Schema['ProposeOneBookEditRequest']>,
  Pin<LlmTestRequest, Schema['LlmTestRequest']>,
  Pin<AttributionChainRequest, Schema['AttributionChainRequest']>,
];
