# .NET 10 OpenAPI and openapi-typescript: responses, enums, build-time generation

Research for #165 (map #103). Versions: `Microsoft.AspNetCore.OpenApi` **10.0.14** (`src/Directory.Packages.props`), SDK 10.0.403, `openapi-typescript` **7.13.0** (`src/Read2Me.Web/package.json`, patched only to load `@typescript/typescript6`). The default document is **OpenAPI 3.1.1**.

**Method.** Primary sources (Microsoft Learn, dotnet/aspnetcore `release/10.0` source, the package's own XML docs, openapi-ts.dev). Each claim was also checked empirically on 2026-10-31. I added `Microsoft.Extensions.ApiDescription.Server` 10.0.14 and a throwaway `/api/probe/*` endpoint file to this host, ran `dotnet build src/Read2Me.App`, and fed the emitted `obj/Read2Me.App.json` to openapi-typescript 7.13.0. The spike was never committed. "Observed" below means that experiment.

Sources:

- [L1] Learn, *Include OpenAPI metadata* (aspnetcore-10.0): https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0
- [L2] Learn, *Generate OpenAPI documents* (build-time section): https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0#generate-openapi-documents-at-build-time
- [L3] Learn, *What's new in ASP.NET Core 10.0* (OpenAPI section): https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0
- [S1] `GetDocumentCommandWorker.cs`: https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Tools/GetDocumentInsider/src/Commands/GetDocumentCommandWorker.cs
- [S2] `OpenApiServiceCollectionExtensions.cs`: https://github.com/dotnet/aspnetcore/blob/release/10.0/src/OpenApi/src/Extensions/OpenApiServiceCollectionExtensions.cs
- [X1] `Microsoft.AspNetCore.OpenApi.xml` in the 10.0.14 package (`OpenApiOptions.CreateSchemaReferenceId`, `IOpenApiDocumentProvider`)
- [T1] openapi-typescript CLI options: https://openapi-ts.dev/cli

---

## 1. Response inference

| Handler shape | Document (observed) | openapi-typescript |
|---|---|---|
| `() => TypedResults.Ok(x)` | `200` `application/json` `$ref T` | `content: { "application/json": T }` |
| `Results<Ok<T>, NotFound, ProblemHttpResult>` | `200` with `T`; `404` with **no content**; **nothing** for `ProblemHttpResult` | 200 typed, 404 `content?: never` |
| `Results<Ok<T>, ValidationProblem, Conflict<string>>` | `200` T; `400` `application/problem+json` `HttpValidationProblemDetails`; `409` json `string` | all typed |
| `async Task<Results<Ok<T>, NotFound>>` | same as the sync union | same |
| `() => Results.Ok(x)` (untyped `IResult`) | `200` with **no content** | `content?: never` |
| `Results.Ok(x)` + `.Produces<T>().ProducesProblem(404)` | `200` T; `404` `application/problem+json` `ProblemDetails` | typed |
| `() => x` / `async () => x` (raw `T` / `Task<T>`) | `200` `application/json` T | typed |
| `() => IAsyncEnumerable<T>` | `200` json `type: array, items: T` | `T[]` |
| `TypedResults.File(...)` / `Results.File(...)` | `200` with **no content** | `content?: never` |
| `TypedResults.ServerSentEvents(IAsyncEnumerable<T>)` | `200` `text/event-stream` `$ref SseItemOfT` (`data`, `eventType`, `eventId`, `reconnectionInterval`) | object type |
| `TypedResults.NoContent()` | `204` no content | `content?: never` |

Why, from the docs [L1]:

- "Only return types that implement `IEndpointMetadataProvider` create a `responses` entry in the OpenAPI document." `TypedResults` "automatically includes the response type metadata".
- For `Results<T1..TN>`, "any of those types that implement `IEndpointMetadataProvider` will contribute to the endpoint's metadata". `ProblemHttpResult` has no fixed status, so it contributes nothing (observed).
- With no metadata, "Schema for the response body can be inferred from the implicit or explicit return type … otherwise, it's considered to be unspecified". An untyped `IResult` (`Results.Ok`) therefore **can never be inferred**. Only `.Produces<T>()` or `[ProducesResponseType]` describe it.
- File results need `.Produces<FileContentHttpResult>(contentType: ...)` (or `Stream`) to get a binary schema [L1]. A bare file result documents nothing.
- In .NET 10 the SSE item schema is a plain `$ref` to `SseItemOf<T>`. The `itemSchema` keyword arrives only with OpenAPI 3.2 / .NET 11 [L1].

Practical consequence: switching a handler from `Results.Ok(x)` to `TypedResults.Ok(x)` (or a `Results<...>` return type) is enough. `.Produces<T>()` is the alternative when the body stays untyped. Error unions with `ProblemHttpResult` need `.ProducesProblem(status)` to appear.

## 2. Enums

Observed schemas (camelCase property names come from the web JSON defaults):

| C# declaration | Schema component | openapi-typescript |
|---|---|---|
| no converter | `{ "type": "integer" }` | `number` (names lost) |
| per-property `[property: JsonConverter(typeof(JsonStringEnumConverter<E>))]` | `{ "enum": ["Red","DarkBlue"] }` | `"Red" \| "DarkBlue"` |
| type-level `[JsonConverter(typeof(JsonStringEnumConverter<E>))]` | `{ "enum": ["Red","DarkBlue"] }` | `"Red" \| "DarkBlue"` |
| per-property or type-level `CamelCaseEnumConverter<E>` (= `JsonStringEnumConverter<E>(JsonNamingPolicy.CamelCase)`) | `{ "enum": ["red","darkBlue"] }` | `"red" \| "darkBlue"` |
| `[JsonStringEnumMemberName("x")]` members (e.g. `HistoryStepKind`) | names honoured | literal union |
| global `services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))` | **every** enum becomes `{ "enum": ["red","darkBlue"] }` | literal unions |

- **A global HTTP converter does reach the generator** (observed). The generator builds schemas from the same `JsonOptions` the endpoints serialize with. Learn says only that a converter yields `enum` with string values and no converter yields `type: integer` [L1].
- **Precedence matches System.Text.Json**: property attribute > `Options.Converters` > type attribute. Under the global camelCase converter, a type-level `JsonStringEnumConverter<E>` (PascalCase) enum rendered as `"red" | "darkBlue"`. Per-property attributes kept their own form.
- Schema `enum` keywords carry no `"type": "string"`. openapi-typescript is fine with that.
- **openapi-typescript emits string-literal unions by default.** `--enum` (default `false`) would "Generate true TS enums rather than string unions"; `--enum-values` exports value arrays; `--dedupe-enums` works only with `--enum` [T1]. The repo's `api:types` passes none of these, so it gets unions.
- `[Flags]` enums with a converter become `type: string` with no `enum` list [L1].

**Two traps found:**

1. **Nullable use with a type-level converter poisons the shared component.** `HistoryStep.Action` is `StepAction?`, and `StepAction` has a type-level converter. The component is emitted as `enum: [..., "pronunciation", null]`, so the checked-in `schema.d.ts` has `StepAction: ... | null` on **every** use. Nullable enums *without* that pattern render as `oneOf: [{type: null}, {$ref}]` → `null | E` at the property only (observed `PlainColour?`, `TypeLevelColour?` in the probe). Check this whenever a nullable enum uses a type-level converter.
2. **One component per CLR type, whatever its converter.** In the probe, `AttrColour` had a per-property string converter on one record and no converter on another. Both refs pointed at a single `AttrColour: { enum: ["Red","DarkBlue"] }`, even though the second property serializes as an **integer**. So the document is silently wrong. Per-property converters are only safe when every use of that enum carries the same converter. Type-level or global converters avoid this by construction.

## 3. Nullability and required (OpenAPI 3.1)

Observed (all match [L1]/[L3]):

| C# | Schema | openapi-typescript |
|---|---|---|
| record positional `string X` | in `required`, `type: string` | `x: string` |
| record positional `string? X` | in `required`, `type: ["null","string"]` | `x: null \| string` (required, nullable) |
| record positional with default `string X = "d"` | **not** in `required`, `default: "d"` | `x: string`. Non-optional because `--default-non-nullable` defaults to `true` [T1] |
| record positional `ProbeChild? C` / `List<string>?` | `oneOf: [{type:null},{$ref}]` / `type: ["null","array"]` | `null \| T` / `null \| string[]` |
| class `{ get; set; }` without `required` | not in `required` | `x?: string` (optional) |
| class `required string? X` | in `required`, nullable | `x: null \| string` |
| `int` | `type: ["integer","string"]`, `pattern: ^-?(?:0\|[1-9]\d*)$`, `format: int32` | **`number \| string`** |
| `int?` | `type: ["null","integer","string"]` | `null \| number \| string` |

- Required comes from the `required` modifier or `[Required]`, plus the single public constructor's parameters (so a record's positional parameters count). Several public constructors add none, and structs add none [L1].
- Nullable value/reference types get a `type` array including `"null"`. Complex types and collections use `oneOf` [L3].
- **`int` → `number | string`** happens because web apps default `JsonSerializerOptions.NumberHandling` to `AllowReadingFromString`. Setting it to `JsonNumberHandling.Strict` restores plain `integer` [L1][L3], but Strict also stops the host *accepting* quoted numbers in requests. Every `int` in today's `schema.d.ts` is already `number | string`, so a decision is needed if typed responses should give the UI plain `number`. The options are Strict globally, a schema transformer, or living with it.

## 4. Schema naming and collisions

Observed default ids (`OpenApiOptions.CreateDefaultSchemaReferenceId`):

- Plain type: short CLR name with no namespace (`ProbeChild`).
- Generic: `PageOfProbeChild`, `PageOfstring` (primitive in lower case). `List<T>` and `Dictionary<string,T>` are inlined as `array` / `additionalProperties`.
- **Nested types: only the inner name.** `Outer.Inner(string A)` and `Outer2.Inner(int B)` both became `Inner`. One component `{ a: string }` was emitted and *both* endpoints referenced it. **No suffixing, no warning**: the second endpoint's document is wrong.
- The same holds for two top-level types with the same name in different namespaces. Both resolve to the same id.

`OpenApiOptions.CreateSchemaReferenceId` is a `Func<JsonTypeInfo, string?>`. The default delegates to `CreateDefaultSchemaReferenceId`, and returning `null` inlines the schema [X1].

Is customisation needed here? Only if colliding types reach the document. Latent cases in the repo today:

- `Read2Me.Core.Models.AudioReviewState` and `Read2Me.Data.Enums.AudioReviewState`. Neither is in the document yet.
- The many nested union-case records (`Refused`, `Failed`, `NoChange`, `CantOpen`, `TryAgain` in several outcome types). These are not wire DTOs today.
- Private parser DTOs (`CharacterDto` and `VoiceDto` in `Read2Me.Services`) also share names with the public API records. They are never serialized by the host, so they are harmless.

A cheap guard is a test over the generated document: every component name must map to a single CLR type. Alternatively, a `CreateSchemaReferenceId` that prefixes the declaring type for nested types.

## 5. Build-time generation with `Microsoft.Extensions.ApiDescription.Server`

**It works on this host with no code change.** Adding the package (`PrivateAssets=all`) made `dotnet build src/Read2Me.App` run the `GenerateOpenApiDocuments` target. That target runs `dotnet-getdocument.dll`, which wrote `src/Read2Me.App/obj/Read2Me.App.json` and added roughly 1–2 s to the build. Feeding that file to openapi-typescript 7.13.0 reproduced the checked-in `src/Read2Me.Web/src/app/api/schema.d.ts` **byte for byte** (after CRLF normalisation; 7170 lines). openapi-typescript takes a file path as well as a URL.

Knobs [L2][L3]:

- `OpenApiDocumentsDirectory` (relative to the project; `.` = beside the csproj). Learn says the default is the output directory, but on this SDK the target passed `--output ...\obj` (observed).
- `OpenApiGenerateDocumentsOptions`: `--file-name`, `--document-name`, `--openapi-version`.
- `OpenApiGenerateDocumentsOnBuild` (switch it off for the normal build and run it only in the gate).
- `OpenApiGenerationEnvironment` is **.NET 11+** only.

**How it launches the app (the pitfall).** "Build-time OpenAPI document generation functions by launching the apps entrypoint with a mock server implementation … any logic in the apps startup is invoked" [L2]. In source [S1]:

- It uses `HostFactoryResolver.ResolveHostFactory(assembly, stopApplication: false, configureHostBuilder: …)`.
- It adds `IServer` → `NoopServer` and `IHostLifetime` → `NoopHostLifetime`.
- It waits **with no timeout** on `ApplicationStarted`, then resolves `Microsoft.Extensions.ApiDescriptions.IDocumentProvider` and calls `GenerateAsync` with a 1-minute timeout.

So **`Program.Main` runs**: `ApplyMigrations`, then `host.Run()`, which runs `Startup.Configure` and **starts every hosted service**. Observed:

- With no Workspace configured (the worktree default), the host logged "No usable Workspace" and the start-up Quick check (`SetupCheckAtStart`) ran.
- With `Workspace__FolderPath` set, **or with a `Workspace:FolderPath` in `src/Read2Me.App/appsettings.Local.json`** (read because the target runs with the project as working directory), the build **created, migrated and seeded `app.db`** in that folder. It also probed ffmpeg and sent HTTP health checks to the AI containers (`:8004`, `:9000`). The workspace-gated hosted services (`WorkspaceHostedService`) would also start.
- On the owner's machine, a plain `dotnet build` would therefore migrate the real Workspace database every time the document is regenerated.

What the host would need before adopting it:

- Skip `ApplyMigrations` and Workspace judgement when `Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider"`, the documented guard [L2]. Alternatively, force a no-Workspace configuration for the generator (e.g. an env var or `Workspace:FolderPath` override in the target).
- Skip or neutralise hosted services (`SetupCheckAtStart`, `LiveRelay`, the workspace-gated services) under the same guard.
- `Startup.Configure` resolves `EventJournal<>`, `ThroughputAggregator` and `LiveRelay` eagerly. These are harmless in-process singletons, but they run.
- Prefer `OpenApiGenerateDocumentsOnBuild=false` for the everyday build. Generate only from the gate (`dotnet build -p:OpenApiGenerateDocumentsOnBuild=true`, or invoke the target), so the inner loop stays fast and side-effect free.
- The no-Workspace document is what generated the identical `schema.d.ts`. Endpoints don't depend on the Workspace being usable (`UseNoWorkspaceGuard` is middleware, not routing).

## 6. In-test alternatives for the gate

- **`IOpenApiDocumentProvider` (new in .NET 10)**: "Inject the interface to access the OpenAPI document … outside the context of HTTP requests" [L3]. `AddOpenApi` registers it as a **keyed singleton under the lower-cased document name**: `services.AddKeyedSingleton<IOpenApiDocumentProvider, OpenApiDocumentService>(documentName)` [S2]. A test with the in-proc host can call `GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync()` [X1]. That returns an `OpenApiDocument` object, which has to be serialized (Microsoft.OpenApi `SerializeAsV31`) to compare with JSON.
- **Plain HTTP in the existing in-proc host**: `GET /openapi/v1.json` (already done by `HostSmokeTests.Openapi_document_lists_agent_endpoints` in `src/Read2Me.E2eTests`). The test host already runs with a throwaway/fake configuration, which sidesteps the Workspace side effects of §5.
- Either way the test can write the JSON to a file. `check-api-types.ts` can then run openapi-typescript on that file instead of fetching `localhost:5000` (it takes a URL today via `R2M_OPENAPI_URL`, but the CLI accepts a path). The test could also snapshot the JSON itself. Where the comparison lives decides gate ordering: today `api:check` runs in stage 1, before any .NET build.
- Build-time generation (§5) and in-test generation produce the same document. The difference is that build-time needs the startup guard, while the test host already has an isolated configuration.

## Facts that adjust the map's assumptions (#103)

- The gate **can** get the document without a running host. Build-time generation reproduces today's `schema.d.ts` exactly. But it is **not side-effect free on this host**: it runs `Main` and hosted services and migrates the configured Workspace's `app.db`. It needs a guard or an isolated configuration first. The in-test route (`IOpenApiDocumentProvider` or the in-proc host) avoids that.
- A global enum converter in `ConfigureHttpJsonOptions` **does** reach the OpenAPI generator. It also overrides type-level `[JsonConverter]` attributes, so "one enum form on the wire" via a global converter changes the integer settings enums *and* the PascalCase type-level enums at once.
- Today's `schema.d.ts` already shows two enum defects: `StepAction` carries `| null` on every use, and per-property converters can mis-describe shared enums. It also renders every `int` as `number | string`, because of the web default `NumberHandling`.
- Default schema ids silently collide on nested or same-named types. Typing ~100 endpoints raises the odds, so the spec should include a collision guard or a `CreateSchemaReferenceId`.
