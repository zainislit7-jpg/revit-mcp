# Shared conventions: Revit + AutoCAD MCP servers

Proposed by the Revit thread (2026-10-23) so both servers can ship in one Claude plugin.
Edit freely; say what changed in your thread.

## Layout (per app)
- `<App>Mcp.Core`   net10.0, no Autodesk refs: result shaping, paging, envelopes. Unit-tested.
- `<App>Mcp.Addin`  the in-app plugin (Revit add-in / AutoCAD NETLOAD dll). Localhost HTTP listener.
- `<App>Mcp.Bridge` net10.0 console, MCP over stdio (ModelContextProtocol 2.x). Thin: forwards calls.
- Plugin launches the bridge: `dotnet <path>/<App>Mcp.Bridge.dll`.

## Discovery + transport (add-in <-> bridge)
- Add-in binds `127.0.0.1` only, random free port, random 32-byte token.
- Writes `%LOCALAPPDATA%\ClaudeMcp\<app>\session.json` = `{"port":n,"token":"..","pid":n,"version":".."}`; deletes it on shutdown.
- Bridge re-reads session.json on every call (app restarts just work).
- `POST /call` header `X-Mcp-Token`, body `{"tool":"query","args":{...}}`.
- Reply `{"ok":true,"text":"...","image":"<base64 png>|null"}` or `{"ok":false,"error":"..."}`.

## Tools (same six verbs, app prefix)
`<app>_status`, `<app>_query`, `<app>_view`, `<app>_export`, `<app>_modify`, `<app>_run_script`
(app = `revit` | `acad`)

## Shared argument names
- `mode` summary | list | get (query)
- `fields` comma-separated string, e.g. `"Id,Name,Level"`
- `limit` (default 50, max 500), `cursor` (opaque string from previous `next:`)
- `ids` comma-separated string
- `filter` simple `Param=Value; Param2~substr` string
- `dry_run` bool, default true for modify
- `path` output file for export (default: `%USERPROFILE%\Documents\ClaudeMcp\exports\`)

## Result format (text, token-lean)
- Line 1: one-line summary, e.g. `Walls: 412 (showing 1-50) next:50`
- Then a pipe table: header row, one row per item, empty cells for nulls, no padding.
- No nulls, no GUIDs unless asked, numbers in display units, rounded.
- Hard cap ~8k chars per result; beyond that say `truncated, use export`.
- Errors: plain sentence starting `Error:`.

## Safety
- Read-only by default. Writes need `allowWrite: true` in `%APPDATA%\ClaudeMcp\<app>\config.json`.
- `run_script` needs `allowScripts: true`; in read-only mode scripts run in a rolled-back transaction.
- Each modify / script call = one named undo step (`Claude: <action>`).

## Additions from the AutoCAD thread (2026-10-23)
Adopted everything above as-is for AutoCAD (`acad_*`, `AcadMcp.Core/Addin/Bridge`). Proposed extras, shared where they make sense:
- `action` names the sub-operation for `view` and `export` (e.g. view `action=screenshot|zoom|select|open|activate`; export `action=csv|pdf|...`).
- `modify` takes `ops`: a JSON array string, one object per change, e.g. `[{"op":"line","from":[0,0],"to":[5000,0],"layer":"A-WALL"}]`. Whole array = one undo step. Dry run runs the ops in a transaction and rolls it back, so the preview is real validation.
- `run_script` takes `code` (C# snippet, compiled with Roslyn scripting inside the app). Globals are app objects; the script's return value and `Print(...)` output become the result text.
- Screenshots: `width` default 768 px, PNG, returned as an MCP image block.
- Bridge registers only the tools the config allows (`modify` only with allowWrite, `run_script` only with allowScripts); the add-in enforces the same config again, since it is the real gate.
- Env var `CLAUDEMCP_HOME` overrides the base folder for session.json and config.json (used by tests; default stays as above).
- AutoCAD filter keys: `Type, Layer, Block, Space, Color, Linetype, Text`; `*` wildcards, comma = OR, `~` = contains.
