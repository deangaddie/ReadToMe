import { loadPreferences, savePreference, type ThemePreference } from "./preferences";
import { ReadinessController, ReadinessPolling, type ControllerSnapshot, type RefreshSeconds } from "./readiness-controller";
import { SERVICE_ADAPTERS, type ReadinessObservation, type ReadinessState, type ServiceAdapter, type ServiceId } from "./readiness";
import { ThemeController } from "./theme-controller";

const app = document.querySelector<HTMLDivElement>("#app");
if (app === null) throw new Error("Dashboard application root is missing.");

const preferences = loadPreferences(localStorage);
const theme = new ThemeController(preferences.theme, localStorage);

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
    <ul>${SERVICE_ADAPTERS.map((service) => `<li>
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

function cardMarkup(adapter: ServiceAdapter): string {
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
      <section class="readiness-group attention-group" aria-labelledby="attention-heading"><div class="section-heading"><h2 id="attention-heading">Attention</h2><p>Unexpected or unvalidated responses</p></div><div class="card-grid" data-group="attention">${SERVICE_ADAPTERS.map(cardMarkup).join("")}</div><p class="empty-group" data-empty="attention" hidden>No services need attention.</p></section>
      <section class="readiness-group" aria-labelledby="available-heading"><div class="section-heading"><h2 id="available-heading">Available now</h2><p>Validated readiness responses</p></div><div class="card-grid" data-group="available"></div><p class="empty-group" data-empty="available">No services currently report ready.</p></section>
      <section class="readiness-group" aria-labelledby="inactive-heading"><div class="section-heading"><h2 id="inactive-heading">Not available now</h2><p>Neutral loading or unreachable observations</p></div><div class="card-grid" data-group="inactive"></div><p class="empty-group" data-empty="inactive">No loading or unavailable services.</p></section>
    </main>
    <aside class="activity-rail" aria-labelledby="activity-heading"><p class="eyebrow">Latest changes</p><h2 id="activity-heading">Recent readiness activity</h2><ol data-activity><li class="empty-activity">Waiting for observations.</li></ol></aside>
    <p class="sr-only" aria-live="polite" aria-atomic="true" data-announcement></p>
  </div>`;
}

function detailMarkup(adapter: ServiceAdapter | undefined): string {
  const content = adapter === undefined
    ? `<header class="page-heading"><p class="eyebrow">Readiness detail</p><h1>Service not found</h1><p>The requested service is missing or invalid. Choose a service from the inventory.</p><a class="primary-link" href="/">Return to readiness overview</a></header>`
    : `<nav class="breadcrumb" aria-label="Breadcrumb"><a href="/">Service readiness</a><span aria-hidden="true">/</span><span>${escapeHtml(adapter.name)}</span></nav>
      <header class="page-heading"><p class="eyebrow">${adapter.compute} service · port ${adapter.port}</p><h1>${escapeHtml(adapter.name)}</h1><p>${escapeHtml(adapter.purpose)}</p></header>
      <section class="detail-readiness" aria-labelledby="detail-readiness-heading"><div><p class="eyebrow">Latest observation</p><h2 id="detail-readiness-heading">Readiness</h2></div><span class="state-badge" data-detail-state="Unknown"><span aria-hidden="true">?</span> <span data-detail-state-text>Unknown</span></span><span class="checking" data-detail-checking hidden>Checking…</span><p data-detail-message>No observation yet.</p><dl><div><dt>Endpoint</dt><dd><code>${escapeHtml(adapter.endpoint)}</code></dd></div><div><dt>Latency</dt><dd data-detail-latency>—</dd></div><div><dt>Last check</dt><dd data-detail-checked>Not checked</dd></div></dl><details><summary>Latest raw diagnostic</summary><pre data-detail-diagnostic>No diagnostic yet.</pre></details></section>
      <section class="coming-next" aria-labelledby="tests-heading"><h2 id="tests-heading">Functional test</h2><p>Functional tests arrive in the next implementation slice.</p></section>`;
  return `<div class="dashboard-shell detail-shell">${railMarkup(adapter?.id)}<main class="workbench" id="main-content">${controlsMarkup()}${content}</main><p class="sr-only" aria-live="polite" aria-atomic="true" data-announcement></p></div>`;
}

const detailId = location.pathname.endsWith("detail.html") ? new URLSearchParams(location.search).get("service") : null;
const selectedAdapter = detailId === null ? undefined : SERVICE_ADAPTERS.find(({ id }) => id === detailId);
const isDetail = location.pathname.endsWith("detail.html");
app.innerHTML = isDetail ? detailMarkup(selectedAdapter) : overviewMarkup();

const refreshSelect = element<HTMLSelectElement>("[data-refresh-interval]");
const themeSelect = element<HTMLSelectElement>("[data-theme-select]");
refreshSelect.value = String(preferences.refreshSeconds);
themeSelect.value = theme.preference;

const activity: Array<{ adapter: ServiceAdapter; observation: ReadinessObservation }> = [];
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

const controller = new ReadinessController(SERVICE_ADAPTERS, (adapter) => adapter.checkReadiness(), (snapshot, previous) => {
  updateRail(snapshot);
  if (isDetail) updateDetail(snapshot);
  else updateOverview(snapshot, previous);
});
const polling = new ReadinessPolling(() => { void controller.refreshAll(); });
polling.setIntervalSeconds(preferences.refreshSeconds);
polling.start(document.visibilityState === "visible");

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
  polling.stop();
  controller.invalidate();
  document.removeEventListener("visibilitychange", onVisibility);
  theme.dispose();
}, { once: true });

document.documentElement.dataset.dashboard = "ready";
