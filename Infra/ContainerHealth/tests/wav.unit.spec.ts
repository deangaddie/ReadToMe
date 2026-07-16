import { expect, test } from "@playwright/test";
import { audioFilename, describeWav, isAudioMediaType, isDocumentedAudioFile, parseWav } from "../src/tts";
import { buildWav } from "./fixtures/wav-fixture";

test("correct RIFF/WAVE bytes parse to their exact declared format", () => {
  const parsed = parseWav(buildWav({ sampleRate: 24_000, samples: 8 }));
  expect(parsed).toEqual({ ok: true, format: { audioFormat: 1, channels: 1, sampleRate: 24_000, bitsPerSample: 16, dataBytes: 16 } });
  if (!parsed.ok) throw new Error("unreachable");
  expect(describeWav(parsed.format)).toBe("WAV format 1 · 1 channel · 24000 Hz · 16-bit · 16 data bytes");
  expect(parseWav(buildWav({ sampleRate: 44_100, channels: 2, extraChunk: true }))).toMatchObject({ ok: true, format: { channels: 2, sampleRate: 44_100 } });
});

test("truncated, malformed, and structurally impossible WAV bytes are rejected with a reason", () => {
  const cases: ReadonlyArray<readonly [string, Uint8Array]> = [
    ["too short", buildWav().subarray(0, 8)],
    ["not RIFF", (() => { const b = buildWav(); b[0] = 0x52; b[1] = 0x49; b[2] = 0x46; b[3] = 0x47; return b; })()],
    ["not WAVE", (() => { const b = buildWav(); b[8] = 0x4a; return b; })()],
    ["truncated data chunk", buildWav({ samples: 64 }).subarray(0, 60)],
    ["riff size larger than payload", buildWav({ riffSize: 9_999 })],
    ["impossible data size", buildWav({ dataSize: 9_999 })],
    ["zero sample rate", buildWav({ sampleRate: 0 })],
    ["zero channels", buildWav({ channels: 0 })],
    ["zero bit depth", buildWav({ bits: 0 })]
  ];
  for (const [name, bytes] of cases) {
    const parsed = parseWav(bytes);
    expect(parsed.ok, name).toBe(false);
    if (!parsed.ok) expect(parsed.reason.length, name).toBeGreaterThan(0);
  }
  expect(parseWav(new Uint8Array(0))).toMatchObject({ ok: false });
});

test("media types are audio-compatible only for audio subtypes", () => {
  for (const value of ["audio/wav", "audio/x-wav; charset=binary", "AUDIO/WAVE", "audio/mpeg"]) expect(isAudioMediaType(value), value).toBe(true);
  for (const value of ["application/json", "text/plain", "application/octet-stream", null]) expect(isAudioMediaType(value), String(value)).toBe(false);
});

test("filenames are safe UTC service-id names and never derive from input", () => {
  expect(audioFilename("chatterbox", new Date("2026-07-16T09:08:07.123Z"))).toBe("read2me-chatterbox-20260716T090807Z.wav");
  expect(audioFilename("qwen3-tts-base", new Date("2026-01-02T00:00:00.000Z"))).toBe("read2me-qwen3-tts-base-20260102T000000Z.wav");
  expect(audioFilename("chatterbox")).toMatch(/^read2me-chatterbox-\d{8}T\d{6}Z\.wav$/u);
});

test("documented audio inputs are recognised by extension or MIME type", () => {
  expect(isDocumentedAudioFile(new File(["x"], "voice.WAV", { type: "" }))).toBe(true);
  expect(isDocumentedAudioFile(new File(["x"], "voice.mp3", { type: "" }))).toBe(true);
  expect(isDocumentedAudioFile(new File(["x"], "voice", { type: "audio/wav" }))).toBe(true);
  expect(isDocumentedAudioFile(new File(["x"], "voice.flac", { type: "audio/flac" }))).toBe(false);
});
