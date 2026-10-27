using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Beanotch.Core;
using Microsoft.Win32;

namespace Beanotch.Services;

public sealed record GitHubAsset([property: JsonPropertyName("name")] string Name,
                                 [property: JsonPropertyName("browser_download_url")] string Url,
                                 [property: JsonPropertyName("digest")] string? Digest);

public sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("html_url")] string Page,
                                   [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

[JsonSerializable(typeof(GitHubRelease[]))]
internal sealed partial class ReleaseJson : JsonSerializerContext;

// Windows installers ship in the same releases as the Mac disk image. Installing downloads the setup for this
// PC's processor and runs it quietly; it closes this copy, replaces it and opens the new one.
public sealed class Updater
{
    // OTA channel: releases published by this fork carry the Windows installers.
    private const string Repository = "faavha-alt/beanie-notch";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public string Current { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    public string? Latest { get; private set; }
    public event Action<string>? Found;
    public bool Checking { get; private set; }
    public bool Installing { get; private set; }
    public bool Failed { get; private set; }
    private string? download;
    private string? downloadSha256;
    private string? announced;

    public bool UpdateAvailable => Latest is not null && Newer(Latest, Current);

    public async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        Failed = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=20");
            request.Headers.UserAgent.ParseAdd("Beanotch-Windows");
            using var response = await Http.SendAsync(request);
            var releases = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync(ReleaseJson.Default.GitHubReleaseArray) : null;
            // The newest release that carries a Windows installer: the Mac and Windows builds share one release.
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var name = $"Beanotch-Setup-{arch}.exe";
            var release = releases?.FirstOrDefault(r => r.Assets.Any(a => a.Name == name));
            if (release is null) { Failed = releases is null; return; }
            Latest = release.Tag.Replace("windows-", "").TrimStart('v');
            var asset = release.Assets.First(a => a.Name == name);
            download = IsGitHubReleaseUrl(asset.Url) ? asset.Url : null;
            downloadSha256 = ReleaseIntegrity.ParseSha256(asset.Digest);
            // No digest means no verified update: the user can still download it from the release page by hand.
            if (download is null || downloadSha256 is null) Failed = true;
            if (UpdateAvailable && !Failed && announced != Latest)
            {
                announced = Latest;
                Found?.Invoke(Latest!);
            }
        }
        catch (Exception) { Failed = true; }
        finally { Checking = false; }
    }

    public async Task InstallAsync()
    {
        if (download is null || downloadSha256 is null || Installing) return;
        Installing = true;
        try
        {
            // A fresh name each time, created exclusively: a planted symlink or file can't redirect the download.
            var file = Path.Combine(Path.GetTempPath(), $"Beanotch-Setup-{Latest}-{Guid.NewGuid():N}.exe");
            await using (var stream = await Http.GetStreamAsync(download))
            await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.CopyToAsync(output);

            var actual = await ReleaseIntegrity.Sha256FileAsync(file);
            if (!ReleaseIntegrity.Matches(downloadSha256, actual))
            {
                File.Delete(file);
                throw new InvalidDataException("The update's SHA-256 does not match the release.");
            }

            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception) { Installing = false; Failed = true; }
    }

    private static bool IsGitHubReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    public static bool Newer(string a, string b)
    {
        int[] Parts(string v) => [.. v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0)];
        var (x, y) = (Parts(a), Parts(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
            if (l != r) return l > r;
        }
        return false;
    }

    // Open at sign-in, through the same Run entry the installer writes.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool OpenAtLogin
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Beanotch") is string;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Beanotch", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Beanotch", false);
        }
    }
}
