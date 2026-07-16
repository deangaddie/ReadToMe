import { ServiceFailure, WIRE_DIAGNOSTIC_LIMIT } from "./service-adapter";

export const VOX_UPLOAD_EXTENSIONS: readonly string[] = Object.freeze([".wav", ".mp3", ".flac", ".ogg", ".m4a"]);
export const VOX_UPLOAD_LIMIT_MIB = 50;
export const VOX_UPLOAD_LIMIT_BYTES = VOX_UPLOAD_LIMIT_MIB * 1_024 * 1_024;
const TRUNCATED = "\n[truncated]";
/** No real frame approaches this; a larger declared length is a framing error, not an allocation. */
const MAX_FRAME_BYTES = 64 * 1_024 * 1_024;
const PCM_FRAME_TYPE = 1;
const CONTROL_FRAME_TYPE = 0;
const HEADER_BYTES = 5;

/** The upload route accepts only these extensions, so an unsupported one is blocked before the request. */
export function isSupportedVoxUpload(file: File): boolean {
  const name = file.name.toLowerCase();
  const dot = name.lastIndexOf(".");
  // Mirrors the service's Path(filename).suffix: a leading dot is a dotfile, not an extension.
  return dot > 0 && VOX_UPLOAD_EXTENSIONS.includes(name.slice(dot));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Converts finite float32 samples to signed PCM16 and wraps them in a mono RIFF/WAVE header at the
 * reported rate. Samples outside [-1,1] are clamped rather than allowed to wrap around.
 */
export function buildPcm16Wav(samples: Float32Array, sampleRate: number): Uint8Array<ArrayBuffer> {
  const dataBytes = samples.length * 2;
  const bytes = new Uint8Array(44 + dataBytes);
  const view = new DataView(bytes.buffer);
  const ascii = (offset: number, value: string): void => {
    for (let index = 0; index < value.length; index += 1) view.setUint8(offset + index, value.charCodeAt(index));
  };
  ascii(0, "RIFF");
  view.setUint32(4, 36 + dataBytes, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, "data");
  view.setUint32(40, dataBytes, true);
  for (let index = 0; index < samples.length; index += 1) {
    const clamped = Math.min(1, Math.max(-1, samples[index]!));
    view.setInt16(44 + index * 2, Math.round(clamped * 32_767), true);
  }
  return bytes;
}

/** Accumulates arbitrary network chunks and releases only whole type/length-prefixed frames. */
class FrameBuffer {
  private buffer = new Uint8Array(0);

  append(chunk: Uint8Array): void {
    const next = new Uint8Array(this.buffer.byteLength + chunk.byteLength);
    next.set(this.buffer);
    next.set(chunk, this.buffer.byteLength);
    this.buffer = next;
  }

  get pending(): number { return this.buffer.byteLength; }

  /** The length the buffered header declares, once a whole header has arrived. */
  get declaredLength(): number | undefined {
    if (this.buffer.byteLength < HEADER_BYTES) return undefined;
    return new DataView(this.buffer.buffer, this.buffer.byteOffset, this.buffer.byteLength).getUint32(1, true);
  }

  take(): { readonly type: number; readonly payload: Uint8Array } | undefined {
    const length = this.declaredLength;
    if (length === undefined || this.buffer.byteLength < HEADER_BYTES + length) return undefined;
    const type = this.buffer[0]!;
    const payload = this.buffer.slice(HEADER_BYTES, HEADER_BYTES + length);
    this.buffer = this.buffer.slice(HEADER_BYTES + length);
    return { type, payload };
  }
}

export interface ParsedVoxStream {
  readonly sampleRate: number;
  readonly samples: Float32Array;
  readonly diagnostic: string;
}

/**
 * Consumes VoxCPM2's framed stream across arbitrary chunk boundaries: one required meta frame,
 * zero or more float32 PCM frames, then done. Anything else is a failure and never partial audio.
 */
export async function parseVoxStream(stream: ReadableStream<Uint8Array>, signal: AbortSignal): Promise<ParsedVoxStream> {
  if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  const reader = stream.getReader();
  const frames = new FrameBuffer();
  const notes: string[] = [];
  let noteLength = 0;
  let frameCount = 0;
  let pcmBytes = 0;
  let sampleRate: number | undefined;
  let done = false;
  const chunks: Float32Array[] = [];
  let totalSamples = 0;

  const addNote = (note: string): void => {
    if (noteLength >= WIRE_DIAGNOSTIC_LIMIT) return;
    const remaining = WIRE_DIAGNOSTIC_LIMIT - noteLength;
    if (note.length + 1 > remaining) {
      notes.push(`${note.slice(0, Math.max(0, remaining - TRUNCATED.length))}${TRUNCATED}`);
      noteLength = WIRE_DIAGNOSTIC_LIMIT;
      return;
    }
    notes.push(note);
    noteLength += note.length + 1;
  };
  // Diagnostics stay byte-only: frame types, lengths, counts, and control JSON. Never PCM content.
  const diagnostic = (): string => notes.join("\n");
  // The explicit annotation lets narrowing treat a fail(...) call as an end point.
  const fail: (message: string) => never = (message) => {
    throw new ServiceFailure({ category: "protocol", message, diagnostic: diagnostic() });
  };

  const onAbort = (): void => { void reader.cancel(); };
  signal.addEventListener("abort", onAbort, { once: true });

  const handleControl = (payload: Uint8Array): void => {
    const text = new TextDecoder("utf-8", { fatal: false }).decode(payload);
    let value: unknown;
    try { value = JSON.parse(text); } catch { addNote(`control: ${text.slice(0, 2_048)}`); fail("The service returned a malformed control frame."); }
    if (!isRecord(value) || typeof value.type !== "string") { addNote(`control: ${text.slice(0, 2_048)}`); fail("The service returned an invalid control frame."); }
    const type = value.type;
    addNote(`frame ${frameCount}: control ${type} · ${payload.byteLength} bytes`);
    if (type === "error") {
      // A framed error is the reached service reporting its own failure, exactly as an SSE error envelope is.
      const message = typeof value.message === "string" && value.message.trim() !== "" ? value.message : "The service reported a generation error.";
      throw new ServiceFailure({ category: "http", message, serviceMessage: message, diagnostic: diagnostic() });
    }
    if (type === "meta") {
      if (sampleRate !== undefined) fail("The service sent a duplicate meta frame.");
      const rate = value.sample_rate;
      if (typeof rate !== "number" || !Number.isInteger(rate) || rate < 1) fail("The service reported an invalid sample rate.");
      sampleRate = rate;
      return;
    }
    if (type === "done") {
      if (sampleRate === undefined) fail("The service ended the stream before reporting its audio format.");
      done = true;
      return;
    }
    fail(`The service sent an unknown control frame: ${type}.`);
  };

  const handlePcm = (payload: Uint8Array): void => {
    if (sampleRate === undefined) fail("The service sent audio before reporting its audio format.");
    if (payload.byteLength % 4 !== 0) fail("The service sent an audio frame that is not whole float32 samples.");
    addNote(`frame ${frameCount}: pcm · ${payload.byteLength} bytes`);
    pcmBytes += payload.byteLength;
    const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const samples = new Float32Array(payload.byteLength / 4);
    for (let index = 0; index < samples.length; index += 1) {
      const sample = view.getFloat32(index * 4, true);
      if (!Number.isFinite(sample)) fail("The service sent a non-finite audio sample.");
      samples[index] = sample;
    }
    chunks.push(samples);
    totalSamples += samples.length;
  };

  const consume = (): void => {
    while (true) {
      const declared = frames.declaredLength;
      // A length no real frame can reach must fail before it is ever treated as a pending read.
      if (declared !== undefined && declared > MAX_FRAME_BYTES) fail(`The service declared an impossible frame length of ${declared} bytes.`);
      const frame = frames.take();
      if (frame === undefined) return;
      frameCount += 1;
      if (frame.type === CONTROL_FRAME_TYPE) handleControl(frame.payload);
      else if (frame.type === PCM_FRAME_TYPE) handlePcm(frame.payload);
      else { addNote(`frame ${frameCount}: type ${frame.type} · ${frame.payload.byteLength} bytes`); fail(`The service sent an unknown frame type: ${frame.type}.`); }
      if (done) return;
    }
  };

  try {
    while (!done) {
      const item = await reader.read();
      if (signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
      if (item.done) break;
      frames.append(item.value);
      consume();
    }
    if (!done) {
      addNote(`[incomplete] ${frameCount} frames · ${pcmBytes} PCM bytes · ${frames.pending} unparsed bytes`);
      throw new ServiceFailure({ category: "protocol", message: "The service response ended before completion.", diagnostic: diagnostic() });
    }
    // Done terminates the stream, so anything still buffered behind it is an out-of-order frame.
    if (frames.pending > 0) fail("The service sent a frame after completing the stream.");
    await reader.cancel();
    // A done frame is only accepted after meta, so this narrows the rate rather than guarding a real case.
    if (sampleRate === undefined) fail("The service response ended before completion.");
    addNote(`total: ${frameCount} frames · ${pcmBytes} PCM bytes · ${totalSamples} samples · ${sampleRate} Hz`);
    const samples = new Float32Array(totalSamples);
    let offset = 0;
    for (const chunk of chunks) { samples.set(chunk, offset); offset += chunk.length; }
    return Object.freeze({ sampleRate, samples, diagnostic: diagnostic() });
  } catch (error) {
    if (signal.aborted || (error instanceof DOMException && error.name === "AbortError")) throw new DOMException("The operation was aborted.", "AbortError");
    if (error instanceof ServiceFailure) throw error;
    throw new ServiceFailure({
      category: "protocol", message: "The service response ended before completion.",
      diagnostic: `${diagnostic()}\nStream read failed: ${error instanceof Error ? error.message : "unknown error"}`
    });
  } finally {
    signal.removeEventListener("abort", onAbort);
    reader.releaseLock();
  }
}
