using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RevitMcp.Core;

namespace RevitMcp.Bridge;

/// <summary>
/// Tool schemas are part of every request's prompt, so descriptions are short and every
/// argument is a flat string/number/bool. ToolSchemaTests snapshots them and enforces a budget.
/// </summary>
internal static class Forward
{
    public static AddinClient Client { get; set; } = new();

    public static async Task<CallToolResult> Call(string tool, CancellationToken ct, params (string Key, object? Value)[] args)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in args)
            if (v is not null && !(v is string s && s.Length == 0))
                obj[k] = JsonValue.Create(v);

        var r = await Client.CallAsync(tool, obj, ct);
        if (!r.Ok)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = r.Error ?? "Error" }] };

        var content = new List<ContentBlock>();
        if (r.Image is { Length: > 0 })
            content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(r.Image), "image/png"));
        if (!string.IsNullOrEmpty(r.Text) || content.Count == 0)
            content.Add(new TextContentBlock { Text = r.Text ?? "" });
        return new CallToolResult { Content = content };
    }
}

[McpServerToolType]
public static class ReadTools
{
    [McpServerTool(Name = "revit_status", ReadOnly = true, Idempotent = true)]
    [Description("Revit version, open models, active view, selection, and whether modify/run_script are enabled. Call once first.")]
    public static Task<CallToolResult> Status(CancellationToken ct) => Forward.Call("status", ct);

    [McpServerTool(Name = "revit_query", ReadOnly = true, Idempotent = true)]
    [Description("Read model data as a pipe table. mode=summary: counts per category (start here). mode=list: rows of one category, paged. mode=get: every parameter of the given ids. Ask only for the fields you need.")]
    public static Task<CallToolResult> Query(
        CancellationToken ct,
        [Description("summary|list|get (default summary)")] string? mode = null,
        [Description("Category name, e.g. Walls, Doors, Rooms, Sheets, Views, Levels")] string? category = null,
        [Description("true = element types instead of instances")] bool types = false,
        [Description("AND-ed clauses: Field=Value; Field~text; Field>num. e.g. Level=Level 1; Width>900")] string? filter = null,
        [Description("Comma list of columns (any parameter name). Default per category")] string? fields = null,
        [Description("Comma list of element ids (get, or restrict list)")] string? ids = null,
        [Description("model|view|selection (default model)")] string? scope = null,
        [Description("Rows per page, default 50, max 500")] int? limit = null,
        [Description("Value of next: from the previous page")] string? cursor = null) =>
        Forward.Call("query", ct, ("mode", mode), ("category", category), ("types", types ? true : null),
            ("filter", filter), ("fields", fields), ("ids", ids), ("scope", scope), ("limit", limit), ("cursor", cursor));

    [McpServerTool(Name = "revit_view", Idempotent = true)]
    [Description("See and drive the UI. screenshot: PNG of a view. activate: open a view. select: select ids. zoom: zoom to ids.")]
    public static Task<CallToolResult> View(
        CancellationToken ct,
        [Description("screenshot|activate|select|zoom (default screenshot)")] string? action = null,
        [Description("View id or exact name (default active view)")] string? view = null,
        [Description("Comma list of element ids (select, zoom)")] string? ids = null,
        [Description("Screenshot width px, default 768, max 1600")] int? width = null) =>
        Forward.Call("view", ct, ("action", action), ("view", view), ("ids", ids), ("width", width));

    [McpServerTool(Name = "revit_export", Idempotent = true)]
    [Description("Write data to a file and return only its path and a count. Use for anything bigger than a page. csv: query rows (same args as revit_query list). schedule: a schedule view to csv. pdf|dwg: views or sheets. image: full-size PNG.")]
    public static Task<CallToolResult> Export(
        CancellationToken ct,
        [Description("csv|schedule|pdf|dwg|image")] string action,
        [Description("csv: category to export")] string? category = null,
        [Description("csv: true = types")] bool types = false,
        [Description("csv: filter, as in revit_query")] string? filter = null,
        [Description("csv: comma list of columns")] string? fields = null,
        [Description("csv: model|view|selection")] string? scope = null,
        [Description("schedule/pdf/dwg/image: comma list of view or sheet ids or names")] string? views = null,
        [Description("Output folder or file path (default Documents\\ClaudeMcp\\exports)")] string? path = null) =>
        Forward.Call("export", ct, ("action", action), ("category", category), ("types", types ? true : null),
            ("filter", filter), ("fields", fields), ("scope", scope), ("views", views), ("path", path));
}

[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(Name = "revit_modify", Destructive = true)]
    [Description("""
        Change the model. ops is a JSON array; the whole array is one undo step. dry_run (default true) applies then rolls back, so check its report before running for real.
        Lengths: numbers in units (default: the model's own, e.g. feet), or text like "2'-6\"" or "1500 mm".
        Ops: {"op":"set","ids":[1],"param":"Mark","value":"A1"} value as typed in Revit | {"op":"move","ids":[1],"by":[x,y,z]} | {"op":"copy","ids":[1],"by":[x,y,z]} | {"op":"rotate","ids":[1],"deg":90} about each element's center | {"op":"type","ids":[1],"to":"type id or name"} | {"op":"delete","ids":[1]} | {"op":"pin"|"unpin","ids":[1]}
        """)]
    public static Task<CallToolResult> Modify(
        CancellationToken ct,
        [Description("JSON array of ops")] string ops,
        [Description("default true")] bool dry_run = true,
        [Description("project|ft|in|mm|cm|m for plain numbers (default project)")] string? units = null) =>
        Forward.Call("modify", ct, ("ops", ops), ("dry_run", dry_run), ("units", units));
}

[McpServerToolType]
public static class ScriptTools
{
    [McpServerTool(Name = "revit_run_script", Destructive = true)]
    [Description("Run C# against the Revit API when other tools can't. Globals: doc, uidoc, app, Print(object). Return a value or Print; do the looping and filtering in the script and return only a short result. Runs inside one open transaction: don't start your own. Rolled back unless commit=true (needs writes enabled).")]
    public static Task<CallToolResult> RunScript(
        CancellationToken ct,
        [Description("C# script body")] string code,
        [Description("Keep changes as one undo step, default false")] bool commit = false) =>
        Forward.Call("run_script", ct, ("code", code), ("commit", commit));
}
