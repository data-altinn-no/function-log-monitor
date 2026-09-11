using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FunctionLogMonitor.Services;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class GitHubClientFactoryTests
{
    private static readonly RSA Key = RSA.Create(2048);

    private static string Pem() => Key.ExportRSAPrivateKeyPem();

    [Fact]
    public void ReadsPemDirectly()
    {
        Assert.StartsWith("-----BEGIN", GitHubClientFactory.ReadPrivateKey(Pem()));
    }

    [Fact]
    public void ReadsBase64WrappedPem()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Pem()));
        Assert.Equal(Pem(), GitHubClientFactory.ReadPrivateKey(encoded));
    }

    [Fact]
    public void RejectsKeyThatIsNeitherPemNorBase64()
    {
        Assert.Throws<InvalidOperationException>(() => GitHubClientFactory.ReadPrivateKey("nonsense!"));
    }

    [Fact]
    public void JwtSignatureVerifiesWithThePublicKey()
    {
        var parts = GitHubClientFactory.CreateAppJwt("Iv23liTEST", Pem()).Split('.');
        Assert.Equal(3, parts.Length);

        var signed = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        Assert.True(Key.VerifyData(
            signed, Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void JwtCarriesClientIdAndRs256()
    {
        var parts = GitHubClientFactory.CreateAppJwt("Iv23liTEST", Pem()).Split('.');
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));

        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("Iv23liTEST", payload.RootElement.GetProperty("iss").GetString());
    }

    [Fact]
    public void JwtExpiryStaysInsideTheTenMinuteLimitGitHubEnforces()
    {
        var parts = GitHubClientFactory.CreateAppJwt("Iv23liTEST", Pem()).Split('.');
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();

        Assert.InRange(exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, 600);
        Assert.True(iat < DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("", 0L, "", false)]
    [InlineData("Iv23li", 157246034L, "key", true)]
    [InlineData("Iv23li", 0L, "key", false)]
    [InlineData("", 157246034L, "key", false)]
    public void UsesGitHubAppRequiresAllThreeSettings(
        string clientId, long installationId, string key, bool expected)
    {
        var opts = new MonitorOptions
        {
            GitHubAppClientId = clientId,
            GitHubAppInstallationId = installationId,
            GitHubAppPrivateKey = key,
        };
        Assert.Equal(expected, opts.UsesGitHubApp);
    }

    private static byte[] Decode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
