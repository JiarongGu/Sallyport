using System.Security.Cryptography;
using System.Text;
using Sallyport.Runtime;

namespace Sallyport.Client;

/// <summary>Seals imported profiles for the user signed in (D14).</summary>
internal sealed class DpapiProtector : IProfileProtector
{
    // A label, not a secret: it keeps another DPAPI consumer of the same user from opening these by mistake.
    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("Sallyport.Client/profile/v1");

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Purpose, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, Purpose, DataProtectionScope.CurrentUser);
}
