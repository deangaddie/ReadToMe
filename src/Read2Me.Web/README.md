# Read2Me web (Angular)

Angular 22 front end for ReadToMe. It is a sibling of the .NET projects and is **not** part of
`Read2Me.slnx`; `dotnet build` never touches it. In production it is built into
`src/Read2Me.App/wwwroot/app/` and served by the .NET host under `/app/`.

## Prerequisites

- Node `>=24.18 <25` and npm `>=11.16 <12` (same pins as `Infra/ContainerHealth`).
- A running ReadToMe host on `http://localhost:5000` for anything that talks to the API
  (`dotnet run --project src/Read2Me.App` from the repo root). In the Development environment the
  host does not redirect HTTP to HTTPS, so the proxy, `npm run api:types` and the host-backed unit
  tests can talk plain HTTP.

## Commands

```bash
npm ci               # install exactly what package-lock.json says
npm start            # ng serve → http://localhost:4200/app/ (proxies /api, /hubs, /workspace, /openapi to :5000)
npm run build        # production build → ../Read2Me.App/wwwroot/app/ (git-ignored)
npm run check        # lint + typecheck + api:check + unit tests + build — must be green before a PR
npm run lint         # ESLint (angular-eslint)
npm run typecheck    # tsc --noEmit against tsconfig.app.json
npm test             # Vitest, single run, jsdom
npm run format       # Prettier
npm run api:types    # regenerate src/app/api/schema.d.ts from the host's /openapi/v1.json
npm run api:check    # fail if schema.d.ts is stale against the running host (skips with a warning when the host is down)
```

## Layout

- `src/app/` — application code (path alias `@app/*`).
- `src/app/ui/` — the `r2m-*` component library (import from `@app/ui`); every component has a story on `/app/styleguide`, which is the place to check a new component in both colour schemes.
- `src/styles/_tokens.scss` — design tokens; component SCSS consumes these and never hard-codes colours.
- `src/app/api/schema.d.ts` — generated OpenAPI types, checked in; regenerate with `npm run api:types`.
- `src/app/live/` — the `/hubs/live` client (`LiveService`, wire shapes in `live-messages.ts`); `live-messages.spec.ts` reads the C# hub sources and fails when a family or `kind` string drifts.
- `src/assets/fonts/` — self-hosted Inter (variable) and Material Symbols Rounded. No CDN fonts.
- `proxy.conf.json` — dev proxy so the browser stays same-origin (no CORS).

## Conventions

- Standalone components, signals, zoneless change detection, `OnPush` everywhere.
- TypeScript `strict` + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes`.
- Unit tests sit next to the code as `*.spec.ts`. End-to-end tests live in `src/Read2Me.E2eTests`.
