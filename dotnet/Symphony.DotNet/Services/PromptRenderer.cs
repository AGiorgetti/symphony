using System.Text.RegularExpressions;
using Symphony.DotNet.Models;

namespace Symphony.DotNet.Services;

internal sealed class PromptRenderer
{
    private static readonly Regex VariablePattern = new("\\{\\{\\s*([^}]+?)\\s*\\}\\}", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex IfPattern = new("\\{%\\s*if\\s+(.+?)\\s*%\\}(.*?)((\\{%\\s*else\\s*%\\}(.*?))?)\\{%\\s*endif\\s*%\\}", RegexOptions.Compiled | RegexOptions.Singleline);

    public string Render(string template, IssueRecord issue, int? attempt)
    {
        try
        {
            var context = BuildContext(issue, attempt);
            return RenderBlock(template, context).Trim();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"template_render_error: {ex.Message}", ex);
        }
    }

    public string RenderContinuationPrompt(int turnNumber, int maxTurns)
    {
        return $"""
Continuation guidance:

- The previous Codex turn completed normally, but the Linear issue is still in an active state.
- This is continuation turn #{turnNumber} of {maxTurns} for the current agent run.
- Resume from the current workspace and workpad state instead of restarting from scratch.
- The original task instructions and prior turn context are already present in this thread, so do not restate them before acting.
- Focus on the remaining ticket work and do not end the turn while the issue stays active unless you are truly blocked.
""".Trim();
    }

    private static Dictionary<string, object?> BuildContext(IssueRecord issue, int? attempt)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["attempt"] = attempt,
            ["issue"] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = issue.Id,
                ["identifier"] = issue.Identifier,
                ["title"] = issue.Title,
                ["description"] = issue.Description,
                ["priority"] = issue.Priority,
                ["state"] = issue.State,
                ["branch_name"] = issue.BranchName,
                ["branchName"] = issue.BranchName,
                ["url"] = issue.Url,
                ["labels"] = issue.Labels,
                ["blocked_by"] = issue.BlockedBy.Select(blocker => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = blocker.Id,
                    ["identifier"] = blocker.Identifier,
                    ["state"] = blocker.State
                }).ToList(),
                ["created_at"] = issue.CreatedAt?.ToString("O"),
                ["updated_at"] = issue.UpdatedAt?.ToString("O")
            }
        };
    }

    private static string RenderBlock(string template, IReadOnlyDictionary<string, object?> context)
    {
        var rendered = RenderConditionals(template, context);
        rendered = VariablePattern.Replace(rendered, match => RenderVariable(match.Groups[1].Value, context));

        if (rendered.Contains("{%", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("template_parse_error: unsupported template tag");
        }

        return rendered;
    }

    private static string RenderConditionals(string template, IReadOnlyDictionary<string, object?> context)
    {
        while (IfPattern.IsMatch(template))
        {
            template = IfPattern.Replace(template, match =>
            {
                var expression = match.Groups[1].Value.Trim();
                var truthy = IsTruthy(ResolvePath(expression, context));
                var chosen = truthy ? match.Groups[2].Value : match.Groups[5].Value;
                return RenderBlock(chosen, context);
            });
        }

        return template;
    }

    private static string RenderVariable(string expression, IReadOnlyDictionary<string, object?> context)
    {
        var value = ResolvePath(expression.Trim(), context);
        return ValueToString(value);
    }

    private static object? ResolvePath(string expression, IReadOnlyDictionary<string, object?> context)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new InvalidOperationException("template_render_error: empty expression");
        }

        object? current = context;
        foreach (var segment in expression.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is IReadOnlyDictionary<string, object?> dictionary)
            {
                if (!dictionary.TryGetValue(segment, out current))
                {
                    throw new InvalidOperationException($"template_render_error: unknown variable '{expression}'");
                }
            }
            else
            {
                throw new InvalidOperationException($"template_render_error: unknown variable '{expression}'");
            }
        }

        return current;
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            null => false,
            bool boolean => boolean,
            string text => !string.IsNullOrWhiteSpace(text),
            int number => number != 0,
            long number => number != 0,
            System.Collections.ICollection collection => collection.Count > 0,
            _ => true
        };
    }

    private static string ValueToString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            IEnumerable<string> strings => string.Join(", ", strings),
            IEnumerable<object?> objects => string.Join(", ", objects.Select(ValueToString).Where(item => !string.IsNullOrWhiteSpace(item))),
            DateTimeOffset timestamp => timestamp.ToString("O"),
            _ => value.ToString() ?? string.Empty
        };
    }
}



