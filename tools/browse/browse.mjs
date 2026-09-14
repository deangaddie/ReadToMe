// Helpers for driving a running ReadToMe host from a throwaway Node script. See README.md.
//
// playwright-core is pinned to the Microsoft.Playwright version the E2E project uses, so the
// Chromium the .NET tests already installed under %LOCALAPPDATA%\ms-playwright is the one this
// launches — no download, no browser install step.
import { chromium } from 'playwright-core';

export const HOST = process.env.R2M_HOST ?? 'http://localhost:5000';
/** The Angular dev server (`npm start` in src/Read2Me.Web) proxies /api and /hubs to HOST. */
export const WEB = process.env.R2M_WEB ?? 'http://localhost:4200';

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export const stamp = () => new Date().toISOString().slice(11, 19);

/**
 * A browser with a hard stop: a hung wait can never keep the script alive. Console errors and
 * failed API calls are collected on `page.errors`; toasts the web app shows on `page.toasts`.
 */
export async function launch({ headless = true, watchdogMs = 240_000 } = {}) {
  setTimeout(() => {
    console.log(`${stamp()} WATCHDOG: exiting after ${watchdogMs / 1000} s`);
    process.exit(2);
  }, watchdogMs).unref();
  process.on('unhandledRejection', (e) => {
    console.log(`${stamp()} unhandledRejection: ${e?.message ?? e}`);
    process.exit(3);
  });

  const browser = await chromium.launch({ headless, ignoreHTTPSErrors: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, ignoreHTTPSErrors: true });
  page.errors = [];
  page.toasts = [];
  page.on('response', (r) => {
    if (r.url().includes('/api/') && r.status() >= 400)
      page.errors.push(`${r.status()} ${r.request().method()} ${r.url()}`);
  });
  page.on('console', (m) => {
    if (m.type() === 'error') page.errors.push(`console: ${m.text()}`);
  });
  page.on('pageerror', (e) => page.errors.push(`pageerror: ${e.message}`));
  const toastWatch = setInterval(async () => {
    try {
      for (const t of await page.$$eval('.r2m-toast-panel', (els) => els.map((e) => e.textContent.trim())))
        if (!page.toasts.includes(t)) page.toasts.push(t);
    } catch {
      /* page navigating */
    }
  }, 200);

  const close = async () => {
    clearInterval(toastWatch);
    await Promise.race([browser.close(), sleep(5000)]);
  };
  return { browser, page, close };
}

/** Polls `fn` until it returns a truthy value; the value is returned. */
export async function waitFor(fn, label, timeoutMs = 8000) {
  const start = Date.now();
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() - start > timeoutMs) throw new Error(`timeout: ${label}`);
    await sleep(150);
  }
}

/** Resolves once `url` answers 2xx — e.g. wait for `ng serve` or the host to come up. */
export function waitForServer(url, timeoutMs = 120_000) {
  return waitFor(
    async () => {
      try {
        return (await fetch(url)).ok;
      } catch {
        return false;
      }
    },
    `server at ${url}`,
    timeoutMs,
  );
}

export const api = async (path, init) => {
  const r = await fetch(`${HOST}${path}`, init);
  if (!r.ok) throw new Error(`${r.status} ${path}: ${await r.text()}`);
  return r.status === 204 ? null : r.json();
};
export const post = (path, body) =>
  api(path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

/**
 * A throwaway text project to mutate freely. Returns the folder and a `remove()` — real projects
 * in the workspace stay untouched.
 */
export async function throwawayProject(text, { title = `browse-${Date.now().toString(36)}` } = {}) {
  const form = new FormData();
  form.set('title', title);
  form.set('bookTitle', title);
  form.set('author', 'Browse');
  form.set('file', new Blob([text], { type: 'text/plain' }), 'book.txt');
  const r = await fetch(`${HOST}/api/projects`, { method: 'POST', body: form });
  if (!r.ok) throw new Error(`create project: ${r.status} ${await r.text()}`);
  const { folderName } = await r.json();
  await post(`/api/projects/${folderName}/import`, { reread: false });
  return {
    folder: folderName,
    remove: () => fetch(`${HOST}/api/projects/${folderName}`, { method: 'DELETE' }),
  };
}

// ---- Angular web app (/app) -------------------------------------------------------------------

/** Opens a web-app route and waits for the reader rows (book pages) or the shell to render. */
export async function openWeb(page, path) {
  await page.goto(`${WEB}/app${path}`);
  await page.waitForSelector('app-root *', { timeout: 60_000 });
}

/** Opens a row's `r2m-node-menu` (host = selector or Locator) and returns its `data-entry` ids. */
export async function openNodeMenu(page, host) {
  const menu = typeof host === 'string' ? page.locator(host) : host;
  await menu.hover({ timeout: 10_000 });
  await menu.locator('button').first().click({ timeout: 10_000 });
  await page.waitForSelector('.mat-mdc-menu-panel', { state: 'visible' });
  return page.$$eval('.mat-mdc-menu-panel [data-entry]', (els) => els.map((e) => e.dataset.entry));
}
export const chooseEntry = (page, entry) =>
  page.click(`.mat-mdc-menu-panel [data-entry="${entry}"]`, { timeout: 10_000 });

/** Answers the `r2m-text-prompt-dialog` and waits for it to close. */
export async function answerPrompt(page, text) {
  await page.waitForSelector('r2m-text-prompt-dialog', { state: 'visible' });
  await page.fill('r2m-text-prompt-dialog .r2m-text-prompt-dialog__input', text);
  await page.click('r2m-text-prompt-dialog .r2m-text-prompt-dialog__confirm');
  await page.waitForSelector('r2m-text-prompt-dialog', { state: 'detached' });
}

/** Accepts the `r2m-confirm-dialog`; returns its full text (title, message, buttons). */
export async function acceptConfirm(page) {
  await page.waitForSelector('r2m-confirm-dialog', { state: 'visible' });
  const text = await page.locator('r2m-confirm-dialog').innerText();
  await page.click('r2m-confirm-dialog .r2m-confirm-dialog__confirm');
  await page.waitForSelector('r2m-confirm-dialog', { state: 'detached' });
  return text.replace(/\s+/g, ' ').trim();
}

/** Waits for the reader's structure tree to show exactly these titles, in order. */
export function waitForTreeTitles(page, expected, timeoutMs = 8000) {
  return waitFor(
    async () => {
      const t = await page.$$eval('.tree__title', (els) => els.map((e) => e.textContent.trim()));
      return JSON.stringify(t) === JSON.stringify(expected) ? t : null;
    },
    `tree titles ${JSON.stringify(expected)}`,
    timeoutMs,
  );
}

// ---- Blazor (/) -------------------------------------------------------------------------------

/** Opens a Blazor page and waits for the circuit (the page's own websocket) before returning. */
export async function openBlazor(page, path) {
  const ws = page.waitForEvent('websocket', { timeout: 15_000 });
  await page.goto(`${HOST}${path}`);
  await ws;
  await page.waitForLoadState('networkidle');
}

/** Visible text of the page, whitespace-collapsed (textContent would include stylesheets). */
export const visibleText = async (page) =>
  (await page.evaluate(() => document.body.innerText)).replace(/\s+/g, ' ');
