namespace Symphony.DotNet.Services;

internal sealed record CliOptions(string WorkflowPath, int? Port, bool GuardrailsAcknowledged)
{
    public static CliOptions Parse(string[] args)
    {
        var workflowPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "WORKFLOW.md"));
        int? port = null;
        var acknowledged = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--i-understand-that-this-will-be-running-without-the-usual-guardrails":
                    acknowledged = true;
                    break;
                case "--port":
                    if (index + 1 >= args.Length || !int.TryParse(args[++index], out var parsedPort) || parsedPort < 0)
                    {
                        throw new InvalidOperationException("Invalid --port value.");
                    }

                    port = parsedPort;
                    break;
                default:
                    if (!args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        workflowPath = Path.GetFullPath(args[index]);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unknown argument: {args[index]}");
                    }
                    break;
            }
        }

        return new CliOptions(workflowPath, port, acknowledged);
    }

    public static string AcknowledgementBanner() => string.Join(Environment.NewLine, [
        "This Symphony implementation is a low key engineering preview.",
        "Codex may run without the usual guardrails.",
        "To proceed, start with:",
        "  --i-understand-that-this-will-be-running-without-the-usual-guardrails"
    ]);
}
