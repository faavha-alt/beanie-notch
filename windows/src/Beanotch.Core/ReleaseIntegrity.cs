using System.Security.Cryptography;

namespace Beanotch.Core;

// GitHub publishes a SHA-256 digest for every release asset. Requiring it before running an update catches a
// corrupted or swapped download; only code signing can prove who built the file.
public static class ReleaseIntegrity
{
    public static string? ParseSha256(string? digest)
    {
        const string prefix = "sha256:";
        var value = digest?.Trim();
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = value[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
    }

    public static bool Matches(string? expectedHex, string? actualHex) =>
        expectedHex is { Length: 64 } && actualHex is { Length: 64 }
        && string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase);

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancel = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancel);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
