import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { expect, test } from "@playwright/test";
import { parseWav } from "../src/tts";

const assetDirectory = resolve(process.cwd(), "test-assets");

test("the live speech fixture is an 8-10 second 24 kHz mono PCM16 WAV with exact transcript and provenance", async () => {
  const [wavBytes, transcriptBytes, provenance] = await Promise.all([
    readFile(resolve(assetDirectory, "read2me-acceptance.wav")),
    readFile(resolve(assetDirectory, "read2me-acceptance.txt")),
    readFile(resolve(assetDirectory, "README.md"), "utf8")
  ]);

  const parsed = parseWav(wavBytes);
  expect(parsed.ok).toBe(true);
  if (!parsed.ok) return;

  expect(parsed.format.audioFormat).toBe(1);
  expect(parsed.format.channels).toBe(1);
  expect(parsed.format.sampleRate).toBe(24_000);
  expect(parsed.format.bitsPerSample).toBe(16);
  const durationSeconds = parsed.format.dataBytes /
    (parsed.format.sampleRate * parsed.format.channels * (parsed.format.bitsPerSample / 8));
  expect(durationSeconds).toBeGreaterThanOrEqual(8);
  expect(durationSeconds).toBeLessThanOrEqual(10);

  const transcript = new TextDecoder("utf-8", { fatal: true }).decode(transcriptBytes);
  expect(transcript).toBe("This recording is a non-sensitive test fixture for Read to Me. Clear speech verifies transcription and voice cloning on local services.\n");
  expect(transcriptBytes.subarray(0, 3)).not.toEqual(Buffer.from([0xef, 0xbb, 0xbf]));
  expect(provenance).toContain("CC0-1.0");
  expect(provenance).toContain("Microsoft David Desktop");
  expect(provenance).toContain("2026-07-16");
});
