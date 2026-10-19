namespace Sallyport.Windows.Routing;

/// <summary>Round trips in milliseconds, kept whole up to a bound and read as a whole or since the last read — a game's
/// ping as the person would see it change.</summary>
public sealed class RoundTrips
{
    private const int Most = 1 << 20;

    private readonly Lock _lock = new();
    private readonly List<double> _samples = [];
    private int _read;

    public void Add(double milliseconds)
    {
        lock (_lock)
            if (_samples.Count < Most) _samples.Add(milliseconds);
    }

    public RoundTripSummary All()
    {
        lock (_lock) return RoundTripSummary.Of(_samples);
    }

    /// <summary>The samples since the last call to this.</summary>
    public RoundTripSummary Recent()
    {
        lock (_lock)
        {
            var recent = RoundTripSummary.Of(_samples.Skip(_read));
            _read = _samples.Count;
            return recent;
        }
    }
}

public readonly record struct RoundTripSummary(int Count, double Min, double P50, double P95, double Max)
{
    public static RoundTripSummary Of(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return sorted.Length == 0 ? default
            : new(sorted.Length, sorted[0], sorted[sorted.Length / 2], sorted[Math.Min(sorted.Length - 1, sorted.Length * 95 / 100)], sorted[^1]);
    }

    public override string ToString() => Count == 0 ? "none" : $"p50 {P50:F0} ms (min {Min:F0}, p95 {P95:F0}, max {Max:F0}, n {Count})";
}
