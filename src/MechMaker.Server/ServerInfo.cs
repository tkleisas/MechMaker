namespace MechMaker.Server;

/// <summary>
/// M4/M5 home of the host-facing seams:
/// - a REST/IPC host exposing the headless engine API (load, run, step, report), and
/// - an MCP server so LLM agents can assemble, wire, validate, and simulate machines.
/// Stub in M0.
/// </summary>
public static class ServerInfo
{
    public const string Name = "MechMaker.Server";
    public const string Version = "0.1.0";
}
