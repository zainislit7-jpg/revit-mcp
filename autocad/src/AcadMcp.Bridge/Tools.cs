using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadMcp.Bridge;

// Descriptions are deliberately short: every word here is paid for on each request.
// Budget is enforced by ToolBudgetTests.

[McpServerToolType]
public sealed class ReadTools(AddinClient addin)
{
    [McpServerTool(Name = "acad_status", ReadOnly = true)]
    [Description("AutoCAD connection, open drawings, units, layouts, counts, write/script mode. Call first.")]
    public Task<CallToolResult> Status(
        [Description("Drawing name if not the active one")] string? doc = null,
        CancellationToken ct = default) =>
        addin.Call("status", new() { ["doc"] = doc }, ct);

    [McpServerTool(Name = "acad_query", ReadOnly = true)]
    [Description("Read drawing data. Start with mode=summary (counts), then list (paged table), then get (all fields for a few handles). Lengths are in drawing units.")]
    public Task<CallToolResult> Query(
        [Description("summary (default) | list | get")] string? mode = null,
        [Description("entities (default) | layers | blocks | layouts | xrefs | textstyles | dimstyles")] string? table = null,
        [Description("e.g. Layer=A-WALL*,A-DOOR; Type=LWPOLYLINE; Text~kitchen; Space=* (default Model)")] string? filter = null,
        [Description("Comma list or *. Entity default Handle,Type,Layer; also Color,Linetype,Block,Text,Length,Area,Closed,Start,End,Center,Radius,Pos,Rot,Height,Bounds,Attrs,Space")] string? fields = null,
        [Description("summary grouping: Type (default), Layer, Block, Space, Color")] string? group = null,
        [Description("Handles for mode=get, comma list")] string? ids = null,
        [Description("Rows per page, default 50, max 500")] int? limit = null,
        [Description("Number after next: from the previous page")] string? cursor = null,
        [Description("Drawing name if not the active one")] string? doc = null,
        CancellationToken ct = default) =>
        addin.Call("query", new()
        {
            ["mode"] = mode, ["table"] = table, ["filter"] = filter, ["fields"] = fields, ["group"] = group,
            ["ids"] = ids, ["limit"] = limit, ["cursor"] = cursor, ["doc"] = doc,
        }, ct);

    [McpServerTool(Name = "acad_view", Idempotent = false)]
    [Description("Look at or navigate the drawing. screenshot returns a small PNG; zoom/select/layout can add shot=true.")]
    public Task<CallToolResult> View(
        [Description("screenshot | zoom | select | layout | open | activate | regen")] string action,
        [Description("zoom: extents (default) | ids | window")] string? target = null,
        [Description("Handles for zoom/select")] string? ids = null,
        [Description("zoom window x1,y1,x2,y2")] string? window = null,
        [Description("Layout name (layout), drawing name (activate) or file path (open)")] string? name = null,
        [Description("Screenshot width px, default 768")] int? width = null,
        [Description("Also return a screenshot")] bool? shot = null,
        [Description("Drawing name if not the active one")] string? doc = null,
        CancellationToken ct = default) =>
        addin.Call("view", new()
        {
            ["action"] = action, ["target"] = target, ["ids"] = ids, ["window"] = window, ["name"] = name,
            ["width"] = width, ["shot"] = shot, ["doc"] = doc,
        }, ct);

    [McpServerTool(Name = "acad_export", ReadOnly = true)]
    [Description("Write big outputs to a file and return the path plus a short summary. csv = same table/filter/fields as query; attrs = block attribute schedule; pdf = plot layouts; dxf/dwg = copy of drawing.")]
    public Task<CallToolResult> Export(
        [Description("csv | attrs | pdf | dxf | dwg")] string action,
        [Description("csv: entities (default) | layers | blocks | layouts | xrefs | textstyles | dimstyles")] string? table = null,
        [Description("Same syntax as query, e.g. Block=DOOR*")] string? filter = null,
        [Description("csv columns, comma list or *")] string? fields = null,
        [Description("pdf: layout names comma list, * for all, default current")] string? layouts = null,
        [Description("Output file path; default exports folder")] string? path = null,
        [Description("Drawing name if not the active one")] string? doc = null,
        CancellationToken ct = default) =>
        addin.Call("export", new()
        {
            ["action"] = action, ["table"] = table, ["filter"] = filter, ["fields"] = fields,
            ["layouts"] = layouts, ["path"] = path, ["doc"] = doc,
        }, ct);
}

[McpServerToolType]
public sealed class WriteTools(AddinClient addin)
{
    [McpServerTool(Name = "acad_modify", Destructive = true)]
    [Description("""
        Change the drawing. ops = JSON array, all applied as one undo step. dry_run (default true) validates then rolls back.
        Ops: line{from,to} pline{pts,closed?,width?} circle{center,r} arc{center,r,start,end deg} rect{p1,p2} text{at,text,h?,rot?} mtext{at,text,h?,w?} insert{block,at,scale?,rot?,attrs?} dim{from,to,at,rot?} hatch{ids|pts,pattern?,scale?} layer{name,color?,linetype?,on?,frozen?,locked?,plot?,current?} set{ids,layer?,color?,linetype?,text?,attrs?} move{ids,by} copy{ids,by} rotate{ids,base,deg} scale{ids,base,f} mirror{ids,p1,p2,keep?} erase{ids}.
        Creates also take layer?,color?,linetype?. Points [x,y] in drawing units. Example: [{"op":"line","from":[0,0],"to":[5000,0],"layer":"A-WALL"}]
        """)]
    public Task<CallToolResult> Modify(
        [Description("JSON array of ops")] string ops,
        [Description("Default true: validate and roll back. false = apply")] bool? dry_run = null,
        [Description("Model (default) or a layout name for new entities")] string? space = null,
        [Description("Drawing name if not the active one")] string? doc = null,
        CancellationToken ct = default) =>
        addin.Call("modify", new() { ["ops"] = ops, ["dry_run"] = dry_run, ["space"] = space, ["doc"] = doc }, ct);
}

[McpServerToolType]
public sealed class ScriptTools(AddinClient addin)
{
    [McpServerTool(Name = "acad_run_script", Destructive = true)]
    [Description("""
        Run a C# snippet inside AutoCAD for loops/aggregation; return only the answer. Globals: Doc, Db, Ed, Tr (open transaction), Model (model space entities), Print(x). Namespaces DatabaseServices, Geometry, ApplicationServices, EditorInput and System.Linq are imported. Return value is printed.
        Rolled back unless dry_run=false and writes are allowed. Example: return Model.OfType<Line>().Sum(l => l.Length);
        """)]
    public Task<CallToolResult> RunScript(
        [Description("C# statements")] string code,
        [Description("Default true: roll back changes")] bool? dry_run = null,
        [Description("Drawing name if not the active one")] string? doc = null,
        CancellationToken ct = default) =>
        addin.Call("run_script", new() { ["code"] = code, ["dry_run"] = dry_run, ["doc"] = doc }, ct);
}
