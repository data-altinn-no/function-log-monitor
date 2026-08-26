using FunctionLogMonitor.Services;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class RedactorTests
{
    private readonly Redactor _redactor = new();

    [Fact]
    public void RedactsNorwegianFnr()
    {
        var output = _redactor.Redact("Lookup failed for subject 13097248022");
        Assert.DoesNotContain("13097248022", output);
        Assert.Contains("<fnr>", output);
    }

    [Fact]
    public void RedactsEmail()
    {
        var output = _redactor.Redact("Notification to ola.nordmann@example.no bounced");
        Assert.DoesNotContain("ola.nordmann@example.no", output);
        Assert.Contains("<email>", output);
    }

    [Fact]
    public void RedactsJwt()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var output = _redactor.Redact($"Token validation failed: {jwt}");
        Assert.DoesNotContain(jwt, output);
        Assert.Contains("<jwt>", output);
    }

    [Fact]
    public void RedactsBearerToken()
    {
        var output = _redactor.Redact("Authorization: Bearer abc123.def456-ghi789");
        Assert.DoesNotContain("abc123.def456-ghi789", output);
        Assert.Contains("Bearer <tok>", output);
    }

    [Theory]
    [InlineData("Password=hunter2;", "Password")]
    [InlineData("AccountKey=abcdef123456;", "AccountKey")]
    [InlineData("User Id=admin;", "User Id")]
    public void RedactsConnectionStringSecretsButKeepsTheKeyName(string input, string key)
    {
        var output = _redactor.Redact(input);
        Assert.Contains($"{key}=<redacted>", output);
    }

    [Fact]
    public void RedactsQueryStringSecrets()
    {
        var output = _redactor.Redact("GET /v1/data?api_key=s3cr3tvalue&page=2");
        Assert.DoesNotContain("s3cr3tvalue", output);
        Assert.Contains("<redacted>", output);
    }

    [Fact]
    public void RedactsClientIpAddress()
    {
        var output = _redactor.Redact("Request from 192.168.14.201 rejected");
        Assert.DoesNotContain("192.168.14.201", output);
        Assert.Contains("<ip>", output);
    }

    [Fact]
    public void RedactsLongBase64Blob()
    {
        var blob = new string('A', 100);
        var output = _redactor.Redact($"key={blob}");
        Assert.DoesNotContain(blob, output);
        Assert.Contains("<b64-redact>", output);
    }

    [Fact]
    public void HandlesNullAndEmptyInput()
    {
        Assert.Equal("", _redactor.Redact(null));
        Assert.Equal("", _redactor.Redact(""));
    }

    [Fact]
    public void AssertCleanPassesForRedactedText()
    {
        var output = _redactor.Redact("Contact ola@example.no about 13097248022");
        _redactor.AssertClean(output);
    }

    [Theory]
    [InlineData("still has ola@example.no in it")]
    [InlineData("still has 13097248022 in it")]
    public void AssertCleanThrowsWhenSensitiveDataSurvives(string text)
    {
        Assert.Throws<InvalidOperationException>(() => _redactor.AssertClean(text));
    }

    // KnownDefect_* assert current, unwanted behaviour so a fix trips them.
    [Fact]
    public void KnownDefect_PhoneRuleAlsoMatchesPlainNumericIdentifiers()
    {
        var output = _redactor.Redact("orderId=12345678 failed");
        Assert.Contains("<phone>", output);
    }

    [Fact]
    public void KnownDefect_IpRuleAlsoMatchesAssemblyVersions()
    {
        var output = _redactor.Redact("Dan.Core, Version=1.0.0.0, Culture=neutral");
        Assert.Contains("<ip>", output);
    }

    // PollExceptions emits this exact shape for the downstream locator.
    [Theory]
    [InlineData("   at Dan.Core.Services.Foo.Bar() in /src/Dan.Core/Services/Foo.cs:line 129",
                "/src/Dan.Core/Services/Foo.cs", "line 129")]
    [InlineData("   at Dan.Core.Services.CosmosDbAccreditationRepository+<UpdateAccreditationAsync>d__5.MoveNext() in /home/runner/work/core/core/Dan.Core/Services/CosmosDbAccreditationRepository.cs:line 129",
                "/home/runner/work/core/core/Dan.Core/Services/CosmosDbAccreditationRepository.cs", "line 129")]
    [InlineData("   at Altinn.Dan.Plugin.Nsg.NSGv1_0+<Get>d__9.MoveNext() in /home/runner/work/plugin-nsg/plugin-nsg/src/Altinn.Dan.Plugin.Nsg/NSGv1.0.cs:line 246",
                "src/Altinn.Dan.Plugin.Nsg/NSGv1.0.cs", "line 246")]
    public void StackTraceFramePathAndLineSurviveRedaction(string frame, string path, string line)
    {
        var output = _redactor.Redact(frame);
        Assert.Contains(path, output);
        Assert.Contains(line, output);
    }

    [Fact]
    public void AssemblyVersionIsMangled_WhichIsWhyFramesOmitTheAssembly()
    {
        var output = _redactor.Redact("Dan.Core, Version=1.0.0.0, Culture=neutral");
        Assert.DoesNotContain("1.0.0.0", output);
    }
}
