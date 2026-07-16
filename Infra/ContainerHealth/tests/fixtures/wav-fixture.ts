export interface WavFixtureOptions {
  readonly sampleRate?: number;
  readonly channels?: number;
  readonly bits?: number;
  readonly audioFormat?: number;
  readonly samples?: number;
  readonly riffSize?: number;
  readonly dataSize?: number;
  readonly extraChunk?: boolean;
}

/** Builds deterministic RIFF/WAVE bytes, optionally with a deliberately invalid field. */
export function buildWav(options: WavFixtureOptions = {}): Uint8Array {
  const sampleRate = options.sampleRate ?? 24_000;
  const channels = options.channels ?? 1;
  const bits = options.bits ?? 16;
  const samples = options.samples ?? 4;
  const dataBytes = samples * 2 * channels;
  const extra = options.extraChunk === true ? 12 : 0;
  const bytes = new Uint8Array(44 + dataBytes + extra);
  const view = new DataView(bytes.buffer);
  const ascii = (offset: number, value: string): void => {
    for (let index = 0; index < value.length; index += 1) view.setUint8(offset + index, value.charCodeAt(index));
  };
  ascii(0, "RIFF");
  view.setUint32(4, options.riffSize ?? 36 + dataBytes + extra, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, options.audioFormat ?? 1, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * channels * (bits / 8), true);
  view.setUint16(32, channels * (bits / 8), true);
  view.setUint16(34, bits, true);
  ascii(36, "data");
  view.setUint32(40, options.dataSize ?? dataBytes, true);
  if (extra > 0) {
    ascii(44 + dataBytes, "LIST");
    view.setUint32(48 + dataBytes, 4, true);
  }
  return bytes;
}
