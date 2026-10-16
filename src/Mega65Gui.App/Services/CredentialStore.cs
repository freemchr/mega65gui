using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mega65Gui.App.Services;

/// <summary>
/// Remembers the Filehost user name, and optionally the password encrypted with Windows DPAPI
/// for the current Windows user (it cannot be decrypted by other accounts or on other PCs).
/// </summary>
public sealed class CredentialStore(string file)
{
    private sealed record Stored(string UserName, string? ProtectedPassword);

    private static readonly byte[] Entropy = "Mega65Gui.Filehost"u8.ToArray();

    public (string UserName, string? Password)? Load()
    {
        try
        {
            if (!File.Exists(file)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file));
            if (s is null) return null;
            string? password = s.ProtectedPassword is null ? null
                : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.ProtectedPassword), Entropy, DataProtectionScope.CurrentUser));
            return (s.UserName, password);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException or IOException)
        {
            return null;
        }
    }

    public void Save(string userName, string? password)
    {
        string? protectedPassword = password is null ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));
        File.WriteAllText(file, JsonSerializer.Serialize(new Stored(userName, protectedPassword)));
    }

    public void Forget()
    {
        if (File.Exists(file)) File.Delete(file);
    }
}
