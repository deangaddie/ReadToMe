# Edit plan accuracy with thinking off

Planning an Edit with AI run with thinking off is a speed option, and its accuracy is best effort. The project doesn't tune the planner prompt, the edit-plan schema or the parser so that individual phrasings reach the same reliability with thinking off as they have with thinking on.

## Why this is out of scope

Planning defaults to thinking on (#295), and thinking on is where the planner is held to its eval bar. A user who turns thinking off trades accuracy for time on purpose, and still sees the plan and its preview before anything is applied.

Closing the gap for one phrasing is expensive and doesn't generalise:

- The cost of each attempt is hours of live eval runs. `EditPlanLiveEvalTests` needs 10-run batches per case and mode against a live `qwen-28b`, and some cases flake on main as well (#303), so every change needs several batches compared against main.
- Prompt wording has stopped helping. In #300, five rewordings of the change_case "headings" rule left the thinking-off miss rate at about 1 in 10.
- The remaining fixes reshape the contract to suit one weak mode. A constrained field ahead of `target` (for example `title_named`) changes the edit-plan schema, `JsonExample` and the parser for every plan. A deterministic backstop that reads the user's raw words was already rejected in #300.

The thinking-off results stay in the eval output as a signal. A thinking-off miss is only worth fixing when the same case also fails with thinking on, or when thinking off stops being optional.

## Prior requests

- #304: "Edit with AI: "headings in ALL CAPS" planner eval misses ~1 in 10 with thinking off"
