using System.Globalization;

namespace Sallyport.Observer;

public sealed record LearnOptions(IReadOnlyList<string> Executables, int Seconds, string? JsonPath)
{
    public static LearnOptions Parse(string[] args)
    {
        const string usage = "Usage: Sallyport.Observer learn --exe <full path>... --for <1..3600 s> [--json <file>]";
        if (args.Length == 0 || args[0] != "learn") throw new ArgumentException(usage);
        List<string> paths = [];
        int? seconds = null;
        string? json = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--exe":
                    int before = paths.Count;
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        string path = args[++i];
                        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("--exe requires a full path.");
                        paths.Add(Path.GetFullPath(path));
                    }
                    if (paths.Count == before) throw new ArgumentException(usage);
                    break;
                case "--for":
                    if (seconds is not null || ++i == args.Length ||
                        !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 3600)
                        throw new ArgumentException("--for must occur once with an integer from 1 to 3600.");
                    seconds = value;
                    break;
                case "--json":
                    if (json is not null || ++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException(usage);
                    json = Path.GetFullPath(args[i]);
                    break;
                default: throw new ArgumentException(usage);
            }
        }
        if (paths.Count == 0 || seconds is null) throw new ArgumentException(usage);
        if (json is not null && paths.Contains(json, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The JSON destination must differ from the executable paths.");
        return new(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), seconds.Value, json);
    }
}
