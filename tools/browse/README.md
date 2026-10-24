# tools/browse — drive a running ReadToMe in a browser

For ad-hoc verification and bug hunting against a real host: write a short `.mjs` script with
the helpers in `browse.mjs`, run it under Bun, read the log. Automated E2E tests do **not** live
here — they are in `src/Read2Me.E2eTests` (xUnit + Microsoft.Playwright, in-proc host, fake AI),
with the browser tests under `Tests/Web/` on `E2eTestBase`.

```bash
cd tools/browse && bun install        # playwright-core only; Chromium is the one the E2E tests installed
```

## Where the app is

`openWeb` opens a path under `/app`, the web app's prefix on the host and on its dev server.

| Variable | Default | Meaning |
|---|---|---|
| `R2M_HOST` | `http://localhost:5000` | the host: the API, the hub, and the built bundle at `/app` |
| `R2M_WEB` | `http://localhost:4300` | the origin serving the app — the dev server, or `R2M_HOST` for the host's own bundle |

Start what the script needs, from the repo root:

```bash
dotnet run --project src/Read2Me.App --urls http://localhost:5000 -- --Workspace:FolderPath="$PWD/tools/browse/workspace"   # host (API, hub, /app bundle)
cd src/Read2Me.Web && bun run dev                                     # dev server at :4300, proxying to the host
```

## The tool workspace

The scripts bring their own files and never touch the owner's workspace (`D:\Dev\Read\WS` in
Development config). The host above runs on `tools/browse/workspace/`, which is git-ignored: the
first start creates it with a fresh `app.db` seeded with the default AI service configs. Delete the
folder to start clean. Settings that live in `app.db`, such as the ffmpeg path, start unset there.

The books are Project Gutenberg epubs in `books/`, committed with the tools; `BOOKS` in
`browse.mjs` lists them, by the folder name each would get:

| Book | Source |
|---|---|
| `alices-adventures-in-wonderland` | <https://www.gutenberg.org/ebooks/11> |
| `the-adventures-of-sherlock-holmes` | <https://www.gutenberg.org/ebooks/1661> |
| `pride-and-prejudice` | <https://www.gutenberg.org/ebooks/1342> (the no-images epub) |
| `dracula` | <https://www.gutenberg.org/ebooks/345> |
| `the-call-of-the-wild` | <https://www.gutenberg.org/ebooks/215> |

Only add public-domain books from Project Gutenberg here. `bookProject('dracula')` imports one as a
throwaway project; `throwawayProject(text)` builds one from a string.

Then, from `tools/browse` (write scripts here or anywhere; they import `./browse.mjs`):

```js
import { launch, throwawayProject, openWeb, openNodeMenu, chooseEntry, answerPrompt, waitForTreeTitles, api } from './browse.mjs';

const { page, close } = await launch();
const project = await throwawayProject('I\n\nFirst chapter.\n\nII\n\nSecond chapter.');
try {
  await openWeb(page, `/projects/${project.folder}/book?mode=speakers`);
  await openNodeMenu(page, page.locator('.r2m-paragraph').nth(1).locator('.r2m-paragraph__menu'));
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
bun my-check.mjs > my-check.log 2>&1; cat my-check.log
R2M_WEB=http://localhost:5000 bun my-check.mjs                         # no dev server: drive the host's own /app bundle
```

`openWeb` waits for `app-root *`. `openNodeMenu` / `chooseEntry` drive the shared
`r2m-node-menu` popover (`#r2m-node-menu-panel`; `chooseEntry` clicks `[role="menu"] [data-entry]`,
so submenus work too), `answerPrompt` and
`acceptConfirm` the `r2m-text-prompt-dialog` and `r2m-confirm-dialog`, and `waitForTreeTitles` the
reader's structure tree. Also exported: `bookProject`, `BOOKS`, `waitForServer`, `post`, `visibleText`, `sleep`, `stamp`,
`resolveWeb` and the `HOST` / `WEB` / `APP` constants. `browse.test.mjs` covers the `R2M_WEB`
resolution: `bun test`.

The other `.mjs` files here (`native-*-verify.mjs`, `cutover-verify.mjs`, `shell-check.mjs`,
`activity-check.mjs`, …) are earlier one-off checks, kept as examples; they are not a suite. Each
imports its own throwaway book project and removes it at the end; the ones that took a project
folder now take a `BOOKS` name.

Rules that keep runs honest:

- Run the host on the tool workspace, and work on a project the script created (`bookProject`,
  `throwawayProject`), never on one it found there.
- Wait on the state you expect (`waitForTreeTitles`, `waitFor`, Playwright `waitForFunction`), never on a
  count that the stale DOM already satisfies, and never on a sleep alone.
- Redirect the script's output to a file. Piping through `Select-Object` shows nothing until exit, so a
  hung wait looks like silence; the launch watchdog exits after 240 s regardless (exit code 2; an unhandled rejection exits 3).
- `page.errors` collects failed API calls and console errors; `page.toasts` the web app's toasts. Print
  both at the end.
