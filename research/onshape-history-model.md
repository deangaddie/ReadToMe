# Onshape's document history model — what ReadToMe's History should borrow

Research for `.scratch/project-history/issues/01-onshape-history-model.md` (2026-10-19).
Glossary: [context/history.md](../context/history.md). Onshape *microversion* ≈ our **History step**; Onshape *restore* ≈ our **Restore**.

## Sources

Primary, first-party only:

- [B] Ilya Baran (Onshape), "Under The Hood: How Collaboration Works", Onshape blog, 2015-06-22 — <https://www.onshape.com/en/blog/under-the-hood-how-collaboration-works>
- [H-VH] Onshape Help, "Versions and history" — <https://cad.onshape.com/help/Content/Document/versions_and_history.htm>
- [H-DM] Onshape Help, "Document management" — <https://cad.onshape.com/help/Content/Document/document_management.htm>
- [H-PV] Onshape Help Primer, "Versions" — <https://cad.onshape.com/help/Content/Primer/versions.htm>
- [H-R] Onshape Help, "Restoring" — <https://cad.onshape.com/help/Content/Document/restoring.htm>
- [H-M] Onshape Help, "Merging" — <https://cad.onshape.com/help/Content/Document/merging.htm>
- [D-I] Onshape developer docs, "API intro" — <https://onshape-public.github.io/docs/api-intro/>
- [D-D] Onshape developer docs, "Documents" — <https://onshape-public.github.io/docs/api-adv/documents/>
- [OAS] Onshape REST API OpenAPI spec, fetched 2026-10-19 — <https://cad.onshape.com/api/openapi> (the spec behind the Glassworks API Explorer). Schema and operation names below are quoted from it.

Where a point is my inference rather than a source statement it is marked **(inference)**.

## 1. Microversions

**What one is.** "An eternal, immutable object" holding a Part Studio's definition at one point in time; microversions are never edited, and any change to the definition — "changing an extrude depth, renaming a part, or dragging a sketch" — creates a new one [B]. The developer docs say "a microversion of every document is automatically created with every edit" [D-I], and "POST requests are always made to a workspace, since versions and microversions are immutable" [D-I].

**What produces one.** One completed edit, not one per record and not one per keystroke: "Onshape captures the state of every tab in a workspace every time an edit is completed", and "Every persisted change to a tab in a workspace is recorded in the document's History" [H-DM]. There is no save button; the edit's commit is the step [H-DM].

**Grouping.** Onshape does not document a "group several edits" mechanism. The unit is the *persisted edit*: a feature edit dialog (e.g. editing Extrude 1, however many parameters it changes, and however much downstream geometry regenerates) commits as one entry labelled `Tab-name::Action:Feature-name` [H-DM]. Regenerated geometry is not stored as separate changes; the microversion stores the *definition* change and geometry is derived [B]. A merge is one new microversion in the target containing all the incoming changes [B]; a restore is one new entry [H-R]. **(inference)** Onshape's grouping is "one commit of one user operation", which is exactly our "one step per user action / per committed save".

**Immutability.** Absolute: microversions are never modified [B][D-I]; all writes go to a workspace [D-I].

## 2. Versions vs workspaces vs branches

- **Workspace**: the mutable modeling space; it tracks its *latest* microversion [B]. "Work within an Onshape document is organized into branches; each branch contains a workspace and may contain versions"; a document starts with the Main workspace [D-I].
- **Version**: a named, immutable snapshot of a workspace at one point: "Versions are immutable and capture the complete scope of a workspace at a particular point in time" [H-DM]; "View only" [H-VH][H-PV]. A version is just a name on a microversion: `BTVersionInfo` carries `microversion`, `parent`, `name`, `description`, `creator`, `createdAt` [OAS]. Versions can be created from any history point ("Create version here" [H-VH]; `createVersion` params include `microversionId` and `fromHistory` [OAS]). Main and versions cannot be deleted [H-VH].
- **Branch**: "A branch can have zero or more versions, and up to one workspace" [H-VH]. In the UI you branch from a version ("Branch to create workspace") [H-VH][H-DM]; the API's `createWorkspace` takes `BTVersionOrWorkspaceParams` (which include `versionId` / `microversionId`) and returns a `BTWorkspaceInfo` with `parent`, `parents` (versions) and `microversion` [OAS]. Deleting a branch's workspace leaves its versions; a branch with no versions is removed with it [H-VH].
- **Tree**: because each workspace points at its tip and each microversion points at its parent, "microversions form a tree"; two workspaces share history up to the branch point [B]. Onshape's own analogy is git: microversion = commit, workspace = branch, version = tag [B].
- **Merge**: "Merging takes the delta from the base to the source and applies it to the target", base being the common ancestor [H-M]; it "creates a new microversion in 'to' containing" the changes not yet in it [B]. Strategies MERGE / KEEP / REPLACE overall, overridable per tab (`BTVersionOrWorkspaceMergeInfo.defaultMergeStrategy`, `mergeStrategyElementOverrides`) [H-M][OAS]. Conflicts resolve in favour of the source rather than blocking [B]. Merges show as explicit merge lines in the graph [H-VH][H-M]. Preconditions include the document not being open elsewhere and matching FeatureScript versions [H-M].

## 3. Restore

- Restore is done in the workspace you are in, from a previous point in the **same branch** [H-R][H-DM]: right-click a history entry → "Restore to…" [H-R], or the "Restore to" link while viewing an entry [H-DM].
- **It appends.** "The Restore operation is displayed as the latest change to take place in the … branch" [H-R]. The API confirms: `restoreFromHistory` (`POST /documents/{did}/w/{wid}/restore/{vm}/{vmid}`) returns `BTRestoreFromHistoryInfo { newMicroversion }` [OAS], and a history entry's `restoreId` is documented as: "If this microversion is the result of a restore from another microversion, the restoreId will be the microversion Id of the original microversion that was restored" [OAS].
- **Later history** is not discussed in the help [H-R]; since microversions are immutable and never deleted [B], the entries after the restored point remain in History. **(inference, strongly supported)**
- Restore can be **partial per tab**: `BTRestoreInfo { defaultRestoreStrategy, elementIdToStrategyOverride }` with `KEEP | REPLACE` [OAS][H-R]. ReadToMe's v1 is project-wide only, so this is a later option, not v1.
- Restore vs undo: undo makes a new microversion that reverses only *your* action; restore returns to an earlier state and "can remove collaborators' edits" [B]. Both append; neither rewinds.
- A webhook event exists for it (`GBTRestoreDocumentFromHistoryMessageBody` with `documentMicroversionId`) [OAS] — i.e. restore is a first-class event, like any other change.

## 4. Viewing an old state

- "Click a history entry to visualize the design at that point"; "click Return to <branch-name> to return to the currently active branch at its current point in history" [H-DM]. Opening a change, version or branch is a context-menu "Open" (also in new tab, or "Copy link") [H-VH] — old states are addressable by URL (`/m/{mid}`) [D-I].
- Read-only: versions are explicitly "View only" [H-VH][H-PV], and microversions are immutable for the API [D-I]. The help does not literally say "a history entry view is read-only", but nothing can be written to a microversion [D-I]; editing needs a restore (or a branch) first. **(inference)**
- Every read endpoint takes `{wvm}` (workspace, version or microversion), e.g. `getDocumentContents`, `getElementsInDocument` [OAS]: viewing an old state is the *same* read API pointed at a different id, not a separate "history view" API.
- **Per entry** the panel shows the description `Tab-name::Action:Feature-name` [H-DM], the user ("Modified" column) [H-VH], and the time (filterable by "Date modified") [H-VH]. The API entry `BTDocumentHistoryInfo` has `microversionId`, `nextMicroversionId`, `description`, `userId`, `username`, `date`, `canBeRestored`, `restoreId` [OAS]. History is searchable and filterable by tab, tab type, modified by and date [H-VH]. Compare works between any two history points [H-VH].

## 5. Storage and ids

- **Delta chain, not snapshots**: each microversion "keeps a reference to its parent microversion plus the definition change itself" and stores only the change relative to its parent [B]. Changes reference features by internal id, so a change can be re-applied on a different parent (the basis of concurrent editing and merge) [B]. **(inference)** Onshape surely caches materialised state for performance, but it does not document that.
- **Ids**: 24-character ids for document, workspace, version, microversion and element; URLs `/d/{did}/{w|v|m}/{id}/e/{eid}` [D-I]. Every element (tab) also reports its own `microversionId` (`BTDocumentElementInfo`) [OAS]; `getCurrentMicroversion` returns a workspace's or version's tip [OAS]; a version points at exactly one `microversion` [D-D][OAS].
- History is read with `getDocumentHistory` (`GET /documents/d/{did}/{w|m}/{id}/documenthistory`) — scoped to a workspace or a microversion, i.e. "walk back from this tip" [OAS]. The meaning of `nextMicroversionId` is not documented in the spec; **(inference)** it is the adjacent entry in the walk, i.e. the parent link.

## 6. Lessons for a linear v1 that can branch later

1. **Step = one committed user action**, immutable once written, labelled `area : action : target` (Onshape's `Tab::Action:Feature`). Matches the map's decisions.
2. **Give every step its own id and a `ParentStepId`** now, even though v1 is a straight line. That single pointer is what makes Onshape's history a tree [B]; with it, branching is "a second tip", not a migration.
3. **Keep the "current tip" as a separate pointer** (Onshape's workspace → latest microversion [B]), not "max(sequence)". v1 has one tip (`Main`); branches later are more tips. A sequence number for display ordering is fine but must not be the identity.
4. **Restore = append a step whose parent is the current tip, plus `RestoredFromStepId`** (Onshape `restoreId` [OAS]). Never move the tip backwards. Later steps stay reachable because nothing points them away.
5. **Store a step's change against its parent, addressed by stable record ids** (Guid keys already exist), so a change can be replayed on another parent later — this is what merge needs [B][H-M]. Whether a step also stores (or caches) a full snapshot is the storage ADR's call; Onshape documents deltas [B].
6. **Make "read at step X" the same read path as "read current"** (Onshape's `{wvm}` parameter [OAS]): the project readers take a state reference (tip or step id). Viewing is then just the read API at an older id, writes always target the tip [D-I].
7. **Record author and timestamp on every step** (Onshape `userId/username/date` [OAS]) even if single-user today; plus `canBeRestored`-style flag if some steps (e.g. across schema migrations) can't be restored.
8. **Named milestones later = a name on a step id** (Onshape version → `microversion` [OAS]), not a separate copy. Nothing to build in v1 beyond stable step ids.
9. **Merge steps later have two parents.** Leave room by not baking "exactly one parent" into a non-nullable unique constraint shape that would need rework, e.g. keep `ParentStepId` plus an optional `MergedFromStepId`, the way Onshape draws merges as explicit lines [H-VH]. **(inference)**
10. **Do not adopt** Onshape's per-tab restore/merge strategies in v1 (our History is project-wide by decision), nor its concurrent re-application machinery.
