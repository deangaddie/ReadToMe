import { expect, test } from "@playwright/test";
import {
  READINESS_DIAGNOSTIC_LIMIT,
  SERVICE_ADAPTERS,
  type ReadinessClock,
  type ServiceId
} from "../src/readiness";

class FakeClock implements ReadinessClock {
  value = 1_000;
  deadline = 0;
  private callback: (() => void) | undefined;

  now = (): number => this.value;
  setTimeout = (callback: () => void, delay: number): number => {
    this.deadline = delay;
    this.callback = callback;
    return 1;
  };
  clearTimeout = (): void => { this.callback = undefined; };
  expire(): void { this.value += this.deadline; this.callback?.(); }
}

const payloads: Record<ServiceId, unknown> = {
  llama: { status: "ok" },
  audiocpp: { status: "ok", backend: "cuda", models: 6, ui: true, ui_management: false },
  whisper: { status: "ok" },
  "minilm-l6": { openapi: "3.1.0", info: { title: "MiniLM", version: "1.0.0" } },
  "mpnet-base-v2": { openapi: "3.1.0", info: { title: "MPNet", version: "1.0.0" } }
};

function jsonResponse(body: unknown, status = 200, contentType = "application/json"): Response {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), {
    status,
    headers: { "content-type": contentType }
  });
}

test("all five Service Adapters accept their exact ready payload", async () => {
  for (const adapter of SERVICE_ADAPTERS) {
    const observation = await adapter.checkReadiness(async () => jsonResponse(payloads[adapter.id]));
    expect(observation.state, adapter.id).toBe("Ready");
    expect(observation.checkedAt).toBeTruthy();
    expect(observation.latencyMs).toBeGreaterThanOrEqual(0);
  }
});

test("every Service Adapter rejects malformed or incomplete nominal success", async () => {
  const malformed: Record<ServiceId, unknown> = {
    llama: { status: "healthy" },
    audiocpp: { status: "ok", backend: "" },
    whisper: { status: true },
    "minilm-l6": { openapi: "", info: { title: "MiniLM", version: "1.0.0" } },
    "mpnet-base-v2": { openapi: "3.1.0", info: { title: "MPNet" } }
  };
  for (const adapter of SERVICE_ADAPTERS) {
    await expect(adapter.checkReadiness(async () => jsonResponse(malformed[adapter.id])), adapter.id).resolves.toMatchObject({ state: "Unknown" });
  }
});

test("readiness maps explicit loading, reached errors, malformed success, and wrong media", async () => {
  const whisper = SERVICE_ADAPTERS.find(({ id }) => id === "whisper")!;
  const llama = SERVICE_ADAPTERS.find(({ id }) => id === "llama")!;

  await expect(whisper.checkReadiness(async () => jsonResponse({ status: "loading" }, 503))).resolves.toMatchObject({ state: "Loading" });
  await expect(llama.checkReadiness(async () => jsonResponse({ detail: "reached" }, 418))).resolves.toMatchObject({ state: "Error" });
  await expect(llama.checkReadiness(async () => jsonResponse("{bad-json"))).resolves.toMatchObject({ state: "Unknown" });
  await expect(llama.checkReadiness(async () => jsonResponse({ status: "ok" }, 200, "text/plain"))).resolves.toMatchObject({ state: "Unknown" });
});

test("every Service Adapter applies shared transport and media failure mappings", async () => {
  for (const adapter of SERVICE_ADAPTERS) {
    await expect(adapter.checkReadiness(async () => jsonResponse(payloads[adapter.id], 200, "text/plain")), `${adapter.id} wrong media`).resolves.toMatchObject({ state: "Unknown" });
    await expect(adapter.checkReadiness(async () => jsonResponse("{bad-json")), `${adapter.id} malformed JSON`).resolves.toMatchObject({ state: "Unknown" });
    await expect(adapter.checkReadiness(async () => jsonResponse({ detail: "reached" }, 500)), `${adapter.id} reached error`).resolves.toMatchObject({ state: "Error" });
    await expect(adapter.checkReadiness(async () => jsonResponse({ kind: "proxy-unavailable", service: adapter.id }, 502)), `${adapter.id} proxy unavailable`).resolves.toMatchObject({ state: "Unavailable" });
  }
});

test("network, normalized proxy failure, and the exact 1.5 second deadline are unavailable", async () => {
  const adapter = SERVICE_ADAPTERS[0]!;
  await expect(adapter.checkReadiness(async () => { throw new TypeError("fetch failed"); })).resolves.toMatchObject({ state: "Unavailable" });
  await expect(adapter.checkReadiness(async () => jsonResponse({ kind: "proxy-unavailable", service: "llama" }, 502))).resolves.toMatchObject({ state: "Unavailable" });

  const clock = new FakeClock();
  const pending = adapter.checkReadiness((_input, init) => new Promise((_resolve, reject) => {
    init?.signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
  }), { clock });
  expect(clock.deadline).toBe(1_500);
  clock.expire();
  await expect(pending).resolves.toMatchObject({ state: "Unavailable", latencyMs: 1_500 });
});

test("diagnostics are bounded and explicitly marked when truncated", async () => {
  const adapter = SERVICE_ADAPTERS[0]!;
  const observation = await adapter.checkReadiness(async () => jsonResponse({ detail: "x".repeat(READINESS_DIAGNOSTIC_LIMIT * 2) }, 500));
  expect(observation.diagnostic.length).toBeLessThanOrEqual(READINESS_DIAGNOSTIC_LIMIT + 32);
  expect(observation.diagnostic).toContain("[truncated]");
});

test("audio.cpp readiness names the loaded model without letting the model list change the state", async () => {
  const audiocpp = SERVICE_ADAPTERS.find(({ id }) => id === "audiocpp")!;
  const health = payloads.audiocpp;
  const models = (loaded: string | undefined) => ({ object: "list", data: ["breeze-q8", "voxcpm2"].map((id) => ({ id, loaded: id === loaded })) });
  const probe = (list: () => Response) => audiocpp.checkReadiness(async (input) => String(input) === "/proxy/audiocpp/health" ? jsonResponse(health) : list());

  const loaded = await probe(() => jsonResponse(models("voxcpm2")));
  expect(loaded).toMatchObject({ state: "Ready", message: "Validated response received. Loaded model: voxcpm2." });
  expect(loaded.diagnostic).toContain("/v1/models:");
  await expect(probe(() => jsonResponse(models(undefined)))).resolves.toMatchObject({ state: "Ready", message: "Validated response received. No model loaded." });
  await expect(probe(() => jsonResponse({ detail: "x" }, 500))).resolves.toMatchObject({ state: "Ready", message: "Validated response received. Loaded model unknown." });
  await expect(probe(() => { throw new TypeError("fetch failed"); })).resolves.toMatchObject({ state: "Ready", message: "Validated response received. Loaded model unknown." });

  const requested: string[] = [];
  await audiocpp.checkReadiness(async (input) => { requested.push(String(input)); return jsonResponse({ detail: "down" }, 500); });
  expect(requested).toEqual(["/proxy/audiocpp/health"]);
});
