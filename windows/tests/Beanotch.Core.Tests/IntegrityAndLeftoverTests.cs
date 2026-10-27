using Beanotch.Core;
using Xunit;

public class ReleaseIntegrityTests
{
    [Theory]
    [InlineData("sha256:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("  sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  ",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void ReadsGitHubsDigest(string digest, string expected)
    {
        Assert.Equal(expected, ReleaseIntegrity.ParseSha256(digest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("md5:ba7816bf8f01cfea414140de5dae2223")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void RejectsMalformedDigest(string? digest)
    {
        Assert.Null(ReleaseIntegrity.ParseSha256(digest));
    }

    [Fact]
    public void ComparesHashesWithoutCase()
    {
        const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.True(ReleaseIntegrity.Matches(hash, hash.ToUpperInvariant()));
        Assert.False(ReleaseIntegrity.Matches(hash, hash[..^1] + "0"));
        Assert.False(ReleaseIntegrity.Matches(null, hash));
    }

    [Fact]
    public async Task HashesAFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                         await ReleaseIntegrity.Sha256FileAsync(path));
        }
        finally { File.Delete(path); }
    }
}

public class LeftoverMatcherTests
{
    [Theory]
    [InlineData("Google Chrome", "Google Chrome")]
    [InlineData("GoogleChrome", "Google Chrome")]
    [InlineData("Spotify", "Spotify")]
    [InlineData("Microsoft Teams", "Microsoft Teams")]
    public void MatchesTheFullAppName(string folder, string app) => Assert.True(LeftoverMatcher.Matches(folder, app));

    [Theory]
    [InlineData("Google", "Google Chrome")]
    [InlineData("Microsoft", "Microsoft Teams")]
    [InlineData("Google", "Google")]
    [InlineData("Microsoft", "Microsoft")]
    [InlineData("Google Chrome", "Google")]
    [InlineData("Go", "Go")]
    public void NeverMatchesAVendorFolderOrShortName(string folder, string app) =>
        Assert.False(LeftoverMatcher.Matches(folder, app));
}
