import { ServiceFailure, type TimingItem, type TranscriptionResult } from "./service-adapter";
import { parseWav, type WavFormat } from "./tts";

export type TranscriptionFormat = TranscriptionResult["format"];
export const TRANSCRIPTION_FORMATS: readonly TranscriptionFormat[] = Object.freeze(["json", "verbose_json", "text", "srt", "vtt"]);

/**
 * The mounted model expects 24 kHz mono PCM16. The image is built with WHISPER_FFMPEG=OFF, so the
 * console advertises WAV only and never offers to convert anything.
 */
export const CANONICAL_WAV = Object.freeze({ sampleRate: 24_000, channels: 1, bitsPerSample: 16, audioFormat: 1 });

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Names how a recognizable WAV differs from Canonical WAV, or undefined when it matches. */
export function describeCanonicalDifference(format: WavFormat): string | undefined {
  const differences: string[] = [];
  if (format.audioFormat !== CANONICAL_WAV.audioFormat) differences.push(`WAV format ${format.audioFormat}, not uncompressed PCM`);
  if (format.channels !== CANONICAL_WAV.channels) differences.push(`${format.channels} channels, not 1 channel`);
  if (format.sampleRate !== CANONICAL_WAV.sampleRate) differences.push(`${format.sampleRate} Hz, not ${CANONICAL_WAV.sampleRate} Hz`);
  if (format.bitsPerSample !== CANONICAL_WAV.bitsPerSample) differences.push(`${format.bitsPerSample}-bit, not ${CANONICAL_WAV.bitsPerSample}-bit`);
  return differences.length === 0 ? undefined : differences.join(", ");
}

/**
 * Inspects the chosen upload's real header. The file is always sent as chosen: a header that is
 * unrecognizable or merely different from Canonical WAV is reported as a warning, never a block or a repair.
 */
export async function inspectUpload(file: File): Promise<string | undefined> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  const parsed = parseWav(bytes);
  if (!parsed.ok) return `This file is not recognizable WAV audio (${parsed.reason}) and is sent exactly as chosen. The service accepts WAV only.`;
  const difference = describeCanonicalDifference(parsed.format);
  return difference === undefined
    ? undefined
    : `This file differs from Canonical WAV (${CANONICAL_WAV.sampleRate} Hz mono PCM16): ${difference}. It is sent unchanged and the transcript may be inaccurate.`;
}

function protocolFailure(message: string, diagnostic: string): never {
  throw new ServiceFailure({ category: "protocol", message, diagnostic });
}

/** Accepts only a finite JSON number; every other shape is a timing the console will not repair. */
function timingNumber(value: unknown, diagnostic: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    protocolFailure("The service returned a non-finite timing value.", diagnostic);
  }
  return value;
}

function optionalProbability(value: unknown, diagnostic: string): number | undefined {
  if (value === undefined) return undefined;
  return timingNumber(value, diagnostic);
}

function readTiming(value: Record<string, unknown>, textKey: "text" | "word", diagnostic: string): TimingItem {
  const start = timingNumber(value.start, diagnostic);
  const end = timingNumber(value.end, diagnostic);
  if (end < start) protocolFailure("The service returned a reversed timing range.", diagnostic);
  const probability = optionalProbability(value.probability, diagnostic);
  return Object.freeze({
    text: typeof value[textKey] === "string" ? value[textKey] : "",
    start,
    end,
    ...(probability === undefined ? {} : { probability })
  });
}

export interface ParsedTranscription {
  readonly result: TranscriptionResult;
  readonly warnings: readonly string[];
}

/**
 * Parses one response into a Service Result. Structured formats are validated and presented in the
 * service's own order; text formats are preserved byte-for-byte including whitespace.
 */
export function parseTranscription(options: {
  readonly format: TranscriptionFormat;
  readonly body: string;
  readonly isJsonMediaType: boolean;
  readonly wordTimestampsRequested: boolean;
  readonly diagnostic: string;
}): ParsedTranscription {
  const { format, body, diagnostic } = options;
  if (format === "text" || format === "srt" || format === "vtt") {
    return Object.freeze({ result: Object.freeze({ kind: "transcription", format, text: body }), warnings: Object.freeze([]) });
  }
  if (!options.isJsonMediaType) protocolFailure("The service returned a success response that is not JSON.", diagnostic);
  let payload: unknown;
  try { payload = JSON.parse(body); } catch { protocolFailure("The service returned malformed JSON.", diagnostic); }
  if (!isRecord(payload) || typeof payload.text !== "string") {
    protocolFailure("The service returned an invalid transcription response.", diagnostic);
  }
  const text = payload.text;
  if (format === "json") {
    return Object.freeze({ result: Object.freeze({ kind: "transcription", format, text }), warnings: Object.freeze([]) });
  }

  const segments: TimingItem[] = [];
  const words: TimingItem[] = [];
  if (payload.segments !== undefined) {
    if (!Array.isArray(payload.segments)) protocolFailure("The service returned an invalid transcription response.", diagnostic);
    for (const segment of payload.segments) {
      if (!isRecord(segment)) protocolFailure("The service returned an invalid transcription response.", diagnostic);
      segments.push(readTiming(segment, "text", diagnostic));
      if (segment.words === undefined) continue;
      if (!Array.isArray(segment.words)) protocolFailure("The service returned an invalid transcription response.", diagnostic);
      for (const word of segment.words) {
        if (!isRecord(word)) protocolFailure("The service returned an invalid transcription response.", diagnostic);
        words.push(readTiming(word, "word", diagnostic));
      }
    }
  }
  const duration = payload.duration === undefined ? undefined : timingNumber(payload.duration, diagnostic);
  const language = typeof payload.language === "string" ? payload.language : undefined;
  // Missing words remain a success: the response is valid, so it is warned about rather than rejected.
  const warnings = options.wordTimestampsRequested && words.length === 0
    ? [`The service returned no word timings. Word timestamps were requested, so this transcript has no word alignment.`]
    : [];
  return Object.freeze({
    result: Object.freeze({
      kind: "transcription", format, text,
      ...(language === undefined ? {} : { language }),
      ...(duration === undefined ? {} : { duration }),
      ...(segments.length === 0 ? {} : { segments: Object.freeze(segments) }),
      ...(words.length === 0 ? {} : { words: Object.freeze(words) })
    }),
    warnings: Object.freeze(warnings)
  });
}
