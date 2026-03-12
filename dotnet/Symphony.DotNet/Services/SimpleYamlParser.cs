namespace Symphony.DotNet.Services;

internal static class SimpleYamlParser
{
    public static Dictionary<string, object?> ParseMap(string yaml)
    {
        var parser = new Parser(yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        return parser.ParseMap(0);
    }

    private sealed class Parser(string[] lines)
    {
        private int _index;

        public Dictionary<string, object?> ParseMap(int indent)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            while (_index < lines.Length)
            {
                if (SkipBlankLines())
                {
                    continue;
                }

                var line = lines[_index];
                var currentIndent = CountIndent(line);
                if (currentIndent < indent)
                {
                    break;
                }

                if (currentIndent > indent)
                {
                    throw new InvalidOperationException($"workflow_parse_error: unexpected indentation on line {_index + 1}");
                }

                var trimmed = line.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    throw new InvalidOperationException($"workflow_parse_error: invalid mapping on line {_index + 1}");
                }

                var key = trimmed[..colon].Trim();
                var remainder = trimmed[(colon + 1)..].TrimStart();
                _index++;

                if (remainder == "|")
                {
                    map[key] = ParseLiteralBlock(indent + 2);
                    continue;
                }

                if (remainder.Length > 0)
                {
                    map[key] = ParseScalar(remainder);
                    continue;
                }

                SkipBlankLines();
                if (_index >= lines.Length)
                {
                    map[key] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    break;
                }

                var nextIndent = CountIndent(lines[_index]);
                if (nextIndent <= indent)
                {
                    map[key] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                map[key] = lines[_index].TrimStart().StartsWith("- ", StringComparison.Ordinal)
                    ? ParseList(nextIndent)
                    : ParseMap(nextIndent);
            }

            return map;
        }

        private List<object?> ParseList(int indent)
        {
            var list = new List<object?>();
            while (_index < lines.Length)
            {
                if (SkipBlankLines())
                {
                    continue;
                }

                var line = lines[_index];
                var currentIndent = CountIndent(line);
                if (currentIndent < indent)
                {
                    break;
                }

                if (currentIndent != indent || !line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"workflow_parse_error: invalid list item on line {_index + 1}");
                }

                var remainder = line.TrimStart()[2..].TrimStart();
                _index++;
                list.Add(ParseScalar(remainder));
            }

            return list;
        }

        private string ParseLiteralBlock(int indent)
        {
            var output = new List<string>();
            while (_index < lines.Length)
            {
                var line = lines[_index];
                if (!string.IsNullOrWhiteSpace(line) && CountIndent(line) < indent)
                {
                    break;
                }

                output.Add(string.IsNullOrWhiteSpace(line)
                    ? string.Empty
                    : line.Length >= indent ? line[indent..] : string.Empty);
                _index++;
            }

            return string.Join('\n', output).TrimEnd();
        }

        private bool SkipBlankLines()
        {
            var skipped = false;
            while (_index < lines.Length && string.IsNullOrWhiteSpace(lines[_index]))
            {
                skipped = true;
                _index++;
            }

            return skipped;
        }

        private static int CountIndent(string line)
        {
            var count = 0;
            while (count < line.Length && line[count] == ' ')
            {
                count++;
            }

            return count;
        }

        private static object? ParseScalar(string value)
        {
            if (int.TryParse(value, out var number))
            {
                return number;
            }

            if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                return value[1..^1];
            }

            return value;
        }
    }
}
