# Revit 2027 MCP server

Lets Claude read and (when you allow it) change the model open in Revit 2027, with as few
tokens as possible. Built to the shared conventions in `../shared/mcp-conventions.md`, which the
AutoCAD server follows too, so both can ship in one Claude plugin.

```
Claude ──stdio/MCP──▶ RevitMcp.Bridge ──HTTP 127.0.0.1 + token──▶ RevitMcp.Addin (inside Revit)
```

| Project | What it is |
|---|---|
| `src/RevitMcp.Addin` | Revit add-in (net10.0-windows). Localhost listener; runs every call on Revit's API thread through one `ExternalEvent`. |
| `src/RevitMcp.Bridge` | MCP server over stdio (official C# SDK). Thin: forwards calls, turns PNGs into image blocks. |
| `src/RevitMcp.Core` | No Revit dependency: transport, paging, filters, table output, op parsing. |
| `tests/RevitMcp.Tests` | Unit tests, transport tests, and end-to-end bridge tests against a fake add-in. |

## Tools

| Tool | Does |
|---|---|
| `revit_status` | Version, model, active view, selection, whether writes/scripts are on. |
| `revit_query` | `mode=summary` counts per category; `mode=list` paged rows of one category with `fields`, `filter`, `scope`; `mode=get` all parameters of `ids`. |
| `revit_view` | `screenshot` (PNG, 768 px default), `activate`, `select`, `zoom`. |
| `revit_export` | `csv`, `schedule`, `pdf`, `dwg`, `image` to a file; returns only the path and a count. |
| `revit_modify` | `ops` JSON array. Edit: set, move, copy, rotate, type, delete, pin, unpin. Create: wall, floor, door, window, room, level. One transaction = one undo. `dry_run` defaults to true. Lengths are plain numbers in the model's units (or `units=ft/in/mm/cm/m`), or text Revit parses, like `2'-6"` or `1500 mm`. |
| `revit_run_script` | C# against the Revit API (Roslyn). Loop and filter inside Revit, return only the answer. Rolled back unless `commit=true`. |

`revit_modify` and `revit_run_script` are only listed when enabled in the config.

### How it keeps token use down
- Six macro tools; the whole tool list is ~1.2k tokens (test-enforced budget: 3k, snapshot in `tests/RevitMcp.Tests/Snapshots/tools.json`).
- Results are a one-line summary plus a pipe table, empty columns dropped, numbers rounded.
- Counts first (`summary`), then pages of 50 with `next:` cursors; `fields` picks columns.
- Unfiltered lists read parameters only for the page shown.
- Hard cap of 8,000 characters per result with a hint to narrow or export.
- Anything big goes to a file via `revit_export`.
- `run_script` is the "code mode" escape hatch for multi-step work.

## Safety
- Listens on `127.0.0.1` only, random port, random 32-byte token in `%LOCALAPPDATA%\ClaudeMcp\revit\session.json`; requests with an `Origin` header (browsers) are refused.
- Read-only by default. `%APPDATA%\ClaudeMcp\revit\config.json`:
  ```json
  { "allowWrite": false, "allowScripts": false, "maxChars": 8000, "exportDir": null }
  ```
  The add-in re-reads it on every call. The bridge reads it at start to decide which tools to list, so restart Claude after changing it.
- Each modify call is one transaction named `Claude: ...`, so one Ctrl+Z undoes it. If any op fails, nothing is kept. Revit warnings are reported instead of popping up dialogs.
- Scripts always run in a transaction that is rolled back unless writes are on and `commit=true`.

## Install (Windows, Revit 2027, .NET 10 SDK)
```powershell
./install.ps1
claude mcp add revit --scope user -- dotnet "%LOCALAPPDATA%\ClaudeMcp\revit\bridge\RevitMcp.Bridge.dll"
```
Start Revit and choose "Always load" for the Claude MCP add-in.

## Develop
```bash
dotnet build      # the add-in compiles on any OS against Nice3point Revit 2027 reference packages
dotnet test       # UPDATE_SNAPSHOTS=1 dotnet test to accept a deliberate tool-schema change
```

## Status
- Compiled: all four projects, against Revit API 2027.3 reference assemblies.
- Tested: Core logic, the localhost transport, and the real bridge over stdio against a fake add-in (61 tests).
- Not yet tested inside Revit: everything in `src/RevitMcp.Addin` (query, view, export, modify, script handlers), `install.ps1`.
