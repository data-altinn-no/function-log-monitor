using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octokit;

namespace FunctionLogMonitor.Services;

public interface IGitHubClientFactory
{
    Task<IGitHubClient> GetClientAsync(CancellationToken ct);
}

public sealed class GitHubClientFactory : IGitHubClientFactory
{
    private static readonly ProductHeaderValue Product = new("dan-agent-log-monitor");
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly MonitorOptions _opts;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IGitHubClient? _client;
    private DateTimeOffset _tokenExpiresAt;

    public GitHubClientFactory(IOptions<MonitorOptions> opts) => _opts = opts.Value;

    public async Task<IGitHubClient> GetClientAsync(CancellationToken ct)
    {
        if (!_opts.UsesGitHubApp)
        {
            return _client ??= Build(RequirePersonalToken());
        }

        if (IsFresh()) return _client!;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _client!;
            var installation = await MintInstallationTokenAsync();
            _client = Build(installation.Token);
            _tokenExpiresAt = installation.ExpiresAt;
            return _client;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsFresh() =>
        _client is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - RefreshBuffer;

    private static GitHubClient Build(string token) =>
        new(Product) { Credentials = new Credentials(token) };

    private string RequirePersonalToken() =>
        _opts.GitHubToken.Length > 0
            ? _opts.GitHubToken
            : throw new InvalidOperationException(
                "No GitHub credentials: set GITHUB_APP_CLIENT_ID, GITHUB_APP_INSTALLATION_ID "
                + "and GITHUB_APP_PRIVATE_KEY, or GITHUB_TOKEN");

    private async Task<AccessToken> MintInstallationTokenAsync()
    {
        var appClient = new GitHubClient(Product)
        {
            Credentials = new Credentials(
                CreateAppJwt(_opts.GitHubAppClientId, _opts.GitHubAppPrivateKey),
                AuthenticationType.Bearer),
        };
        return await appClient.GitHubApps.CreateInstallationToken(_opts.GitHubAppInstallationId);
    }

    public static string CreateAppJwt(string clientId, string privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(ReadPrivateKey(privateKey));

        var now = DateTimeOffset.UtcNow;
        var header = new { alg = "RS256", typ = "JWT" };
        // Backdated for clock skew; GitHub rejects an expiry over 10 minutes out.
        var payload = new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = clientId,
        };

        var signingInput =
            $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}."
            + $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public static string ReadPrivateKey(string configured)
    {
        var value = configured.Trim();
        if (value.Contains("-----BEGIN", StringComparison.Ordinal)) return value;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "GITHUB_APP_PRIVATE_KEY is neither PEM nor base64-encoded PEM");
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
