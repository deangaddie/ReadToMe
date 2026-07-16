import type { ServiceId } from "./readiness";

export interface WavFormat {
  readonly audioFormat: number;
  readonly channels: number;
  readonly sampleRate: number;
  readonly bitsPerSample: number;
  readonly dataBytes: number;
}

export type WavParse = { readonly ok: true; readonly format: WavFormat } | { readonly ok: false; readonly reason: string };

function failure(reason: string): WavParse {
  return Object.freeze({ ok: false, reason });
}

function chunkId(view: DataView, offset: number): string {
  return String.fromCharCode(view.getUint8(offset), view.getUint8(offset + 1), view.getUint8(offset + 2), view.getUint8(offset + 3));
}

/** Validates RIFF/WAVE structure without retaining or inspecting audible content. */
export function parseWav(bytes: Uint8Array): WavParse {
  if (bytes.byteLength < 12) return failure("The response is too short to be a WAV file.");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (chunkId(view, 0) !== "RIFF") return failure("The response does not start with a RIFF header.");
  if (chunkId(view, 8) !== "WAVE") return failure("The RIFF response is not WAVE data.");
  const riffSize = view.getUint32(4, true);
  if (riffSize < 4 || riffSize > bytes.byteLength - 8) return failure("The RIFF size does not match the received bytes.");

  let offset = 12;
  let format: Omit<WavFormat, "dataBytes"> | undefined;
  let dataBytes: number | undefined;
  while (offset + 8 <= bytes.byteLength) {
    const id = chunkId(view, offset);
    const size = view.getUint32(offset + 4, true);
    const body = offset + 8;
    if (size > bytes.byteLength - body) return failure(`The WAV ${id.trim() || "unnamed"} chunk is truncated.`);
    if (id === "fmt ") {
      if (size < 16) return failure("The WAV fmt chunk is too small.");
      format = {
        audioFormat: view.getUint16(body, true),
        channels: view.getUint16(body + 2, true),
        sampleRate: view.getUint32(body + 4, true),
        bitsPerSample: view.getUint16(body + 14, true)
      };
    } else if (id === "data") {
      dataBytes = size;
    }
    offset = body + size + (size % 2);
  }
  if (format === undefined) return failure("The WAV file has no fmt chunk.");
  if (dataBytes === undefined) return failure("The WAV file has no data chunk.");
  if (format.channels < 1) return failure("The WAV file declares no audio channels.");
  if (format.sampleRate < 1) return failure("The WAV file declares no sample rate.");
  if (format.bitsPerSample < 1) return failure("The WAV file declares no sample depth.");
  return Object.freeze({ ok: true, format: Object.freeze({ ...format, dataBytes }) });
}

export function describeWav(format: WavFormat): string {
  return `WAV format ${format.audioFormat} · ${format.channels} channel${format.channels === 1 ? "" : "s"} · ${format.sampleRate} Hz · ${format.bitsPerSample}-bit · ${format.dataBytes} data bytes`;
}

/** True only for an audio-compatible media type; HTTP success alone never proves audio. */
export function isAudioMediaType(value: string | null): boolean {
  return (value?.split(";", 1)[0]?.trim().toLowerCase() ?? "").startsWith("audio/");
}

/** Deterministic UTC service-id filename; never derived from input or response headers. */
export function audioFilename(serviceId: ServiceId, now: Date = new Date()): string {
  return `read2me-${serviceId}-${now.toISOString().replace(/[-:]/gu, "").replace(/\.\d+Z$/u, "Z")}.wav`;
}

const DOCUMENTED_AUDIO_EXTENSIONS = [".wav", ".mp3"];
const DOCUMENTED_AUDIO_TYPES = ["audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave", "audio/mpeg", "audio/mp3"];

/** Chatterbox and Qwen document WAV/MP3 but their decoders are more permissive, so this only warns. */
export function isDocumentedAudioFile(file: File): boolean {
  const name = file.name.toLowerCase();
  return DOCUMENTED_AUDIO_EXTENSIONS.some((extension) => name.endsWith(extension))
    || DOCUMENTED_AUDIO_TYPES.includes(file.type.toLowerCase());
}
