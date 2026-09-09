using MechMaker.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MechMaker M4: MCP server over stdio. LLM agents assemble, wire, validate,
// compile, and simulate machines through the tools in MachineTools.cs.
// Run: dotnet run --project src/MechMaker.Server

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP stdio protocol — every log goes to stderr instead.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
