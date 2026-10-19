namespace Sallyport.Windows.PacketFilter;

/// <summary>
/// The one Windows Packet Filter installer Sallyport will run (D18): NT Kernel's own release, fetched from their own
/// repository on the person's machine when they ask for it — never copied into Sallyport or shipped with it.
/// </summary>
/// <remarks>
/// The SHA-256 is the trust anchor: these exact bytes were read on 2026-10-20 before being pinned — the MSI and the
/// driver inside it signed by "The Anti-Cloud Corporation" (EV), the driver's catalog (<c>ndisrd.cat</c>) signed by
/// "Microsoft Windows Hardware Compatibility Publisher", the driver itself <c>ndisrd.sys</c> 3.6.2.1, "NT KERNEL | NDIS
/// Filter driver", installed by its INF as the NDIS lightweight filter <c>nt_ndisrd</c>, service <c>ndisrd</c>. A new
/// release is pinned the same way, or not at all.
/// </remarks>
public static class PacketFilterRelease
{
    public const string Version = "3.6.2.1";

    public static readonly Uri Installer = new("https://github.com/wiresock/ndisapi/releases/download/v3.6.2/Windows.Packet.Filter.3.6.2.1.x64.msi");

    public const string InstallerSha256 = "9C388C0B7F189F7FA98720BAE2CAECF7D64F30910838B80B438ECF8956B8502C";

    public const string InstallerFileName = "Windows.Packet.Filter.3.6.2.1.x64.msi";

    /// <summary>The service its INF adds, and the device its driver makes (<c>\Device\NDISRD</c>).</summary>
    public const string ServiceName = "ndisrd";

    public const string DevicePath = @"\\.\NDISRD";

    /// <summary>The installer is x64; an Arm64 machine would need its own pin.</summary>
    public static bool FitsThisMachine => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64;
}
