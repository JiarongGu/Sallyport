using System.Net;

/// <summary>A public address as a measurement may print it (AGENTS.md rule 2: the output goes into commits), the VPN's
/// egress above all: its last part masked. This side's own networks are named as they are. Sallyport.Windows has the
/// same for the router; this tool runs where that does not.</summary>
internal static class Addresses
{
    public static string Masked(IPAddress address) =>
        address.GetAddressBytes() is [var a, var b, var c, _] && !(a is 10 or 127 || a == 172 && b is >= 16 and <= 31 || a == 192 && b == 168 || a == 100 && b is >= 64 and <= 127)
            ? $"{a}.{b}.{c}.x"
            : address.ToString();

    /// <summary>An address a server wrote back, masked; anything that is not one, said as such rather than printed.</summary>
    public static string Masked(string? text) => text is null ? "-" : IPAddress.TryParse(text, out var address) ? Masked(address) : "(not an address)";
}
