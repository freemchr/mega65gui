using System.Security.Cryptography;
using System.Text;

namespace Mega65Gui.App.Services;

/// <summary>
/// Keeps one secret (such as a GitHub token) encrypted with Windows DPAPI for the current Windows
/// user, as <see cref="CredentialStore"/> does for the Filehost password.
/// </summary>
public sealed class SecretStore(string file, string purpose)
{
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes(purpose);

    public string? Load()
    {
        try
        {
            if (!File.Exists(file)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(file).Trim()), _entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IOException)
        {
            return null;
        }
    }

    public void Save(string secret) =>
        File.WriteAllText(file, Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), _entropy, DataProtectionScope.CurrentUser)));

    public void Forget()
    {
        if (File.Exists(file)) File.Delete(file);
    }
}
