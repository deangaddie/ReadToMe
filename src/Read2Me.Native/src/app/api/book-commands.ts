/**
 * Typed union of every command `POST /api/projects/{folder}/commands` accepts (ticket 05).
 *
 * Hand-written from `src/Read2Me.Core/Models/BookCommands.cs`: `type` is the record name minus the
 * `Command` suffix, properties are the record's positional parameters camelCased, `folderId` comes
 * from the route and is never sent. String enums (`MergeDirection`, `InsertPosition`, `PauseKind`,
 * `RuleMoveDirection`, `VoiceAnchorLevel`, `BookEditTargetKind`) travel as their member names.
 *
 * `BOOK_COMMAND_TYPES` is the runtime list; `book-commands.spec.ts` checks it against the host's
 * "Known types" 400 whenever a host is reachable, and the exhaustiveness check at the bottom fails
 * `npm run typecheck` when the union and the list drift apart.
 */
import type { Guid } from './dtos';

export type MergeDirection = 'Previous' | 'Next';
export type InsertPosition = 'Before' | 'After';
export type RuleMoveDirection = 'Up' | 'Down';
export type VoiceAnchorLevel = 'Volume' | 'Part' | 'Chapter' | 'Paragraph' | 'ParagraphItem';
export type BookEditTargetKind = 'VolumeTitle' | 'PartTitle' | 'ChapterTitle' | 'ParagraphItemText';
export type { PauseKind } from './dtos';
import type { PauseKind } from './dtos';

/** One item's resolved attribution (`ItemAttribution`); `characterId: null` never erases a stamp. */
export interface ItemAttribution {
  itemId: Guid;
  characterId: Guid | null;
  voiceInstructions?: string | null;
}

/** One AI book edit (`BookEditItem`). */
export interface BookEditItem {
  kind: BookEditTargetKind;
  id: Guid;
  newValue: string;
}

// ---- Delete ----------------------------------------------------------------------------------------
export interface DeleteVolume {
  type: 'DeleteVolume';
  volumeId: Guid;
}
export interface DeletePart {
  type: 'DeletePart';
  partId: Guid;
}
export interface DeleteChapter {
  type: 'DeleteChapter';
  chapterId: Guid;
}
export interface DeleteParagraph {
  type: 'DeleteParagraph';
  paragraphId: Guid;
}
export interface DeleteParagraphItem {
  type: 'DeleteParagraphItem';
  itemId: Guid;
}

// ---- Update ----------------------------------------------------------------------------------------
export interface UpdateVolumeTitle {
  type: 'UpdateVolumeTitle';
  volumeId: Guid;
  title: string;
}
export interface UpdatePartTitle {
  type: 'UpdatePartTitle';
  partId: Guid;
  title: string;
}
export interface UpdateChapterTitle {
  type: 'UpdateChapterTitle';
  chapterId: Guid;
  title: string;
}
export interface UpdateParagraphItemText {
  type: 'UpdateParagraphItemText';
  itemId: Guid;
  text: string;
}

// ---- Split -----------------------------------------------------------------------------------------
export interface SplitAtPart {
  type: 'SplitAtPart';
  partId: Guid;
  newVolumeTitle?: string | null;
}
export interface SplitAtChapter {
  type: 'SplitAtChapter';
  chapterId: Guid;
  newPartTitle?: string | null;
}
export interface SplitAtParagraph {
  type: 'SplitAtParagraph';
  paragraphId: Guid;
  newChapterTitle?: string | null;
}
export interface SplitAtItem {
  type: 'SplitAtItem';
  itemId: Guid;
}

// ---- Merge -----------------------------------------------------------------------------------------
export interface MergeVolume {
  type: 'MergeVolume';
  volumeId: Guid;
  direction: MergeDirection;
}
export interface MergePart {
  type: 'MergePart';
  partId: Guid;
  direction: MergeDirection;
}
export interface MergeChapter {
  type: 'MergeChapter';
  chapterId: Guid;
  direction: MergeDirection;
}
export interface MergeParagraph {
  type: 'MergeParagraph';
  paragraphId: Guid;
  direction: MergeDirection;
}
export interface MergeParagraphItem {
  type: 'MergeParagraphItem';
  itemId: Guid;
  direction: MergeDirection;
}

// ---- Characters ------------------------------------------------------------------------------------
export interface SetItemCharacter {
  type: 'SetItemCharacter';
  itemId: Guid;
  characterId: Guid | null;
}
export interface CreateCharacter {
  type: 'CreateCharacter';
  name: string;
}
export interface SetParagraphCharacter {
  type: 'SetParagraphCharacter';
  paragraphId: Guid;
  characterId: Guid | null;
  voiceInstructions?: string | null;
}
export interface SetParagraphsCharacter {
  type: 'SetParagraphsCharacter';
  paragraphIds: Guid[];
  characterId: Guid | null;
}
export interface AttributeItems {
  type: 'AttributeItems';
  paragraphId: Guid;
  items: ItemAttribution[];
}
export interface AddCharacterAlias {
  type: 'AddCharacterAlias';
  characterId: Guid;
  name: string;
}
export interface RemoveCharacterAlias {
  type: 'RemoveCharacterAlias';
  aliasId: Guid;
}
export interface MergeCharacters {
  type: 'MergeCharacters';
  survivorId: Guid;
  mergedId: Guid;
  addNameAsAlias: boolean;
}
export interface DeleteCharacter {
  type: 'DeleteCharacter';
  characterId: Guid;
}
export interface RenameCharacter {
  type: 'RenameCharacter';
  characterId: Guid;
  name: string;
}

// ---- Narrator --------------------------------------------------------------------------------------
/** `characterId: null` unlinks. 422 on an unknown character or a self-link. */
export interface SetNarratorCharacter {
  type: 'SetNarratorCharacter';
  characterId: Guid | null;
}

// ---- Voices ----------------------------------------------------------------------------------------
export interface CreateVoice {
  type: 'CreateVoice';
  characterId: Guid;
  name: string;
  isGenerated?: boolean;
}
export interface SetVoiceDefault {
  type: 'SetVoiceDefault';
  voiceId: Guid;
}
export interface UpdateVoice {
  type: 'UpdateVoice';
  voiceId: Guid;
  name: string;
  description?: string | null;
}
export interface SetVoiceDesignPrompt {
  type: 'SetVoiceDesignPrompt';
  voiceId: Guid;
  prompt: string;
}
export interface SetVoiceSettingsOverride {
  type: 'SetVoiceSettingsOverride';
  voiceId: Guid;
  json: string | null;
}
export interface SetVoiceTtsSettingsOverride {
  type: 'SetVoiceTtsSettingsOverride';
  voiceId: Guid;
  json: string | null;
}
export interface SetVoiceTranscript {
  type: 'SetVoiceTranscript';
  voiceId: Guid;
  transcript: string;
}
export interface SetVoiceAudio {
  type: 'SetVoiceAudio';
  voiceId: Guid;
  audioFileName: string;
}
export interface SetVoiceGenerated {
  type: 'SetVoiceGenerated';
  voiceId: Guid;
  audioFileName: string;
  transcript: string;
  designPrompt: string;
}
export interface SetVoiceSource {
  type: 'SetVoiceSource';
  voiceId: Guid;
  isGenerated: boolean;
}
export interface DeleteVoice {
  type: 'DeleteVoice';
  voiceId: Guid;
}

// ---- Voice rules -----------------------------------------------------------------------------------
export interface CreateVoiceRule {
  type: 'CreateVoiceRule';
  characterId: Guid;
  voiceId: Guid;
  fromLevel?: VoiceAnchorLevel | null;
  fromNodeId?: Guid | null;
  toLevel?: VoiceAnchorLevel | null;
  toNodeId?: Guid | null;
}
export interface DeleteVoiceRule {
  type: 'DeleteVoiceRule';
  ruleId: Guid;
}
export interface MoveVoiceRule {
  type: 'MoveVoiceRule';
  ruleId: Guid;
  direction: RuleMoveDirection;
}

// ---- Titles, pauses, insertion ---------------------------------------------------------------------
export interface AddBookTitle {
  type: 'AddBookTitle';
}
export interface AddVolumeTitles {
  type: 'AddVolumeTitles';
}
export interface AddPartTitles {
  type: 'AddPartTitles';
}
export interface AddChapterTitles {
  type: 'AddChapterTitles';
}
export interface AddPauses {
  type: 'AddPauses';
}
export interface InsertParagraphItem {
  type: 'InsertParagraphItem';
  anchorItemId: Guid;
  position: InsertPosition;
  text: string;
}
export interface InsertPauseParagraph {
  type: 'InsertPauseParagraph';
  anchorItemId: Guid;
  position: InsertPosition;
  pauseKind: PauseKind;
}

// ---- Other -----------------------------------------------------------------------------------------
export interface ClearBookContent {
  type: 'ClearBookContent';
}
export interface ApplyBookEdits {
  type: 'ApplyBookEdits';
  edits: BookEditItem[];
}
export interface SetParagraphItemAudio {
  type: 'SetParagraphItemAudio';
  itemId: Guid;
  audioFileName: string;
}
export interface SetAudioReview {
  type: 'SetAudioReview';
  paragraphItemId: Guid;
  normalizeOk: boolean;
  normalizeReason?: string | null;
  verifyOk: boolean;
  wer?: number | null;
  verifyReason?: string | null;
  transcript?: string | null;
  originalTextSnapshot?: string | null;
}
export interface DismissAudioReview {
  type: 'DismissAudioReview';
  paragraphItemId: Guid;
}

export type BookCommand =
  | DeleteVolume
  | DeletePart
  | DeleteChapter
  | DeleteParagraph
  | DeleteParagraphItem
  | UpdateVolumeTitle
  | UpdatePartTitle
  | UpdateChapterTitle
  | UpdateParagraphItemText
  | SplitAtPart
  | SplitAtChapter
  | SplitAtParagraph
  | SplitAtItem
  | MergeVolume
  | MergePart
  | MergeChapter
  | MergeParagraph
  | MergeParagraphItem
  | SetItemCharacter
  | CreateCharacter
  | SetParagraphCharacter
  | SetParagraphsCharacter
  | AttributeItems
  | AddCharacterAlias
  | RemoveCharacterAlias
  | MergeCharacters
  | DeleteCharacter
  | RenameCharacter
  | SetNarratorCharacter
  | CreateVoice
  | SetVoiceDefault
  | UpdateVoice
  | SetVoiceDesignPrompt
  | SetVoiceSettingsOverride
  | SetVoiceTtsSettingsOverride
  | SetVoiceTranscript
  | SetVoiceAudio
  | SetVoiceGenerated
  | SetVoiceSource
  | DeleteVoice
  | CreateVoiceRule
  | DeleteVoiceRule
  | MoveVoiceRule
  | AddBookTitle
  | AddVolumeTitles
  | AddPartTitles
  | AddChapterTitles
  | AddPauses
  | InsertParagraphItem
  | InsertPauseParagraph
  | ClearBookContent
  | ApplyBookEdits
  | SetParagraphItemAudio
  | SetAudioReview
  | DismissAudioReview;

export type BookCommandType = BookCommand['type'];

/** Every discriminator the host knows, alphabetical like its "Known types" 400 message. */
export const BOOK_COMMAND_TYPES = [
  'AddBookTitle',
  'AddChapterTitles',
  'AddCharacterAlias',
  'AddPartTitles',
  'AddPauses',
  'AddVolumeTitles',
  'ApplyBookEdits',
  'AttributeItems',
  'ClearBookContent',
  'CreateCharacter',
  'CreateVoice',
  'CreateVoiceRule',
  'DeleteChapter',
  'DeleteCharacter',
  'DeleteParagraph',
  'DeleteParagraphItem',
  'DeletePart',
  'DeleteVoice',
  'DeleteVoiceRule',
  'DeleteVolume',
  'DismissAudioReview',
  'InsertParagraphItem',
  'InsertPauseParagraph',
  'MergeChapter',
  'MergeCharacters',
  'MergeParagraph',
  'MergeParagraphItem',
  'MergePart',
  'MergeVolume',
  'MoveVoiceRule',
  'RemoveCharacterAlias',
  'RenameCharacter',
  'SetAudioReview',
  'SetItemCharacter',
  'SetNarratorCharacter',
  'SetParagraphCharacter',
  'SetParagraphItemAudio',
  'SetParagraphsCharacter',
  'SetVoiceAudio',
  'SetVoiceDefault',
  'SetVoiceDesignPrompt',
  'SetVoiceGenerated',
  'SetVoiceSettingsOverride',
  'SetVoiceSource',
  'SetVoiceTranscript',
  'SetVoiceTtsSettingsOverride',
  'SplitAtChapter',
  'SplitAtItem',
  'SplitAtParagraph',
  'SplitAtPart',
  'UpdateChapterTitle',
  'UpdateParagraphItemText',
  'UpdatePartTitle',
  'UpdateVoice',
  'UpdateVolumeTitle',
] as const satisfies readonly BookCommandType[];

/** Compile-time exhaustiveness: a union member missing from the list makes this `never`-typed. */
type MissingFromList = Exclude<BookCommandType, (typeof BOOK_COMMAND_TYPES)[number]>;
export const BOOK_COMMAND_LIST_COMPLETE: MissingFromList extends never ? true : never = true;
