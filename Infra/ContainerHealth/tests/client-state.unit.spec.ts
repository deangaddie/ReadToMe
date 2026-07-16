import { expect, test } from "@playwright/test";
import { ReadinessController, ReadinessPolling, type PollingClock } from "../src/readiness-controller";
import { loadPreferences, savePreference, type PreferenceStorage } from "../src/preferences";
import { SERVICE_ADAPTERS, type ReadinessObservation } from "../src/readiness";

function observation(state: ReadinessObservation["state"], latencyMs = 4): ReadinessObservation {
  return { state, latencyMs, checkedAt: "2026-07-16T04:00:00.000Z", message: `${state} message`, diagnostic: state };
}

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>((done) => { resolve = done; }), resolve };
}

class ManualPollingClock implements PollingClock {
  delays: number[] = [];
  private nextId = 0;
  private callbacks = new Map<number, () => void>();
  setTimeout = (callback: () => void, delay: number): number => {
    const id = ++this.nextId;
    this.delays.push(delay);
    this.callbacks.set(id, callback);
    return id;
  };
  clearTimeout = (id: unknown): void => { this.callbacks.delete(id as number); };
  tick(): void {
    const callbacks = [...this.callbacks.values()];
    this.callbacks.clear();
    callbacks.forEach((callback) => callback());
  }
  get pending(): number { return this.callbacks.size; }
}

test("controllers start concurrently, publish independently, retain observations, and skip overlaps", async () => {
  const llama = deferred<ReadinessObservation>();
  const chatterbox = deferred<ReadinessObservation>();
  const retained = deferred<ReadinessObservation>();
  let llamaCalls = 0;
  const calls: string[] = [];
  const snapshots: Array<{ id: string; checking: boolean; state?: string }> = [];
  const controller = new ReadinessController(SERVICE_ADAPTERS.slice(0, 2), (adapter) => {
    calls.push(adapter.id);
    if (adapter.id !== "llama") return chatterbox.promise;
    llamaCalls += 1;
    return llamaCalls === 1 ? llama.promise : retained.promise;
  }, (snapshot) => snapshots.push({ id: snapshot.adapter.id, checking: snapshot.checking, state: snapshot.observation?.state }));

  const first = controller.refreshAll();
  expect(calls).toEqual(["llama", "chatterbox"]);
  expect(controller.snapshot("llama")).toMatchObject({ checking: true });
  expect(controller.snapshot("llama").observation).toBeUndefined();
  await expect(controller.refreshService("llama")).resolves.toBe("skipped");

  chatterbox.resolve(observation("Ready"));
  await Promise.resolve();
  expect(controller.snapshot("chatterbox")).toMatchObject({ checking: false, observation: { state: "Ready" } });
  expect(controller.snapshot("llama")).toMatchObject({ checking: true });

  llama.resolve(observation("Unavailable"));
  await first;
  const refreshing = controller.refreshService("llama");
  expect(controller.snapshot("llama")).toMatchObject({ checking: true, observation: { state: "Unavailable" } });
  retained.resolve(observation("Ready"));
  await refreshing;
  expect(snapshots.some(({ checking, state }) => checking && state === "Unavailable")).toBeTruthy();
});

test("invalidating controller epochs rejects late responses", async () => {
  const result = deferred<ReadinessObservation>();
  const controller = new ReadinessController(SERVICE_ADAPTERS.slice(0, 1), () => result.promise);
  const pending = controller.refreshService("llama");
  controller.invalidate();
  result.resolve(observation("Ready"));
  await expect(pending).resolves.toBe("stale");
  expect(controller.snapshot("llama").observation).toBeUndefined();
});

test("polling uses 2/10/30 second clocks, refreshes on changes, and pauses while hidden", () => {
  const clock = new ManualPollingClock();
  let refreshes = 0;
  const polling = new ReadinessPolling(() => { refreshes += 1; }, clock);

  polling.start(true);
  expect(refreshes).toBe(1);
  expect(clock.delays.at(-1)).toBe(10_000);
  clock.tick();
  expect(refreshes).toBe(2);

  polling.setIntervalSeconds(2);
  expect(refreshes).toBe(3);
  expect(clock.delays.at(-1)).toBe(2_000);
  polling.setVisible(false);
  expect(clock.pending).toBe(0);
  clock.tick();
  expect(refreshes).toBe(3);
  polling.setIntervalSeconds(30);
  expect(refreshes).toBe(3);
  polling.setVisible(true);
  expect(refreshes).toBe(4);
  expect(clock.delays.at(-1)).toBe(30_000);
});

test("preferences allow-list values and isolate corrupt or unavailable storage by key", () => {
  const removed: string[] = [];
  const corrupt: PreferenceStorage = {
    getItem(key) {
      if (key === "chd.refresh") return "999";
      throw new Error("theme read denied");
    },
    setItem() { throw new Error("write denied"); },
    removeItem(key) { removed.push(key); }
  };
  expect(loadPreferences(corrupt)).toEqual({ refreshSeconds: 10, theme: "system" });
  expect(removed).toEqual(["chd.refresh"]);
  expect(() => savePreference(corrupt, "refresh", "2")).not.toThrow();
});
