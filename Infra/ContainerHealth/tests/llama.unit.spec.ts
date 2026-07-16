import { expect, test } from "@playwright/test";
import { LlamaPreparationController, parseLlamaSse } from "../src/llama";
import { createLlamaAdapter, type FormValues } from "../src/service-adapter";

function models(data: unknown[]): Response {
  return new Response(JSON.stringify({ object: "list", data }), { headers: { "content-type": "application/json" } });
}

function preparationController(): LlamaPreparationController {
  return new LlamaPreparationController(createLlamaAdapter().prepareForm!);
}

test("Llama preparation validates statuses, selects loaded first, and preserves a valid selection", async () => {
  const controller = preparationController();
  const first = await controller.refresh(async () => models([
    { id: "sleeping", status: { value: "sleeping", preset: true } },
    { id: "loaded", status: { value: "loaded", preset: true } },
    { id: "failed", status: { value: "unloaded", preset: true, failed: true, last_error: "bad model" } }
  ]));
  expect(first.status).toBe("prepared");
  expect(first.selectedModel).toBe("loaded");
  expect(first.models.map(({ id, label, runnable }) => ({ id, label, runnable }))).toEqual([
    { id: "sleeping", label: "sleeping — Sleeping", runnable: true },
    { id: "loaded", label: "loaded — Loaded", runnable: true },
    { id: "failed", label: "failed — Failed", runnable: false }
  ]);

  controller.select("sleeping");
  const refreshed = await controller.refresh(async () => models([
    { id: "loaded", status: { value: "loaded", preset: true } },
    { id: "sleeping", status: { value: "sleeping", preset: true } }
  ]));
  expect(refreshed.selectedModel).toBe("sleeping");
});

test("Llama preparation falls back to the first non-failed preset and rejects malformed responses", async () => {
  const controller = preparationController();
  const snapshot = await controller.refresh(async () => models([
    { id: "alias-not-preset", status: { value: "loaded" } },
    { id: "failed", status: { value: "unloaded", preset: true, failed: true } },
    { id: "loading", status: { value: "loading", preset: true } },
    { id: "unloaded", status: { value: "unloaded", preset: true } }
  ]));
  expect(snapshot.selectedModel).toBe("loading");
  expect(snapshot.models.map(({ id }) => id)).not.toContain("alias-not-preset");

  const failed = await controller.refresh(async () => new Response("not json", { headers: { "content-type": "text/plain" } }));
  expect(failed.status).toBe("failed");
  expect(failed.selectedModel).toBeUndefined();
  expect(failed.diagnostic).toContain("not json");

  const oversized = await controller.refresh(async () => new Response("x".repeat(70 * 1_024), { headers: { "content-type": "text/plain" } }));
  expect(oversized.diagnostic).toMatch(/\[truncated\]$/u);
});

test("Llama preparation ignores a stale response after a newer epoch", async () => {
  const controller = preparationController();
  let release!: (value: Response) => void;
  const slow = controller.refresh(async () => await new Promise<Response>((resolve) => { release = resolve; }));
  const fast = await controller.refresh(async () => models([{ id: "new", status: { value: "loaded", preset: true } }]));
  release(models([{ id: "old", status: { value: "loaded", preset: true } }]));
  await slow;
  expect(controller.snapshot).toEqual(fast);
});

function chunks(parts: readonly Uint8Array[]): ReadableStream<Uint8Array> {
  return new ReadableStream({ start(controller) { for (const part of parts) controller.enqueue(part); controller.close(); } });
}

test("Llama SSE parses arbitrary UTF-8 splits, CRLF comments, reasoning fallback, content, and terminal metadata", async () => {
  const source = ": keepalive\r\n\r\ndata: {\"choices\":[{\"delta\":{\"reasoning\":\"think 💡\"}}]}\r\n\r\n"
    + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" more\",\"content\":\"answer\"},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":3},\"timings\":{\"predicted_ms\":12}}\n\n"
    + "data: [DONE]\n\n";
  const bytes = new TextEncoder().encode(source);
  const deltas: string[] = [];
  for (let split = 1; split < bytes.length; split += 1) {
    deltas.length = 0;
    const result = await parseLlamaSse(chunks([bytes.slice(0, split), bytes.slice(split)]), new AbortController().signal, (event) => deltas.push(`${event.kind}:${event.text}`));
    expect(result).toEqual({ thinking: "think 💡 more", answer: "answer", finishReason: "stop", usage: { completion_tokens: 3 }, timing: { predicted_ms: 12 }, diagnostic: expect.stringContaining("[DONE]") });
  }
  expect(deltas).toEqual(["thinking-delta:think 💡", "thinking-delta: more", "answer-delta:answer"]);
});

test("Llama SSE rejects malformed envelopes, missing DONE, and abort without returning partial output", async () => {
  const signal = new AbortController().signal;
  await expect(parseLlamaSse(chunks([new TextEncoder().encode("data: {bad}\n\n")]), signal, () => {})).rejects.toMatchObject({ category: "protocol" });
  await expect(parseLlamaSse(chunks([new TextEncoder().encode('data: {"choices":[{"delta":{"content":"partial"}}]}\n\n')]), signal, () => {})).rejects.toMatchObject({ category: "protocol", message: "The service response ended before completion.", partialResult: { answer: "partial" }, diagnostic: expect.stringContaining("Partial answer:\npartial") });
  const controller = new AbortController();
  controller.abort();
  await expect(parseLlamaSse(chunks([]), controller.signal, () => {})).rejects.toMatchObject({ name: "AbortError" });
});

test("Llama SSE settles at DONE without waiting for socket EOF and bounds metadata diagnostics", async () => {
  const encoded = new TextEncoder().encode(`${Array.from({ length: 4_000 }, () => 'data: {"choices":[{"delta":{}}]}\n\n').join("")}data: [DONE]\n\n`);
  let pulls = 0;
  const stream = new ReadableStream<Uint8Array>({
    pull(controller) {
      pulls += 1;
      if (pulls === 1) controller.enqueue(encoded);
      else throw new Error("Parser read beyond the terminal event.");
    }
  }, { highWaterMark: 0 });
  const parsed = await parseLlamaSse(stream, new AbortController().signal, () => {});
  expect(parsed.diagnostic).toContain("[DONE]");
  expect(parsed.diagnostic.length).toBeLessThanOrEqual(65_550);
  expect(pulls).toBe(1);
});

test("Llama adapter declares exact defaults and rejects blank, numeric, JSON, and reserved-property inputs", () => {
  const adapter = createLlamaAdapter();
  expect(adapter.initialValues()).toEqual({
    prompt: "", model: "", temperature: "0.8", top_p: "0.95", max_tokens: "256",
    frequency_penalty: "0", presence_penalty: "0", additional_properties: "{}"
  });
  const invalid = adapter.validate({
    prompt: " ", model: "", temperature: "hot", top_p: "0.95", max_tokens: "2.5",
    frequency_penalty: "0", presence_penalty: "0", additional_properties: '{"model":"override"}'
  });
  expect(invalid.errors).toMatchObject({
    prompt: "Enter a prompt.", model: "Choose a prepared model preset.", temperature: "Enter a finite number.",
    max_tokens: "Enter a positive whole number.", additional_properties: "Additional properties cannot replace model, messages, or stream."
  });
});

test("Llama adapter sends one user message with forced streaming, merges allowed properties, and emits live deltas", async () => {
  const adapter = createLlamaAdapter();
  const values: FormValues = {
    ...adapter.initialValues(), prompt: " Explain this ", model: "gemma", additional_properties: '{"reasoning_format":"auto","seed":7}'
  };
  const progress: string[] = [];
  const execution = await adapter.execute(values, new AbortController().signal, (event) => {
    progress.push(event.kind === "phase" ? `${event.phase}:${event.status}` : `${event.kind}:${event.text}`);
  }, async (input, init) => {
    expect(input).toBe("/proxy/llama/v1/chat/completions");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(String(init?.body))).toEqual({
      temperature: 0.8, top_p: 0.95, max_tokens: 256, frequency_penalty: 0, presence_penalty: 0,
      reasoning_format: "auto", seed: 7, model: "gemma", messages: [{ role: "user", content: " Explain this " }], stream: true
    });
    return new Response('data: {"choices":[{"delta":{"reasoning_content":"think"}}]}\n\ndata: {"choices":[{"delta":{"content":"answer"},"finish_reason":"stop"}],"usage":{"completion_tokens":1}}\n\ndata: [DONE]\n\n', { headers: { "content-type": "text/event-stream" } });
  });
  expect(execution.result).toEqual({ kind: "llm", model: "gemma", thinking: "think", answer: "answer", finishReason: "stop", usage: { completion_tokens: 1 } });
  expect(progress).toEqual(["request:started", "thinking-delta:think", "answer-delta:answer", "request:completed"]);
  expect(execution.diagnostic).toContain('"stream": true');
  expect(execution.diagnostic).toContain("[DONE]");
});

test("Llama adapter maps reached, unavailable, wrong-media, and incomplete responses without a result", async () => {
  const adapter = createLlamaAdapter();
  const values = { ...adapter.initialValues(), prompt: "go", model: "gemma" };
  const run = (fetcher: typeof fetch) => adapter.execute(values, new AbortController().signal, () => {}, fetcher);
  await expect(run(async () => new Response('{"error":{"message":"overloaded"}}', { status: 503, headers: { "content-type": "application/json" } }))).rejects.toMatchObject({ category: "http", status: 503, serviceMessage: "overloaded" });
  await expect(run(async () => new Response('{"kind":"proxy-unavailable"}', { status: 502, headers: { "content-type": "application/json" } }))).rejects.toMatchObject({ category: "unavailable" });
  await expect(run(async () => new Response("not an event stream", { headers: { "content-type": "text/plain" } }))).rejects.toMatchObject({ category: "protocol" });
  await expect(run(async () => new Response('data: {"choices":[{"delta":{"content":"partial"}}]}\n\n', { headers: { "content-type": "text/event-stream" } }))).rejects.toMatchObject({ category: "protocol", message: "The service response ended before completion." });
});
