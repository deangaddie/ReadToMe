import { expect, test } from "@playwright/test";
import { createSimilarityAdapter, type FormValues } from "../src/service-adapter";

const minilm = createSimilarityAdapter("minilm-l6");

test("similarity fields preserve strings, validate nonblank text, and bound summaries", () => {
  expect(minilm.fields.map(({ key, label, control, required }) => ({ key, label, control, required }))).toEqual([
    { key: "text1", label: "First text", control: "textarea", required: true },
    { key: "text2", label: "Second text", control: "textarea", required: true }
  ]);
  expect(minilm.validate({ text1: "  ", text2: "second" }).errors).toEqual({ text1: "Enter the first text.", text2: undefined });
  const values: FormValues = { text1: "x".repeat(5_000), text2: "  kept unchanged  " };
  const summary = minilm.summarizeInput(values);
  expect(summary[0]?.value).toHaveLength(4_096 + "\n[truncated]".length);
  expect(summary[0]?.value.endsWith("\n[truncated]")).toBeTruthy();
  expect(summary[1]?.value).toBe("  kept unchanged  ");
  expect(Object.isFrozen(summary)).toBeTruthy();
});

test("similarity execution sends exact JSON, preserves negative scores, and emits request progress", async () => {
  const progress: string[] = [];
  const fetcher: typeof fetch = async (input, init) => {
    expect(input).toBe("/proxy/minilm-l6/similarity");
    expect(init?.method).toBe("POST");
    expect(init?.body).toBe(JSON.stringify({ text1: " first ", text2: "second" }));
    expect(init?.signal).toBeInstanceOf(AbortSignal);
    return new Response('{"similarity":-0.1256789}', { status: 200, headers: { "content-type": "application/json" } });
  };
  const result = await minilm.execute(
    { text1: " first ", text2: "second" },
    new AbortController().signal,
    (event) => { if (event.kind === "phase") progress.push(`${event.phase}:${event.status}`); },
    fetcher
  );
  expect(result.result).toEqual({ kind: "similarity", score: -0.1256789 });
  expect(result.warnings).toEqual([]);
  expect(progress).toEqual(["request:started", "request:completed"]);
});

test("similarity execution keeps finite out-of-range scores but rejects HTTP and malformed successes", async () => {
  const outside = await minilm.execute(
    { text1: "a", text2: "b" }, new AbortController().signal, () => {},
    async () => new Response('{"similarity":1.25}', { headers: { "content-type": "application/json" } })
  );
  expect(outside.result).toEqual({ kind: "similarity", score: 1.25 });
  expect(outside.warnings).toEqual(["This raw cosine value is outside the theoretical -1 to 1 range."]);

  await expect(minilm.execute(
    { text1: "a", text2: "b" }, new AbortController().signal, () => {},
    async () => new Response('{"detail":"bad input"}', { status: 422, headers: { "content-type": "application/json" } })
  )).rejects.toMatchObject({ category: "http", status: 422, serviceMessage: "bad input" });

  for (const body of ['{"similarity":"1"}', '{"similarity":null}', '{"similarity":1e999}', "{bad"]) {
    await expect(minilm.execute(
      { text1: "a", text2: "b" }, new AbortController().signal, () => {},
      async () => new Response(body, { headers: { "content-type": "application/json" } })
    )).rejects.toMatchObject({ category: "protocol" });
  }
});

test("similarity execution propagates cancellation to fetch and never maps AbortError to failure", async () => {
  const controller = new AbortController();
  let observed = false;
  const pending = minilm.execute({ text1: "a", text2: "b" }, controller.signal, () => {}, async (_input, init) => {
    return await new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => { observed = true; reject(new DOMException("aborted", "AbortError")); }, { once: true });
    });
  });
  controller.abort();
  await expect(pending).rejects.toMatchObject({ name: "AbortError" });
  expect(observed).toBeTruthy();
});

test("functional diagnostics stop at 64 KiB and carry an explicit truncation marker", async () => {
  await expect(minilm.execute(
    { text1: "a", text2: "b" }, new AbortController().signal, () => {},
    async () => new Response(`{"similarity":"${"x".repeat(70 * 1_024)}"}`, { headers: { "content-type": "application/json" } })
  )).rejects.toMatchObject({ category: "protocol", diagnostic: expect.stringMatching(/\[truncated\]$/u) });
});
