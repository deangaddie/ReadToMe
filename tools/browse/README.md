# tools/browse — drive a running ReadToMe in a browser

For ad-hoc verification and bug hunting against a real host: write a short `.mjs` script with
the helpers in `browse.mjs`, run it, read the log. Automated E2E tests do **not** live here —
they are in `src/Read2Me.E2eTests` (xUnit + Microsoft.Playwright, in-proc host, fake AI), with
Angular pages under `Tests/Web/` on `WebE2eTestBase`.

```bash
cd tools/browse && npm ci            # playwright-core only; Chromium is the one the E2E tests installed
```

Start what the script needs:

```bash
dotnet run --project src/Read2Me.App --urls http://localhost:5000     # host (web app at /app, Blazor home at /blazor, API, hub)
cd src/Read2Me.Web && npm start                                       # Angular dev server at :4200 (web scripts only)
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
  console.log(await waitForTreeTitles(page, ['Chapter 1', 'Two']));
  console.log((await api(`/api/projects/${project.folder}/book`)).totalChapters, page.toasts, page.errors);
} finally {
  await project.remove();
  await close();
}
```

```bash
node my-check.mjs > my-check.log 2>&1; cat my-check.log
```

Rules that keep runs honest:

- Mutate a throwaway project (`throwawayProject`), never the real ones in the workspace.
- Wait on the state you expect (`waitForTreeTitles`, `waitFor`, Playwright `waitForFunction`), never on a
  count that the stale DOM already satisfies, and never on a sleep alone.
- Redirect the script's output to a file. Piping through `Select-Object` shows nothing until exit, so a
  hung wait looks like silence; the launch watchdog exits after 240 s regardless.
- `page.errors` collects failed API calls and console errors; `page.toasts` the web app's toasts. Print
  both at the end.
- Blazor pages need `openBlazor` (waits for the circuit); clicks before that are dropped.
