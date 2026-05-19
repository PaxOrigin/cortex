// ─────────────────────────────────────────────────────────────────────────────
//  ELICITATION TOOLS
//  Add this class to McpHttpServer/Program.cs
//
//  Elicitation is a server → client round trip that happens MID-TOOL.
//  The tool pauses, sends a schema to the client, the client collects
//  user input and returns it, then the tool resumes with that data.
//
//  This is the correct pattern for:
//    - Confirming destructive operations
//    - Collecting missing parameters at runtime
//    - Multi-step guided workflows
//
//  The server MUST be stateful (WithHttpTransport() default).
//  The client MUST declare Elicitation capability in McpClientOptions.
// ─────────────────────────────────────────────────────────────────────────────

using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public static class ElicitationTools
{
    // ── DEPLOY TOOL — two-step elicitation ────────────────────────────────
    // Step 1: ask which environment + version (string + enum)
    // Step 2: if production, require explicit boolean confirmation
    //
    // This demonstrates the real-world pattern:
    //   structured form input → conditional follow-up → proceed or abort

    [McpServerTool(ReadOnly = false, Destructive = true),
     Description("Deploys a service to an environment. " +
                 "Elicits environment and version from the user, " +
                 "then requires explicit confirmation for production deployments.")]
    public static async Task<string> DeployService(
        McpServer server,
        [Description("The name of the service to deploy")] string service,
        CancellationToken ct)
    {
        // ── Capability guard ───────────────────────────────────────────────
        // Always check before eliciting — not all clients support it.
        // Throw McpException to return IsError=true (recoverable by LLM).
        if (server.ClientCapabilities?.Elicitation is null)
            throw new McpException(
                "This tool requires elicitation support. " +
                "Connect with a client that declares ElicitationCapability.");

        // ── Step 1: collect environment + version ──────────────────────────
        var configSchema = new ElicitRequestParams.RequestSchema
        {
            Properties =
            {
                ["environment"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                {
                    Description = "Target deployment environment",
                    Enum        = ["development", "staging", "production"]
                },
                ["version"] = new ElicitRequestParams.StringSchema
                {
                    Description = "Version tag to deploy (e.g. v1.2.3 or 'latest')",
                    MinLength   = 1
                }
            }
        };

        var configResult = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = $"Configure deployment for service '{service}'",
            RequestedSchema = configSchema
        }, ct);

        // Handle all three user actions
        if (configResult.Action == "cancel")
            return "Deployment cancelled — user dismissed the form.";

        if (configResult.Action != "accept" || configResult.Content is null)
            return "Deployment declined — user chose not to provide configuration.";

        var environment = configResult.Content["environment"].GetString() ?? "development";
        var version = configResult.Content.TryGetValue("version", out var v)
            ? v.GetString() ?? "latest"
            : "latest";

        // ── Step 2: extra confirmation for production ──────────────────────
        // Only destructive targets get the extra gate.
        // This is the canonical elicitation pattern for safety-critical ops.
        if (environment == "production")
        {
            var confirmSchema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["confirmed"] = new ElicitRequestParams.BooleanSchema
                    {
                        Description =
                            $"Type 'true' to confirm deploying {service} {version} " +
                            $"to PRODUCTION. This cannot be undone."
                    }
                }
            };

            var confirmResult = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "⚠️  PRODUCTION deployment — explicit confirmation required",
                RequestedSchema = confirmSchema
            }, ct);

            var confirmed = confirmResult.Action == "accept" &&
                            confirmResult.Content is not null &&
                            confirmResult.Content.TryGetValue("confirmed", out var c) &&
                            c.ValueKind == JsonValueKind.True;

            if (!confirmed)
                return $"Production deployment of '{service}' aborted — " +
                       $"user did not confirm (action: {confirmResult.Action}).";
        }

        // ── Simulate deployment ────────────────────────────────────────────
        await Task.Delay(300, ct);  // simulate work

        return $"✓ Deployed '{service}' version '{version}' " +
               $"to '{environment}' at {DateTime.UtcNow:HH:mm:ss} UTC.";
    }

    // ── REPORT GENERATOR — demonstrates string + boolean schema together ──
    // A simpler single-step elicitation: ask for report parameters,
    // then generate the report with the user-provided values.

    [McpServerTool(ReadOnly = true),
     Description("Generates a server report. Elicits report format and time window from user.")]
    public static async Task<string> GenerateReport(
        McpServer server,
        LiveMetricsService metrics,
        CancellationToken ct)
    {
        if (server.ClientCapabilities?.Elicitation is null)
            return GenerateDefaultReport(metrics);  // graceful fallback

        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties =
            {
                ["format"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                {
                    Description = "Report output format",
                    Enum        = ["summary", "detailed", "json"]
                },
                ["includeHistory"] = new ElicitRequestParams.BooleanSchema
                {
                    Description = "Include historical trend data"
                }
            }
        };

        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = "Configure your server report",
            RequestedSchema = schema
        }, ct);

        if (result.Action != "accept" || result.Content is null)
            return GenerateDefaultReport(metrics);

        var format = result.Content.TryGetValue("format", out var f)
            ? f.GetString() ?? "summary" : "summary";
        var includeHistory = result.Content.TryGetValue("includeHistory", out var h) &&
                             h.ValueKind == JsonValueKind.True;

        var m = metrics.GetCurrent();

        return format switch
        {
            "json" => JsonSerializer.Serialize(new
            {
                generatedAt = DateTime.UtcNow,
                cpu = m.CpuPercent,
                memory = m.MemoryMb,
                requests = m.RequestCount,
                uptime = m.Uptime,
                history = includeHistory ? "trend data would appear here" : null
            }, new JsonSerializerOptions { WriteIndented = true }),

            "detailed" => $"DETAILED SERVER REPORT — {DateTime.UtcNow:o}\n" +
                          $"CPU:      {m.CpuPercent}%\n" +
                          $"Memory:   {m.MemoryMb} MB\n" +
                          $"Requests: {m.RequestCount}\n" +
                          $"Uptime:   {m.Uptime}\n" +
                          (includeHistory ? "History:  [trend data]\n" : ""),

            _ => $"Server OK — CPU {m.CpuPercent}%, " +
                          $"Memory {m.MemoryMb}MB, Uptime {m.Uptime}"
        };
    }

    private static string GenerateDefaultReport(LiveMetricsService metrics)
    {
        var m = metrics.GetCurrent();
        return $"Server OK — CPU {m.CpuPercent}%, Memory {m.MemoryMb}MB";
    }
}