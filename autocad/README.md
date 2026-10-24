# AutoCAD 2027 MCP server

Lets Claude (Claude Code or Claude Desktop) read and edit AutoCAD 2027 drawings through MCP.
It's built to keep token use low: there are 6 tools, results start with counts and come back in pages, you choose the fields, and anything big is written to a file.

```
Claude ──stdio/MCP──▶ AcadMcp.Bridge (dotnet console)
                          │  POST http://127.0.0.1:<random port>/call  + X-Mcp-Token
                          ▼
                     AcadMcp.Addin (loaded inside AutoCAD) ──▶ AutoCAD .NET API on the UI thread
```

The add-in writes `%LOCALAPPDATA%\ClaudeMcp\acad\session.json` (port + random token) when AutoCAD starts and deletes it when AutoCAD exits.
The bridge reads that file on every call, so restarting AutoCAD just works.
shared conventions with the Revit server are in [`../shared/mcp-conventions.md`](../shared/mcp-conventions.md).

## Tools

| Tool | What it does | Schema cost |
|---|---|---|
| `acad_status` | Connection, open drawings, units, layouts, counts, read/write mode | ~80 tokens |
| `acad_query` | `summary` (counts by Type/Layer/Block/Space/Color), `list` (paged pipe table), `get` (all fields for a few handles). Tables: entities, layers, blocks, layouts, xrefs, textstyles, dimstyles | ~355 |
| `acad_view` | screenshot (768 px PNG by default), zoom (extents/ids/window), select, layout, open, activate, regen | ~265 |
| `acad_export` | csv (same table/filter/fields as query), attrs (block attribute schedule, e.g. doors/windows), pdf (layouts, multi-sheet), dxf, dwg copy. Returns path + short summary | ~270 |
| `acad_modify` | Batch of ops in **one undo step**. `dry_run` defaults to true (runs then rolls back). Only listed when `allowWrite` is on | ~325 |
| `acad_run_script` | C# snippet run inside AutoCAD (Roslyn) for loops/aggregation ("code mode"). Rolled back unless `dry_run=false` and writes are allowed. Only listed when `allowScripts` is on | ~205 |

The read-only tool list is about 970 tokens, and all six together are about 1,500. `ToolListStaysUnderTokenBudget` fails the build if they grow past 3,000.

### Result shape
```
Entities: 43 (showing 1-5) next:5 · units mm
Handle|Length
2A04|50
...
```
Empty values are left out. Each result is capped at about 8k characters, and the `next:` cursor stays correct when a page is cut short.
Errors are plain sentences starting `Error:` and say how to fix the call (for example, they list the valid fields or ops).

### Filters
`Layer=A-WALL*,A-DOOR; Type=LWPOLYLINE; Text~kitchen; Space=*`. Clauses are ANDed, commas are OR, `*`/`?` are wildcards, `!=` negates, `~` means contains. Space defaults to Model.

### Modify ops
`line pline circle arc rect text mtext insert dim hatch layer set move copy rotate scale mirror erase`. For example:
```json
[{"op":"layer","name":"A-WALL","color":"1"},
 {"op":"pline","pts":[[0,0],[5000,0],[5000,3000],[0,3000]],"closed":true,"layer":"A-WALL"},
 {"op":"insert","block":"DOOR","at":[900,0],"attrs":{"MARK":"D01"}}]
```
Points are in drawing units (check `acad_status`). Unknown keys, bad points, missing layers and missing blocks are all rejected before anything is changed.

## Install (Windows, AutoCAD 2027)

Requirements: AutoCAD 2027 (not LT) and the .NET 10 SDK.

```powershell
git clone <this repo>; cd <repo>/autocad
pwsh scripts/install.ps1                 # read-only
pwsh scripts/install.ps1 -AllowWrite     # also enable acad_modify
pwsh scripts/install.ps1 -AllowWrite -AllowScripts
```
The script:
1. Publishes the add-in and bridge.
2. Copies them to `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcp.bundle`, so AutoCAD autoloads the add-in.
3. Writes `%APPDATA%\ClaudeMcp\acad\config.json`.
4. Runs `claude mcp add autocad --scope user -- dotnet <bundle>\Contents\Bridge\AcadMcp.Bridge.dll`.

Once AutoCAD starts, its command line shows `AcadMcp 0.1.0 ready for Claude on 127.0.0.1:<port>`. In AutoCAD, `CLAUDEMCP` shows the status and `CLAUDEMCPRESTART` restarts the listener.
If you don't want the bundle, run `NETLOAD` on `dist/addin/AcadMcp.Addin.dll` instead.

To use it from Claude Desktop, add this to `claude_desktop_config.json`:
```json
{ "mcpServers": { "autocad": { "command": "dotnet", "args": ["C:\\Users\\<you>\\AppData\\Roaming\\Autodesk\\ApplicationPlugins\\AcadMcp.bundle\\Contents\\Bridge\\AcadMcp.Bridge.dll"] } } }
```

### config.json
```json
{ "allowWrite": false, "allowScripts": false, "exportDir": null, "maxChars": 8000 }
```
The bridge reads this at startup to decide which tools to advertise, so restart Claude after you change it. The add-in reads it again on every call and is the real gate.

## Safety
- The add-in listens on 127.0.0.1 only and needs a 32-byte random token that changes every session. It uses a plain TcpListener, so no admin rights or URL ACLs are needed.
- It's read-only by default. With writes off, `open` opens drawings read-only.
- Each modify or script call is one transaction under one document lock, so it's one undo step (`Claude: modify` / `Claude: script`). If any op fails, nothing changes.
- If AutoCAD is busy in a command, the call returns "press Esc and retry". If the bridge times out, a queued write is dropped so it can't run later.

## Development
```
src/AcadMcp.Core     net10.0, no Autodesk refs: filters, paging, tables, op parsing, router, localhost server
src/AcadMcp.Addin    net10.0-windows AutoCAD plugin (AutoCAD.NET 26.0.0 reference assemblies = 2027 API)
src/AcadMcp.Bridge   net10.0 stdio MCP server (ModelContextProtocol 2.3.0)
tests/AcadMcp.Tests  unit tests + end-to-end tests that drive the real bridge process against a fake AutoCAD
```
`dotnet build && dotnet test` works on Windows, macOS and Linux. The add-in compiles anywhere against the reference assemblies.

### What is and isn't verified
- **Tested (56 tests)**: filters, paging, char caps, summaries, field validation, op parsing and validation, config gates (the bridge hides tools *and* the add-in refuses them), the token-checked localhost transport, stale and missing sessions, the stdio MCP handshake, text and image results, and the tool-list token budget.
- **Compiled but not yet run inside AutoCAD**: everything in `AcadMcp.Addin` (entity reading, zoom, screenshot via `CapturePreviewImage`, PDF plotting via PlotEngine, modify ops, Roslyn scripts, the autoloader bundle). The first run on a real drawing should check, in order: `acad_status` → `acad_query` summary/list → screenshot → a dry-run modify → a PDF export.
