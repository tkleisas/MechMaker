namespace MechMaker.Server;

/// <summary>
/// M4/M5 host-facing seams:
/// - an MCP server (Program.cs + MachineTools.cs) so LLM agents can assemble, wire,
///   validate, and simulate machines through tools, and
/// - a future REST/IPC host exposing the headless engine API (load, run, step, report).
/// </summary>
public static class ServerInfo
{
    public const string Name = "MechMaker.Server";
    public const string Version = "0.1.0";
}
