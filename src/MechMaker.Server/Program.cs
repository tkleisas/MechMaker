using MechMaker.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MechMaker M4: MCP server. LLM agents assemble, wire, validate, compile, and
// simulate machines through the tools in MachineTools.cs.
//
//   stdio (default):  dotnet run --project src/MechMaker.Server
//   streamable HTTP:  dotnet run --project src/MechMaker.Server -- --http [port]
//                     (default port 8080; the MCP endpoint is /mcp)

if (args.Contains("--http"))
{
    var port = 8080;
    var httpIndex = Array.IndexOf(args, "--http");
    if (httpIndex >= 0 && httpIndex + 1 < args.Length && int.TryParse(args[httpIndex + 1], out var requested))
        port = requested;

    var web = WebApplication.CreateBuilder(args);

    // stdout stays free for whoever wants it; logs to stderr like stdio mode.
    web.Logging.ClearProviders();
    web.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    web.WebHost.UseUrls($"http://0.0.0.0:{port}");
    web.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = web.Build();
    app.MapMcp("/mcp"); // streamable HTTP at /mcp (the SDK's parameterless overload maps the root)

    // The human door: the three.js viewer + the scene it renders.
    app.MapGet("/", () => Results.Content(WebUi.Html, "text/html"));
    app.MapGet("/scene", () => Results.Text(MachineTools.SceneJson(), "application/json"));

    app.Run();
    return;
}

// stdio transport (the agent default).
var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP stdio protocol — every log goes to stderr instead.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();