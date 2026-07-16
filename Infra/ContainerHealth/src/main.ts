import { loadPreferences, savePreference, type ThemePreference } from "./preferences";
import { ReadinessController, ReadinessPolling, type ControllerSnapshot, type RefreshSeconds } from "./readiness-controller";
import { SERVICE_ADAPTERS, type ReadinessObservation, type ReadinessState, type ServiceAdapter as ReadinessAdapter, type ServiceId } from "./readiness";
import { FUNCTIONAL_ADAPTERS, type FieldDefinition, type FormValues, type ServiceAdapter as FunctionalAdapter, type ServiceResult } from "./service-adapter";
import { DetailRunController, type RunEntry, type RunSnapshot } from "./run-controller";
import { ThemeController } from "./theme-controller";

const app = document.querySelector<HTMLDivElement>("#app");
if (app === null) throw new Error("Dashboard application root is missing.");

const preferences = loadPreferences(localStorage);
const theme = new ThemeController(preferences.theme, localStorage);
const DASHBOARD_ADAPTERS: readonly ReadinessAdapter[] = SERVICE_ADAPTERS.map((adapter) => FUNCTIONAL_ADAPTERS.find(({ id }) => id === adapter.id) ?? adapter);

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character] ?? character);
}

function element<T extends Element>(selector: string, root: ParentNode = document): T {
  const found = root.querySelector<T>(selector);
  if (found === null) throw new Error(`Expected dashboard element: ${selector}`);
  return found;
}

function serviceHref(id: ServiceId): string {
  return `/detail.html?service=${encodeURIComponent(id)}`;
}

function railMarkup(current?: ServiceId): string {
  return `<nav class="service-rail" aria-labelledby="services-heading">
    <div class="rail-heading"><p class="eyebrow">Read2Me infrastructure</p><h2 id="services-heading">Services</h2></div>
    <ul>${DASHBOARD_ADAPTERS.map((service) => `<li>
      <a data-service-link="${service.id}" href="${serviceHref(service.id)}"${current === service.id ? ' aria-current="page"' : ""}>
        <span class="rail-service-name">${escapeHtml(service.shortName)}</span>
        <span class="rail-state" data-rail-state="${service.id}"><span aria-hidden="true">?</span> Unknown</span>
      </a>
    </li>`).join("")}</ul>
  </nav>`;
}

function controlsMarkup(): string {
  return `<div class="dashboard-controls" aria-label="Dashboard preferences">
    <button class="secondary-button" type="button" data-refresh>Refresh readiness now</button>
    <label>Refresh interval
      <select data-refresh-interval>
        <option value="2">2 seconds</option>
        <option value="10">10 seconds</option>
        <option value="30">30 seconds</option>
      </select>
    </label>
    <label>Theme
      <select data-theme-select>
        <option value="system">System</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
    </label>
  </div>`;
}

function stateIcon(state: ReadinessState): string {
  return ({ Ready: "✓", Loading: "↻", Unavailable: "○", Error: "!", Unknown: "?" })[state];
}

function cardMarkup(adapter: ReadinessAdapter): string {
  return `<article class="service-card" data-service-card="${adapter.id}" aria-labelledby="card-${adapter.id}">
    <div class="card-heading"><div><p class="compute">${adapter.compute} · port ${adapter.port}</p><h3 id="card-${adapter.id}">${escapeHtml(adapter.name)}</h3></div>
      <span class="state-badge" data-state="Unknown"><span aria-hidden="true">?</span> <span data-state-text>Unknown</span></span>
    </div>
    <p class="purpose">${escapeHtml(adapter.purpose)}</p>
    <dl class="card-metadata">
      <div><dt>Endpoint</dt><dd><code>${escapeHtml(adapter.endpoint)}</code></dd></div>
      <div><dt>Compute</dt><dd>${adapter.compute}</dd></div>
      <div><dt>Latency</dt><dd data-latency>—</dd></div>
      <div><dt>Last check</dt><dd data-checked>Not checked</dd></div>
    </dl>
    <div class="observation-row"><span class="checking" data-checking hidden>Checking…</span><p data-message>No observation yet.</p></div>
    <a class="detail-link" href="${serviceHref(adapter.id)}">Open ${escapeHtml(adapter.name)} details <span aria-hidden="true">→</span></a>
  </article>`;
}

function overviewMarkup(): string {
  return `<div class="dashboard-shell">
    ${railMarkup()}
    <main class="workbench" id="main-content">
      ${controlsMarkup()}
      <header class="page-heading"><p class="eyebrow">Observed through the local proxy</p><h1>Service readiness</h1><p>Each service is checked independently. Unavailable services may simply not be needed right now.</p></header>
      <section class="readiness-group attention-group" aria-labelledby="attention-heading"><div class="section-heading"><h2 id="attention-heading">Attention</h2><p>Unexpected or unvalidated responses</p></div><div class="card-grid" data-group="attention">${DASHBOARD_ADAPTERS.map(cardMarkup).join("")}</div><p class="empty-group" data-empty="attention" hidden>No services need attention.</p></section>
      <section class="readiness-group" aria-labelledby="available-heading"><div class="section-heading"><h2 id="available-heading">Available now</h2><p>Validated readiness responses</p></div><div class="card-grid" data-group="available"></div><p class="empty-group" data-empty="available">No services currently report ready.</p></section>
      <section class="readiness-group" aria-labelledby="inactive-heading"><div class="section-heading"><h2 id="inactive-heading">Not available now</h2><p>Neutral loading or unreachable observations</p></div><div class="card-grid" data-group="inactive"></div><p class="empty-group" data-empty="inactive">No loading or unavailable services.</p></section>
    </main>
    <aside class="activity-rail" aria-labelledby="activity-heading"><p class="eyebrow">Latest changes</p><h2 id="activity-heading">Recent readiness activity</h2><ol data-activity><li class="empty-activity">Waiting for observations.</li></ol></aside>
    <p class="sr-only" aria-live="polite" aria-atomic="true" data-announcement></p>
  </div>`;
}

function fieldMarkup(field: FieldDefinition): string {
  const describedBy = `help-${field.key} error-${field.key}`;
  const required = field.required ? " required" : "";
  const value = typeof field.initialValue === "string" ? field.initialValue : "";
  const constraints = `${field.min === undefined ? "" : ` min="${escapeHtml(field.min)}"`}${field.max === undefined ? "" : ` max="${escapeHtml(field.max)}"`}${field.step === undefined ? "" : ` step="${escapeHtml(field.step)}"`}`;
  let control: string;
  switch (field.control) {
    case "textarea":
      control = `<textarea id="field-${field.key}" name="${field.key}" rows="5" aria-describedby="${describedBy}"${required} placeholder="${escapeHtml(field.example ?? "")}">${escapeHtml(value)}</textarea>`;
      break;
    case "select":
      control = `<select id="field-${field.key}" name="${field.key}" aria-describedby="${describedBy}"${required}>${(field.options ?? []).map((option) => `<option value="${escapeHtml(option.value)}"${option.value === value ? " selected" : ""}>${escapeHtml(option.label)}</option>`).join("")}</select>`;
      break;
    case "checkbox":
      control = `<input id="field-${field.key}" name="${field.key}" type="checkbox" aria-describedby="${describedBy}"${field.initialValue === true ? " checked" : ""}${required}>`;
      break;
    case "file":
      control = `<input id="field-${field.key}" name="${field.key}" type="file" aria-describedby="${describedBy}"${field.accept === undefined ? "" : ` accept="${escapeHtml(field.accept)}"`}${required}>`;
      break;
    case "number":
    case "text":
      control = `<input id="field-${field.key}" name="${field.key}" type="${field.control}" aria-describedby="${describedBy}" value="${escapeHtml(value)}"${constraints}${required} placeholder="${escapeHtml(field.example ?? "")}">`;
      break;
  }
  return `<div class="form-field" data-field="${field.key}"><label for="field-${field.key}">${escapeHtml(field.label)}${field.required ? ' <span aria-hidden="true">*</span>' : ""}</label>${control}
    <p class="field-help" id="help-${field.key}">${escapeHtml(field.help ?? (field.required ? "Required." : "Optional."))}</p>
    <p class="field-error" id="error-${field.key}" data-field-error="${field.key}"></p></div>`;
}

function workbenchMarkup(adapter: FunctionalAdapter): string {
  const common = adapter.fields.filter(({ group }) => group === "common").map(fieldMarkup).join("");
  const advanced = adapter.fields.filter(({ group }) => group === "advanced").map(fieldMarkup).join("");
  return `<section class="functional-workbench" aria-labelledby="tests-heading"><div class="section-heading"><h2 id="tests-heading">Functional test</h2><p>Inputs and results remain in this page only.</p></div>
    <form data-run-form novalidate><fieldset><legend>Common fields</legend>${common}</fieldset>
      <details class="advanced-fields"><summary>Advanced</summary>${advanced || "<p>No advanced fields for this service.</p>"}</details>
      <div class="run-actions"><button class="primary-button" type="submit" data-run-action>Run similarity test</button><p data-run-progress aria-live="polite">Ready to run.</p></div>
    </form>
    <section class="run-history" aria-labelledby="history-heading"><div class="section-heading"><h3 id="history-heading">Run history</h3><p>Newest first · up to five entries</p></div><div data-run-history><p class="empty-history">No runs yet.</p></div></section>
  </section>`;
}

function detailMarkup(adapter: ReadinessAdapter | undefined, functional: FunctionalAdapter | undefined): string {
  const content = adapter === undefined
    ? `<header class="page-heading"><p class="eyebrow">Readiness detail</p><h1>Service not found</h1><p>The requested service is missing or invalid. Choose a service from the inventory.</p><a class="primary-link" href="/">Return to readiness overview</a></header>`
    : `<nav class="breadcrumb" aria-label="Breadcrumb"><a href="/">Service readiness</a><span aria-hidden="true">/</span><span>${escapeHtml(adapter.name)}</span></nav>
      <header class="page-heading"><p class="eyebrow">${adapter.compute} service · port ${adapter.port}</p><h1>${escapeHtml(adapter.name)}</h1><p>${escapeHtml(adapter.purpose)}</p></header>
      <section class="detail-readiness" aria-labelledby="detail-readiness-heading"><div><p class="eyebrow">Latest observation</p><h2 id="detail-readiness-heading">Readiness</h2></div><span class="state-badge" data-detail-state="Unknown"><span aria-hidden="true">?</span> <span data-detail-state-text>Unknown</span></span><span class="checking" data-detail-checking hidden>Checking…</span><p data-detail-message>No observation yet.</p><dl><div><dt>Endpoint</dt><dd><code>${escapeHtml(adapter.endpoint)}</code></dd></div><div><dt>Latency</dt><dd data-detail-latency>—</dd></div><div><dt>Last check</dt><dd data-detail-checked>Not checked</dd></div></dl><details><summary>Latest raw diagnostic</summary><pre data-detail-diagnostic>No diagnostic yet.</pre></details></section>
      ${functional === undefined ? `<section class="coming-next" aria-labelledby="tests-heading"><h2 id="tests-heading">Functional test</h2><p>This service adapter arrives in a later implementation slice.</p></section>` : workbenchMarkup(functional)}`;
  return `<div class="dashboard-shell detail-shell">${railMarkup(adapter?.id)}<main class="workbench" id="main-content">${controlsMarkup()}${content}</main><p class="sr-only" aria-live="polite" aria-atomic="true" data-announcement></p></div>`;
}

const detailId = location.pathname.endsWith("detail.html") ? new URLSearchParams(location.search).get("service") : null;
const selectedAdapter = detailId === null ? undefined : DASHBOARD_ADAPTERS.find(({ id }) => id === detailId);
const functionalAdapter = detailId === null ? undefined : FUNCTIONAL_ADAPTERS.find(({ id }) => id === detailId);
const isDetail = location.pathname.endsWith("detail.html");
app.innerHTML = isDetail ? detailMarkup(selectedAdapter, functionalAdapter) : overviewMarkup();

const refreshSelect = element<HTMLSelectElement>("[data-refresh-interval]");
const themeSelect = element<HTMLSelectElement>("[data-theme-select]");
refreshSelect.value = String(preferences.refreshSeconds);
themeSelect.value = theme.preference;

const activity: Array<{ adapter: ReadinessAdapter; observation: ReadinessObservation }> = [];
const announcedStates = new Map<ServiceId, ReadinessState>();

interface ObservationViewSelectors {
  readonly badge: string;
  readonly stateText: string;
  readonly checking: string;
  readonly message: string;
  readonly latency: string;
  readonly checked: string;
}

function updateObservationView(snapshot: ControllerSnapshot, selectors: ObservationViewSelectors, root: ParentNode = document): ReadinessObservation | undefined {
  const observation = snapshot.observation;
  const state = observation?.state ?? "Unknown";
  const badge = element<HTMLElement>(selectors.badge, root);
  badge.dataset.state = state;
  badge.firstElementChild!.textContent = stateIcon(state);
  element<HTMLElement>(selectors.stateText, root).textContent = state;
  element<HTMLElement>(selectors.checking, root).hidden = !snapshot.checking;
  if (observation !== undefined) {
    element<HTMLElement>(selectors.message, root).textContent = observation.message;
    element<HTMLElement>(selectors.latency, root).textContent = `${observation.latencyMs} ms`;
    element<HTMLElement>(selectors.checked, root).textContent = new Date(observation.checkedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  }
  return observation;
}

function groupFor(state: ReadinessState): "attention" | "available" | "inactive" {
  if (state === "Ready") return "available";
  if (state === "Loading" || state === "Unavailable") return "inactive";
  return "attention";
}

function updateGroupEmptyStates(): void {
  for (const name of ["attention", "available", "inactive"] as const) {
    const group = element<HTMLElement>(`[data-group="${name}"]`);
    element<HTMLElement>(`[data-empty="${name}"]`).hidden = group.childElementCount > 0;
  }
}

function updateRail(snapshot: ControllerSnapshot): void {
  const state = snapshot.observation?.state ?? "Unknown";
  const target = element<HTMLElement>(`[data-rail-state="${snapshot.adapter.id}"]`);
  target.dataset.state = state;
  target.innerHTML = `<span aria-hidden="true">${stateIcon(state)}</span> ${state}${snapshot.checking ? ' <span class="sr-only">Checking</span>' : ""}`;
}

function updateOverview(snapshot: ControllerSnapshot, previous?: ReadinessObservation): void {
  const card = element<HTMLElement>(`[data-service-card="${snapshot.adapter.id}"]`);
  const observation = updateObservationView(snapshot, {
    badge: "[data-state]", stateText: "[data-state-text]", checking: "[data-checking]",
    message: "[data-message]", latency: "[data-latency]", checked: "[data-checked]"
  }, card);
  const state = observation?.state ?? "Unknown";
  if (observation !== undefined) {
    element<HTMLElement>(`[data-group="${groupFor(state)}"]`).append(card);

    if (previous === undefined || previous.state !== state) {
      activity.unshift({ adapter: snapshot.adapter, observation });
      activity.splice(8);
      const list = element<HTMLOListElement>("[data-activity]");
      list.innerHTML = activity.map((item) => `<li><span class="activity-icon" data-state="${item.observation.state}" aria-hidden="true">${stateIcon(item.observation.state)}</span><div><strong>${escapeHtml(item.adapter.shortName)}</strong><span>${item.observation.state} · ${item.observation.latencyMs} ms</span></div></li>`).join("");
    }
    if (announcedStates.get(snapshot.adapter.id) !== state) {
      announcedStates.set(snapshot.adapter.id, state);
      element<HTMLElement>("[data-announcement]").textContent = `${snapshot.adapter.name}: ${state}. ${observation.message}`;
    }
  }
  updateGroupEmptyStates();
}

function updateDetail(snapshot: ControllerSnapshot): void {
  if (selectedAdapter === undefined || snapshot.adapter.id !== selectedAdapter.id) return;
  const observation = updateObservationView(snapshot, {
    badge: "[data-detail-state]", stateText: "[data-detail-state-text]", checking: "[data-detail-checking]",
    message: "[data-detail-message]", latency: "[data-detail-latency]", checked: "[data-detail-checked]"
  });
  if (observation !== undefined) {
    element<HTMLElement>("[data-detail-diagnostic]").textContent = observation.diagnostic || "No response body.";
  }
}

function formatScore(score: number): string {
  return score.toFixed(6).replace(/\.0+$/u, "").replace(/(\.\d*?)0+$/u, "$1");
}

function resultMarkup(result: ServiceResult | undefined, resultUrl?: string): string {
  if (result === undefined) return "";
  switch (result.kind) {
    case "similarity": return `<div class="similarity-result"><span>Raw cosine similarity</span><strong data-testid="similarity-score">${formatScore(result.score)}</strong></div>`;
    case "llm": return `<section><h4>Thinking</h4><pre>${escapeHtml(result.thinking)}</pre><h4>Answer</h4><p>${escapeHtml(result.answer)}</p></section>`;
    case "audio": return `<audio controls src="${escapeHtml(resultUrl ?? "")}"></audio><a href="${escapeHtml(resultUrl ?? "")}" download="${escapeHtml(result.filename)}">Download WAV</a>`;
    case "transcription": return `<pre>${escapeHtml(result.text)}</pre>`;
  }
}

function historyMarkup(entry: RunEntry): string {
  const warnings = entry.warnings.map((warning) => `<li>${escapeHtml(warning)}</li>`).join("");
  const input = entry.input.map((item) => `<div><dt>${escapeHtml(item.label)}</dt><dd>${escapeHtml(item.value)}</dd></div>`).join("");
  return `<article class="run-entry" data-run-entry data-outcome="${entry.outcome}"><div class="run-entry-heading"><div><p class="eyebrow">${escapeHtml(entry.outcome)}</p><h4>${escapeHtml(entry.title)}</h4></div><span>${(entry.elapsedMs / 1_000).toFixed(1)} s</span></div>
    <p>${escapeHtml(entry.message)}</p>${resultMarkup(entry.result, entry.resultUrl)}${warnings ? `<ul class="run-warnings">${warnings}</ul>` : ""}
    <details><summary>Input summary</summary><dl class="input-summary">${input}</dl></details>
    <details><summary>Raw diagnostic</summary><pre>${escapeHtml(entry.diagnostic || "No response body.")}</pre></details></article>`;
}

let runController: DetailRunController | undefined;
let elapsedTimer: ReturnType<typeof setInterval> | undefined;
let runReadinessState: ReadinessState | undefined;

function renderRun(snapshot: RunSnapshot): void {
  if (functionalAdapter === undefined) return;
  for (const field of functionalAdapter.fields) {
    const error = snapshot.validationErrors[field.key];
    const target = element<HTMLElement>(`[data-field-error="${field.key}"]`);
    target.textContent = error ?? "";
    const input = element<HTMLElement>(`#field-${field.key}`);
    if (error === undefined) input.removeAttribute("aria-invalid"); else input.setAttribute("aria-invalid", "true");
  }
  const action = element<HTMLButtonElement>("[data-run-action]");
  const status = element<HTMLElement>("[data-run-progress]");
  if (snapshot.status === "running") {
    action.type = "button";
    action.textContent = "Cancel run";
    const latestReadiness = functionalAdapter === undefined ? undefined : controller.snapshot(functionalAdapter.id).observation?.state;
    const readinessChanged = runReadinessState !== undefined && latestReadiness !== undefined && latestReadiness !== runReadinessState;
    const progressMessage = snapshot.progress === undefined ? ""
      : snapshot.progress.kind === "phase" ? snapshot.progress.message
      : snapshot.progress.kind === "thinking-delta" ? "Receiving model thinking."
      : "Receiving model answer.";
    status.textContent = readinessChanged
      ? "Latest readiness changed; the active test is still running."
      : `Running · Elapsed ${Math.max(0, (Date.now() - (snapshot.startedAtMs ?? Date.now())) / 1_000).toFixed(1)} s${progressMessage ? ` · ${progressMessage}` : ""}`;
    if (elapsedTimer === undefined) elapsedTimer = setInterval(() => renderRun(runController!.snapshot), 250);
  } else {
    action.type = "submit";
    action.textContent = "Run similarity test";
    if (elapsedTimer !== undefined) { clearInterval(elapsedTimer); elapsedTimer = undefined; }
    status.textContent = snapshot.status === "idle" ? "Ready to run." : `${snapshot.status[0]?.toUpperCase()}${snapshot.status.slice(1)}.`;
  }
  const history = element<HTMLElement>("[data-run-history]");
  history.innerHTML = snapshot.history.length === 0 ? '<p class="empty-history">No runs yet.</p>' : snapshot.history.map(historyMarkup).join("");
  element<HTMLElement>("[data-announcement]").textContent = snapshot.status === "running" ? "Run started." : snapshot.history[0]?.message ?? "";
}

const controller = new ReadinessController(DASHBOARD_ADAPTERS, (adapter) => adapter.checkReadiness(), (snapshot, previous) => {
  updateRail(snapshot);
  if (isDetail) updateDetail(snapshot);
  else updateOverview(snapshot, previous);
});
const polling = new ReadinessPolling(() => { void controller.refreshAll(); });
polling.setIntervalSeconds(preferences.refreshSeconds);
polling.start(document.visibilityState === "visible");

if (functionalAdapter !== undefined) {
  runController = new DetailRunController(functionalAdapter, renderRun);
  renderRun(runController.snapshot);
  const form = element<HTMLFormElement>("[data-run-form]");
  const action = element<HTMLButtonElement>("[data-run-action]");
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    if (runController?.snapshot.status === "running") return;
    const values: Record<string, string | boolean | File | null> = {};
    for (const field of functionalAdapter.fields) {
      const control = element<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(`#field-${field.key}`);
      values[field.key] = control instanceof HTMLInputElement && control.type === "checkbox" ? control.checked
        : control instanceof HTMLInputElement && control.type === "file" ? control.files?.[0] ?? null
        : control.value;
    }
    runReadinessState = controller.snapshot(functionalAdapter.id).observation?.state;
    void runController?.submit(Object.freeze(values) as FormValues);
    queueMicrotask(() => {
      if (runController?.snapshot.status === "running") element<HTMLButtonElement>("[data-run-action]").focus();
    });
  });
  action.addEventListener("click", (event) => {
    if (runController?.snapshot.status !== "running") return;
    event.preventDefault();
    runController.cancel();
    element<HTMLButtonElement>("[data-run-action]").focus();
  });
}

element<HTMLButtonElement>("[data-refresh]").addEventListener("click", () => polling.refreshNow());
refreshSelect.addEventListener("change", () => {
  const value = Number(refreshSelect.value) as RefreshSeconds;
  if (![2, 10, 30].includes(value)) return;
  savePreference(localStorage, "refresh", String(value));
  polling.setIntervalSeconds(value);
});
themeSelect.addEventListener("change", () => {
  const value = themeSelect.value as ThemePreference;
  if (!["system", "light", "dark"].includes(value)) return;
  theme.setPreference(value);
});

const onVisibility = (): void => polling.setVisible(document.visibilityState === "visible");
document.addEventListener("visibilitychange", onVisibility);
addEventListener("pagehide", () => {
  if (elapsedTimer !== undefined) clearInterval(elapsedTimer);
  runController?.dispose();
  polling.stop();
  controller.invalidate();
  document.removeEventListener("visibilitychange", onVisibility);
  theme.dispose();
}, { once: true });

document.documentElement.dataset.dashboard = "ready";
