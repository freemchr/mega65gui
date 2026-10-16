using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mega65Gui.Core.Filehost;

public enum SignInResult { Success, InvalidCredentials, Error }

public sealed record SignInOutcome(SignInResult Result, string? Message);

/// <summary>
/// Signs in to the Filehost with the user's own account, the same way the website does
/// (POST login.php; the session lives in the PHPSESSID cookie). The HttpClient passed in must
/// use a cookie container shared with <see cref="FilehostClient"/>.
///
/// The password is only sent to files.mega65.org over HTTPS. It is never stored here and never
/// logged. What a signed-in account may see (for example closed-ROM packages after redeeming
/// the ownership code) is decided entirely by the server.
/// </summary>
public sealed class FilehostSession(HttpClient http)
{
    public string? UserName { get; private set; }
    public bool IsSignedIn => UserName is not null;

    public event Action? Changed;

    public async Task<SignInOutcome> SignInAsync(string userName, string password, CancellationToken ct = default)
    {
        // jQuery encodes the site's {"logindata": {...}} payload as bracketed form fields.
        using var content = new FormUrlEncodedContent(
        [
            new("logindata[username]", userName),
            new("logindata[password]", password),
        ]);
        using var resp = await http.PostAsync(new Uri(FilehostClient.PhpBase, "login.php"), content, ct);
        resp.EnsureSuccessStatusCode();
        var (state, message) = ReadState(await resp.Content.ReadAsStringAsync(ct));

        var outcome = state switch
        {
            "OK" => new SignInOutcome(SignInResult.Success, null),
            "NOTOK" => new SignInOutcome(SignInResult.InvalidCredentials, message ?? "Wrong user name or password."),
            _ => new SignInOutcome(SignInResult.Error, message ?? "The Filehost returned an unexpected answer."),
        };
        UserName = outcome.Result == SignInResult.Success ? userName : null;
        Changed?.Invoke();
        return outcome;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.PostAsync(new Uri(FilehostClient.PhpBase, "logout.php"), new FormUrlEncodedContent([]), ct);
        }
        finally
        {
            UserName = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Called when the server reports RELOGIN, TIMEOUT or LOGINERROR.</summary>
    public void MarkExpired()
    {
        if (UserName is null) return;
        UserName = null;
        Changed?.Invoke();
    }

    public static bool IsExpiredState(string? state) => state is "RELOGIN" or "TIMEOUT" or "LOGINERROR";

    internal static (string? State, string? Message) ReadState(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonArray { Count: > 0 } a) node = a[0];
            return (node?["state"]?.GetValue<string>(), node?["message"]?.GetValue<string>());
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }
}

public static partial class FilehostLinks
{
    [GeneratedRegex(@"files\.mega65\.org/?(?:html/main\.php)?\?id=(?<id>[A-Za-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileLink();

    /// <summary>File IDs linked from a description, e.g. the "private link" to an owner-only ROM package.</summary>
    public static IReadOnlyList<string> Extract(string? text) =>
        string.IsNullOrEmpty(text) ? [] : FileLink().Matches(text).Select(m => m.Groups["id"].Value).Distinct().ToList();
}
