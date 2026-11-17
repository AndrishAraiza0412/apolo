using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Apolo.Api;

public static partial class Security
{
    [GeneratedRegex(@"<script>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    /// CSP hashes ('sha256-…') of the inline scripts in index.html, so 'unsafe-inline' does not have to be allowed.
    public static string InlineScriptHashes(string? webRoot)
    {
        var index = webRoot is null ? null : Path.Combine(webRoot, "index.html");
        if (index is null || !File.Exists(index)) return "";
        return string.Join(" ", InlineScript().Matches(File.ReadAllText(index))
            .Select(m => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))}'"));
    }
}
