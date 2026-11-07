# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root. It is an index: the glossary itself is split by area into `context/*.md`. Read the index, then only the file(s) for the area you are working in.
- **`docs/adr/`** — read ADRs that touch the area you're about to work in.

This repo is single-context:

```
/
├── CONTEXT.md                         ← index of the area files
├── context/
│   ├── book-structure.md
│   ├── attribution.md
│   └── …
├── docs/adr/
│   ├── 0001-voice-rules-replace-default-voice-flag.md
│   └── …
└── src/
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in the glossary (`CONTEXT.md` and its `context/*.md` files), including its _Avoid_ lists.

If the concept you need isn't in the glossary yet, that's a signal — either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0007 (book mutations reconcile Book View projections) — but worth reopening because…_
