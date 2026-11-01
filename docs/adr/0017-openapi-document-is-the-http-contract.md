# The OpenAPI document is the HTTP contract

## Status

accepted (2026-10-31). Decided on [Map: typed host responses make the OpenAPI schema the seam](https://github.com/deangaddie/ReadToMe/issues/103),
with the implement-or-close decision in [#170](https://github.com/deangaddie/ReadToMe/issues/170). The library facts
are in [#165](https://github.com/deangaddie/ReadToMe/issues/165) (`docs/research/openapi-responses.md` on branch
`research/openapi-responses`). The work is the spec [#171](https://github.com/deangaddie/ReadToMe/issues/171), ten
slices with `main` green after each.

## Context

The host's OpenAPI document described request bodies but almost no responses: 105 handlers returned an untyped
`Results.Ok(...)`. So `schema.d.ts` could not type what comes back, and `dtos.ts` mirrored about 150 response shapes
from the C# records by hand. `schema-pins.ts` could pin only request bodies and settings entities. A field renamed on
the host passed `typecheck` and showed up only at run time or in E2E.

`api:check` never ran in the gate, because it needed a host on `:5000`. Enums went over the wire in four forms, every
`int` read as `number | string`, and `ApiClient.get<T>(url)` was an unchecked cast, so neither the route nor the
pairing of a path with its type was checked.

## Decision

**The host's OpenAPI document is the one HTTP contract. The compiler checks it at both ends, and the gate regenerates
it.** Four decisions make it up; each issue's resolution comment holds the detail.

- **[Enums take one wire form](https://github.com/deangaddie/ReadToMe/issues/166).** C# member names, PascalCase, in
  JSON values, route and query segments and dictionary keys, in both directions. One shared global
  `JsonStringEnumConverter` with `allowIntegerValues: false`; no per-property enum converters; no enum sent as a
  `.ToString()` string. Stored JSON keeps its own form.
- **[The gate regenerates the contract](https://github.com/deangaddie/ReadToMe/issues/167).** An explicit E2E test
  (`OpenApiContractTests.Write_the_contract`) gets the `v1` document from `IOpenApiDocumentProvider` on the in-proc
  host and writes a canonical `src/Read2Me.Web/src/app/api/openapi.json`: keys sorted at every depth, `servers`
  dropped, array order kept. `scripts/api-types.ps1` runs it, then `bun run api:types` turns the file into
  `schema.d.ts` (with `--empty-objects-unknown`, so an object with no listed properties, such as a `JsonObject` or a
  map of `JsonElement`, reads `Record<string, unknown>`). The gate runs that script after `dotnet build` and before
  the web check, so `typecheck` is the comparison, not a text diff. Both files are committed, so a contract change
  shows as a diff and the web-only loop needs no .NET.
- **[An endpoint's return type is its declaration](https://github.com/deangaddie/ReadToMe/issues/168).** Handlers
  return `Results<>` unions built with `TypedResults`, over named positional records. One document transformer adds a
  `default` ProblemDetails response to every `/api/` operation. Mapped file endpoints declare their content type with a
  binary schema, `.Produces<Stream>(contentType: …)` (a bare `.Produces(200, contentType: …)` documents no content).
  A response record's constructor parameters carry no defaults, so every field reads required. A property that
  isn't a constructor parameter (a computed get-only one, or any property of a settable
  class such as a settings entity) is marked required, since every answer carries it. Numbers are strict
  (`NumberHandling.Strict`). Document tests fail on an undeclared 2xx body, an integer or
  nullable enum, or a duplicate short schema name.
- **[`dtos.ts` becomes aliases over the schema](https://github.com/deangaddie/ReadToMe/issues/169).** Every type with
  a host counterpart is `export type X = Schema['X']` under its C# name; bad generated names are fixed on the host.
  `schema-pins.ts` is deleted. `ApiClient` is typed by path, openapi-fetch style, with the response inferred from the
  document; the untyped methods go once every call is converted.

## Considered options

- **Keep hand-written DTOs and pins.** Rejected: the pins reach only request bodies, and the rest drifts silently.
- **`.Produces<T>()` beside untyped `Results.Ok`.** Rejected: the declaration could lie, which is the `dtos.ts` drift
  moved into the host.
- **Build-time document generation (`Microsoft.Extensions.ApiDescription.Server`).** Rejected: it runs `Program.Main`
  and migrates the configured Workspace, and would force the web check after the .NET build anyway.
- **Compare a checked-in snapshot's text, or keep `api:check` against a dev host.** Rejected: key order, `servers` and
  formatting make a text compare flaky, and the dev-host check never ran in the gate.
- **openapi-fetch as a dependency.** Rejected: our `ApiClient` keeps its `ApiError` handling, and typing it by path
  needs no new package.
- **Namespace-qualified schema ids (`CreateSchemaReferenceId`).** Rejected: every TypeScript name gets a prefix; the
  collision test and a host rename keep names short instead.

## Consequences

- A host DTO or route change is a reviewable diff in `openapi.json` and `schema.d.ts`, and fails `typecheck` at the
  web call sites it breaks. Forgetting to regenerate is caught, because the gate regenerates anyway.
- About 1,400 hand-written lines of `dtos.ts`, `schema-pins.ts`, `api:check`, four enum wire forms and 64
  `number | string` types go.
- The cost is mechanical: about 130 mapped endpoints in 24 files and 133 wrapper calls, slice by slice.
- Agents lose the per-status problem list (409/422/503) some endpoints declared; the document says "any non-2xx is a
  ProblemDetails" once, which is what the web client already assumes.
- An enum sent as a `.ToString()` string can't be caught by a test; review guards it.
- Out of scope: live hub payloads (`/hubs/live`), request bodies beyond their enum and number form, stored JSON, and
  files served outside mapped endpoints (`StepFileReads`, `/workspace`).
