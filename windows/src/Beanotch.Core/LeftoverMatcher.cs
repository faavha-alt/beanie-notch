namespace Beanotch.Core;

// Windows uninstallers leave folders named after the app. Match the full name only: treating the first word as a
// match puts shared vendor folders such as %APPDATA%\Microsoft or %LOCALAPPDATA%\Google one click from deletion.
public static class LeftoverMatcher
{
    private static readonly HashSet<string> protectedNames = new(StringComparer.Ordinal)
    {
        "microsoft", "google", "apple", "adobe", "mozilla", "nvidia", "intel",
        "commonfiles", "windows", "programfiles", "programdata", "users", "local", "roaming",
    };

    public static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool IsProtected(string name) => protectedNames.Contains(Normalize(name));

    public static bool Matches(string folderName, string appName)
    {
        var candidate = Normalize(appName);
        return candidate.Length >= 4 && !protectedNames.Contains(candidate)
            && Normalize(folderName) == candidate;
    }
}
