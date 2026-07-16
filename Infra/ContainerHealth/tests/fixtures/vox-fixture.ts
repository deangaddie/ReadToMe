/** Builds VoxCPM2's five-byte type/uint32-LE-length framing without reusing the parser's own code. */
export function frame(type: number, payload: Uint8Array): Uint8Array {
  const bytes = new Uint8Array(5 + payload.byteLength);
  bytes[0] = type;
  new DataView(bytes.buffer).setUint32(1, payload.byteLength, true);
  bytes.set(payload, 5);
  return bytes;
}

export function controlFrame(value: unknown): Uint8Array {
  return frame(0, new TextEncoder().encode(JSON.stringify(value)));
}

/** Encodes little-endian float32 PCM exactly as the service does. */
export function pcmFrame(samples: readonly number[]): Uint8Array {
  const payload = new Uint8Array(samples.length * 4);
  const view = new DataView(payload.buffer);
  samples.forEach((sample, index) => view.setFloat32(index * 4, sample, true));
  return frame(1, payload);
}

export function concat(...parts: readonly Uint8Array[]): Uint8Array {
  const bytes = new Uint8Array(parts.reduce((total, part) => total + part.byteLength, 0));
  let offset = 0;
  for (const part of parts) { bytes.set(part, offset); offset += part.byteLength; }
  return bytes;
}

/** A complete meta/PCM/done exchange at the given rate. */
export function voxBytes(samples: readonly number[] = [0, 0.5, -0.5, 1], sampleRate = 24_000): Uint8Array {
  return concat(controlFrame({ type: "meta", sample_rate: sampleRate }), pcmFrame(samples), controlFrame({ type: "done", chunks: 1 }));
}

/**
 * Emits bytes as separate chunks at the given boundaries, so the parser must survive framing that
 * never aligns with frame edges. Each chunk is delivered asynchronously, as the network would.
 */
export function chunkedStream(bytes: Uint8Array, boundaries: readonly number[] = []): ReadableStream<Uint8Array> {
  const offsets = [0, ...boundaries, bytes.byteLength].filter((value, index, all) => all.indexOf(value) === index).sort((a, b) => a - b);
  let index = 0;
  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      if (index >= offsets.length - 1) { controller.close(); return; }
      await Promise.resolve();
      controller.enqueue(bytes.slice(offsets[index]!, offsets[index + 1]!));
      index += 1;
    }
  });
}

/** A stream that never completes, for cancellation coverage. */
export function pendingStream(prefix: Uint8Array = new Uint8Array(0)): ReadableStream<Uint8Array> {
  let sent = false;
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (sent || prefix.byteLength === 0) return new Promise<void>(() => {});
      sent = true;
      controller.enqueue(prefix);
      return undefined;
    }
  });
}

export function voxStreamResponse(bytes: Uint8Array, boundaries: readonly number[] = []): Response {
  return new Response(chunkedStream(bytes, boundaries), { status: 200, headers: { "content-type": "application/octet-stream" } });
}

export function uploadResponse(fileId = "fixture-file-id"): Response {
  return new Response(JSON.stringify({ file_id: fileId }), { status: 200, headers: { "content-type": "application/json" } });
}
