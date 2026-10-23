namespace AcadMcp.Core;

/// <summary>An expected failure with a message written for the model (bad args, missing ids, busy app).</summary>
public sealed class ToolError(string message) : Exception(message);

/// <summary>What a tool hands back: text, plus an optional PNG.</summary>
public sealed record ToolOutput(string Text, byte[]? Png = null);
