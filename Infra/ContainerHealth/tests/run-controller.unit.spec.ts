import { expect, test } from "@playwright/test";
import { DetailRunController, type ResourceOwner, type RunEntry } from "../src/run-controller";
import { createSimilarityAdapter, ServiceFailure, type AdapterExecution, type FormValues, type ServiceAdapter } from "../src/service-adapter";

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void; reject(error: unknown): void } {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  return { promise: new Promise<T>((yes, no) => { resolve = yes; reject = no; }), resolve, reject };
}

function controlledAdapter(run: ReturnType<typeof deferred<AdapterExecution>>): ServiceAdapter {
  const base = createSimilarityAdapter("minilm-l6");
  return { ...base, execute: (_values, signal) => {
    signal.addEventListener("abort", () => {}, { once: true });
    return run.promise;
  } };
}

const values: FormValues = { text1: "one", text2: "two" };

test("duplicate submit is rejected and user cancellation wins over late success", async () => {
  const run = deferred<AdapterExecution>();
  const entries: RunEntry[] = [];
  const controller = new DetailRunController(controlledAdapter(run), (snapshot) => {
    if (snapshot.history[0] !== undefined) entries.splice(0, entries.length, ...snapshot.history);
  });
  const pending = controller.submit(values);
  expect(controller.snapshot.status).toBe("running");
  await expect(controller.submit(values)).resolves.toBe("busy");
  expect(controller.cancel()).toBe(true);
  expect(controller.snapshot.status).toBe("cancelled");
  expect(entries[0]).toMatchObject({ outcome: "cancelled", message: "Cancelled by you. The service may continue processing after the connection closes." });
  run.resolve({ result: { kind: "similarity", score: 0.8 }, diagnostic: "late", warnings: [] });
  await pending;
  expect(controller.snapshot.history).toHaveLength(1);
  expect(controller.snapshot.history[0]?.outcome).toBe("cancelled");
});

test("success or failure settles once, late cancellation is rejected, and teardown abandons without history", async () => {
  const success = deferred<AdapterExecution>();
  const successful = new DetailRunController(controlledAdapter(success));
  const pendingSuccess = successful.submit(values);
  success.resolve({ result: { kind: "similarity", score: -0.2 }, diagnostic: "ok", warnings: [] });
  await pendingSuccess;
  expect(successful.snapshot.history[0]).toMatchObject({ outcome: "succeeded", result: { kind: "similarity", score: -0.2 } });
  expect(successful.cancel()).toBe(false);

  const abandoned = deferred<AdapterExecution>();
  const disposed = new DetailRunController(controlledAdapter(abandoned));
  const pendingAbandoned = disposed.submit(values);
  disposed.dispose();
  abandoned.reject(new DOMException("aborted", "AbortError"));
  await pendingAbandoned;
  expect(disposed.snapshot.history).toEqual([]);
});

test("failure wins over late cancellation and cancellation wins over late failure", async () => {
  const failed = deferred<AdapterExecution>();
  const failureFirst = new DetailRunController(controlledAdapter(failed));
  const pendingFailure = failureFirst.submit(values);
  failed.reject(new ServiceFailure({ category: "protocol", message: "Malformed response.", diagnostic: "bad" }));
  await pendingFailure;
  expect(failureFirst.snapshot.history[0]).toMatchObject({ outcome: "failed", failureCategory: "protocol" });
  expect(failureFirst.cancel()).toBe(false);

  const lateFailure = deferred<AdapterExecution>();
  const cancelFirst = new DetailRunController(controlledAdapter(lateFailure));
  const pendingCancel = cancelFirst.submit(values);
  expect(cancelFirst.cancel()).toBe(true);
  lateFailure.reject(new ServiceFailure({ category: "http", message: "late", diagnostic: "late" }));
  await pendingCancel;
  expect(cancelFirst.snapshot.history).toHaveLength(1);
  expect(cancelFirst.snapshot.history[0]?.outcome).toBe("cancelled");
});

test("history remains newest-first at five entries and resource ownership revokes exactly once", async () => {
  const revoked: string[] = [];
  let created = 0;
  let stopped = 0;
  const resources: ResourceOwner = { create: () => `blob:${++created}`, revoke: (url) => revoked.push(url), stopAll: () => { stopped += 1; } };
  const adapter = createSimilarityAdapter("minilm-l6");
  const immediate: ServiceAdapter = { ...adapter, execute: async () => ({ result: { kind: "audio", blob: new Blob(["wav"]), mediaType: "audio/wav", filename: "fixture.wav" }, diagnostic: "ok", warnings: [] }) };
  const controller = new DetailRunController(immediate, undefined, resources);
  for (let index = 0; index < 5; index += 1) await controller.submit(values);
  controller.setPlaying(1);
  await controller.submit(values);
  expect(controller.snapshot.history.map(({ id }) => id)).toEqual([6, 5, 4, 3, 1]);
  expect(revoked).toEqual(["blob:2"]);
  controller.dispose();
  expect(stopped).toBe(1);
  expect(revoked).toEqual(["blob:2", "blob:6", "blob:5", "blob:4", "blob:3", "blob:1"]);
  controller.dispose();
  expect(stopped).toBe(1);
});
