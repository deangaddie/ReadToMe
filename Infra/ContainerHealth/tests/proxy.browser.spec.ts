import { expect, test, type Page } from "@playwright/test";

const services = ["llama", "chatterbox", "chatterbox-turbo", "qwen3-tts", "qwen3-tts-base", "voxcpm2", "whisper", "minilm-l6", "mpnet-base-v2"] as const;

function failOnBrowserErrors(page: Page): void {
  page.on("pageerror", (error) => { throw error; });
  page.on("console", (message) => {
    if (message.type() === "error") throw new Error(`Browser console error: ${message.text()}`);
  });
}

test.beforeEach(async ({ page }) => {
  failOnBrowserErrors(page);
});

test("the shell is served from IPv4 loopback", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL("http://127.0.0.1:5173/");
  await expect(page.getByRole("heading", { name: "Service dashboard" })).toBeVisible();
});

test("all nine fixed prefixes rewrite to the target root without cross-matching", async ({ request }) => {
  for (const service of services) {
    const response = await request.post(`/proxy/${service}/echo?marker=${service}`, { data: `body:${service}`, headers: { "content-type": "application/octet-stream" } });
    expect(response.ok()).toBeTruthy();
    expect(await response.json()).toEqual({
      service,
      method: "POST",
      path: `/echo?marker=${service}`,
      body: Buffer.from(`body:${service}`).toString("base64"),
      contentType: "application/octet-stream"
    });
  }
});

test("multipart uploads cross the proxy byte-for-byte", async ({ page }) => {
  await page.goto("/");
  const expected = Buffer.from("--read2me-boundary\r\nContent-Disposition: form-data; name=\"text\"\r\n\r\nhello proxy\r\n--read2me-boundary\r\nContent-Disposition: form-data; name=\"reference_audio\"; filename=\"voice.wav\"\r\nContent-Type: audio/wav\r\n\r\n\x00\x01\x02\x7f\x80\xff\r\n--read2me-boundary--\r\n", "binary");
  const result = await page.evaluate(async (base64Body) => {
    const body = Uint8Array.from(atob(base64Body), (character) => character.charCodeAt(0));
    const response = await fetch("/proxy/chatterbox/echo", {
      method: "POST",
      headers: { "content-type": "multipart/form-data; boundary=read2me-boundary" },
      body
    });
    return await response.json() as { body: string; contentType: string };
  }, expected.toString("base64"));
  const captured = Buffer.from(result.body, "base64");
  expect(result.contentType).toBe("multipart/form-data; boundary=read2me-boundary");
  expect(captured).toEqual(expected);
});

test("SSE, framed binary, and WAV responses retain bytes, media types, and headers", async ({ request }) => {
  const sse = await request.get("/proxy/llama/sse");
  expect(sse.headers()["content-type"]).toContain("text/event-stream");
  expect(sse.headers()["x-fixture"]).toBe("sse");
  expect(await sse.body()).toEqual(Buffer.from("data: {\"part\":\"one\"}\n\ndata: [DONE]\n\n"));

  const framed = await request.get("/proxy/voxcpm2/framed");
  expect(framed.headers()["content-type"]).toContain("application/octet-stream");
  expect(await framed.body()).toEqual(Buffer.from([0, 3, 0, 0, 0, 97, 98, 99, 1, 2, 0, 0, 0, 0, 255]));

  const wav = await request.get("/proxy/whisper/wav");
  const wavBytes = await wav.body();
  expect(wav.headers()["content-type"]).toContain("audio/wav");
  expect(wav.headers()["x-fixture"]).toBe("wav");
  expect(wavBytes.subarray(0, 12)).toEqual(Buffer.from("RIFF(\u0000\u0000\u0000WAVE", "binary"));
  expect(wavBytes.readInt16LE(44)).toBe(-1234);
  expect(wavBytes.readInt16LE(46)).toBe(2345);
});

test("slow chunked responses complete through streaming backpressure", async ({ page }) => {
  await page.goto("/");
  const result = await page.evaluate(async () => {
    const response = await fetch("/proxy/qwen3-tts/slow");
    const reader = response.body?.getReader();
    if (!reader) throw new Error("Missing response stream");
    let bytes = 0;
    let chunks = 0;
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      bytes += next.value.byteLength;
      chunks += 1;
    }
    return { bytes, chunks };
  });
  expect(result.bytes).toBe(8 * 1024 * 1024);
  expect(result.chunks).toBeGreaterThan(1);
});

test("browser abort closes the proxied upstream response", async ({ page }) => {
  await page.goto("/");
  const observed = await page.evaluate(async () => {
    const controller = new AbortController();
    const observedPromise = fetch("/proxy/qwen3-tts-base/abort-status").then((item) => item.json()) as Promise<{ abortObserved: boolean }>;
    const pending = fetch("/proxy/qwen3-tts-base/abort", { signal: controller.signal });
    const response = await pending;
    const reader = response.body?.getReader();
    await reader?.read();
    controller.abort();
    try { await reader?.read(); } catch { /* expected abort */ }
    return (await observedPromise).abortObserved;
  });
  expect(observed).toBeTruthy();
});

test("reached-service errors and malformed successes pass through unchanged", async ({ request }) => {
  const error = await request.get("/proxy/minilm-l6/error");
  expect(error.status()).toBe(418);
  expect(error.headers()["content-type"]).toContain("application/problem+json");
  expect(error.headers()["x-service-error"]).toBe("minilm-l6");
  expect(await error.text()).toBe('{"detail":"reached service"}');

  const malformed = await request.get("/proxy/minilm-l6/malformed");
  expect(malformed.status()).toBe(200);
  expect(malformed.headers()["content-type"]).toContain("application/json");
  expect(await malformed.text()).toBe("{not-json");
});

test("an unreachable fixed target returns bounded service-labelled JSON 502", async ({ request }) => {
  const shutdown = await request.post("/proxy/mpnet-base-v2/shutdown");
  expect(shutdown.ok()).toBeTruthy();

  let response = await request.get("/proxy/mpnet-base-v2/echo");
  for (let attempt = 0; response.status() !== 502 && attempt < 20; attempt += 1) {
    response = await request.get("/proxy/mpnet-base-v2/echo");
  }
  expect(response.status()).toBe(502);
  expect(response.headers()["content-type"]).toContain("application/json");
  const body = await response.body();
  expect(body.byteLength).toBeLessThan(512);
  expect(JSON.parse(body.toString())).toMatchObject({ kind: "proxy-unavailable", service: "mpnet-base-v2" });
});
