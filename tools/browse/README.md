# tools/browse — drive a running ReadToMe in a browser

For ad-hoc verification and bug hunting against a real host: write a short `.mjs` script with
the helpers in `browse.mjs`, run it, read the log. Automated E2E tests do **not** live here —
they are in `src/Read2Me.E2eTests` (xUnit + Microsoft.Playwright, in-proc host, fake AI), with
Angular pages under `Tests/Web/` and native pages under `Tests/Native/`, both on `E2eTestBase`.

```bash
cd tools/browse && bun install        # playwright-core only; Chromium is the one the E2E tests installed
cd tools/browse && npm ci             # the same under Node, which still works until the native cutover
```

## Which app

Two apps run side by side while the native migration is in progress (`docs/agents/web.md`,
"Native app"): Angular at `/app` and the native app at `/app2`. `R2M_APP` picks the prefix
`openWeb` uses, and `R2M_WEB` picks the origin serving it:

| Variable | Default | Meaning |
|---|---|---|
| `R2M_HOST` | `http://localhost:5000` | the host: the API, the hub, and the built bundles at `/app` and `/app2` |
| `R2M_APP` | `/app` | the app to drive: `/app` (Angular) or `/app2` (native) |
| `R2M_WEB` | by app: `:4200` for `/app`, `:4300` for `/app2` | the origin serving the app — a dev server, or `R2M_HOST` for the host's own bundle |

Start what the script needs:

```bash
dotnet run --project src/Read2Me.App --urls http://localhost:5000     # host (API, hub, /app and /app2 bundles)
cd src/Read2Me.Web && npm start                                       # Angular dev server at :4200 (R2M_APP=/app, the default)
cd src/Read2Me.Native && bun run dev                                  # native dev server at :4300 (R2M_APP=/app2)
```

Then, from `tools/browse` (write scripts here or anywhere; they import `./browse.mjs`):

```js
import { launch, throwawayProject, openWeb, openNodeMenu, chooseEntry, answerPrompt, waitForTreeTitles, api } from './browse.mjs';

const { page, close } = await launch();
const project = await throwawayProject('I\n\nFirst chapter.\n\nII\n\nSecond chapter.');
try {
  await openWeb(page, `/projects/${project.folder}/book?mode=speakers`);
  await openNodeMenu(page, page.locator('r2m-paragraph').nth(1).locator('.r2m-paragraph__menu'));
  await chooseEntry(page, 'split');
  await answerPrompt(page, 'Two');
  // The import is one chapter, titled after the uploaded file (book.txt); the split adds "Two".
  console.log(await waitForTreeTitles(page, ['book', 'Two']));
  console.log((await api(`/api/projects/${project.folder}/book`)).totalChapters, page.toasts, page.errors);
} finally {
  await project.remove();
  await close();
}
```

```bash
bun my-check.mjs > my-check.log 2>&1; cat my-check.log                 # or `node my-check.mjs`
R2M_WEB=http://localhost:5000 bun my-check.mjs                         # no dev server: drive the host's own /app bundle
R2M_APP=/app2 bun my-check.mjs                                         # the native app on its dev server (:4300)
R2M_APP=/app2 R2M_WEB=http://localhost:5000 bun my-check.mjs           # the native app from the host's /app2 bundle
```

`openWeb` waits for `app-root *` in both apps. The menu, prompt and confirm helpers
(`openNodeMenu`, `chooseEntry`, `answerPrompt`, `acceptConfirm`) and `waitForTreeTitles` are
written against the Angular reader's markup; the native shell keeps the same `r2m-*` element
names, BEM classes and `data-*` attributes (`docs/agents/web.md`, "Conventions"), but the Material
overlay panel (`.mat-mdc-menu-panel`) does not exist there, so `openNodeMenu` / `chooseEntry` need a
native counterpart once the reader is ported. `browse.test.mjs` covers the `R2M_APP` / `R2M_WEB`
resolution: `bun test` or `node --test browse.test.mjs`.

Rules that keep runs honest:

- Mutate a throwaway project (`throwawayProject`), never the real ones in the workspace.
- Wait on the state you expect (`waitForTreeTitles`, `waitFor`, Playwright `waitForFunction`), never on a
  count that the stale DOM already satisfies, and never on a sleep alone.
- Redirect the script's output to a file. Piping through `Select-Object` shows nothing until exit, so a
  hung wait looks like silence; the launch watchdog exits after 240 s regardless.
- `page.errors` collects failed API calls and console errors; `page.toasts` the web app's toasts. Print
  both at the end.
