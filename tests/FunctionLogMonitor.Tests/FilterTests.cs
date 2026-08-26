using FunctionLogMonitor.Services;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class FingerprintNormalizationTests
{
    [Fact]
    public void TraceparentCollapses()
    {
        var a = Fingerprint.ComputeFromTemplate("func-dancore-prod",
            "Failed to send reminder traceId 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        var b = Fingerprint.ComputeFromTemplate("func-dancore-prod",
            "Failed to send reminder traceId 00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        Assert.Equal(a, b);
    }

    [Fact]
    public void GuidCollapses()
    {
        var a = Fingerprint.ComputeFromTemplate("r", "order aid=4c7d5464-e858-470d-bb68-9a283933531c failed");
        var b = Fingerprint.ComputeFromTemplate("r", "order aid=1a2b3c4d-0000-1111-2222-333344445555 failed");
        Assert.Equal(a, b);
    }

    [Fact]
    public void UndashedHexCollapses()
    {
        var a = Fingerprint.ComputeFromTemplate("r", "sourceOrgNo=a1b2c3d4e5f60718 timed out");
        var b = Fingerprint.ComputeFromTemplate("r", "sourceOrgNo=f6e5d4c3b2a10817 timed out");
        Assert.Equal(a, b);
    }

    [Fact]
    public void QueryStringCollapsesButPathStillDiscriminates()
    {
        var a = Fingerprint.ComputeFromTemplate("r", "GET https://api.example.no/v1/x?requestor=abcdef01");
        var b = Fingerprint.ComputeFromTemplate("r", "GET https://api.example.no/v1/x?requestor=fedcba10");
        var c = Fingerprint.ComputeFromTemplate("r", "GET https://api.example.no/v1/OTHER?requestor=abcdef01");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void AssemblyVersionCollapses_SoADeployDoesNotRefileTheBacklog()
    {
        var a = Fingerprint.Compute("System.NullReferenceException", "Dan.Core, Version=1.0.0.0, Culture=neutral");
        var b = Fingerprint.Compute("System.NullReferenceException", "Dan.Core, Version=1.4.2.9, Culture=neutral");
        Assert.Equal(a, b);
    }

    [Fact]
    public void LineNumberCollapses_SoARefactorDoesNotRefile()
    {
        var a = Fingerprint.Compute("E", "at Foo.Bar() in /src/Foo.cs:line 129");
        var b = Fingerprint.Compute("E", "at Foo.Bar() in /src/Foo.cs:line 415");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentRolesStayDistinct()
    {
        Assert.NotEqual(
            Fingerprint.ComputeFromTemplate("func-dancore-prod", "same message"),
            Fingerprint.ComputeFromTemplate("func-estilda-prod-prod", "same message"));
    }

    [Fact]
    public void DifferentTemplatesStayDistinct()
    {
        Assert.NotEqual(
            Fingerprint.ComputeFromTemplate("r", "Failed to send reminder"),
            Fingerprint.ComputeFromTemplate("r", "Failed to fetch account"));
    }

    [Fact]
    public void IsSixteenLowerHexAndStable()
    {
        var fp = Fingerprint.Compute("E", "at Foo.Bar()");
        Assert.Equal(16, fp.Length);
        Assert.All(fp, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        Assert.Equal(fp, Fingerprint.Compute("E", "at Foo.Bar()"));
    }
}

public class ExceptionClassifierTests
{
    [Theory]
    [InlineData("Dan.Core.Exceptions.InvalidSubjectException")]
    [InlineData("Dan.Core.Exceptions.AuthorizationFailedException")]
    [InlineData("Dan.Core.Exceptions.UnknownEvidenceCodeException")]
    [InlineData("Dan.Common.Exceptions.EvidenceSourceTransientException")]
    [InlineData("System.Net.Sockets.SocketException")]
    [InlineData("System.Threading.Tasks.TaskCanceledException")]
    [InlineData("StackExchange.Redis.RedisTimeoutException")]
    [InlineData("Altinn.ApiClients.Maskinporten.Models.TokenRequestException")]
    public void SkipsNoise(string type) => Assert.True(ExceptionClassifier.ShouldSkip(type, ""));

    [Theory]
    [InlineData("System.NullReferenceException")]
    [InlineData("System.ArgumentNullException")]
    [InlineData("System.ArgumentException")]
    [InlineData("Newtonsoft.Json.JsonReaderException")]
    [InlineData("System.Text.Json.JsonReaderException")]
    public void KeepsDefectShaped(string type) => Assert.False(ExceptionClassifier.ShouldSkip(type, ""));

    [Fact]
    public void KeepsInvalidJmesPath_DespiteBeingDanPrefixed()
    {
        Assert.False(ExceptionClassifier.ShouldSkip(
            "Dan.Core.Exceptions.InvalidJmesPathExpressionException", ""));
    }

    [Fact]
    public void PollyCircuitBreakerIsSkipped_DespiteGenericArgInTypeName()
    {
        Assert.True(ExceptionClassifier.ShouldSkip(
            "Polly.CircuitBreaker.BrokenCircuitException`1[[System.Net.Http.HttpResponseMessage, System.Net.Http, Version=1.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a]]",
            ""));
    }

    [Fact]
    public void Cosmos4xxIsKept_5xxIsSkipped()
    {
        const string t = "Microsoft.Azure.Cosmos.CosmosException";
        Assert.False(ExceptionClassifier.ShouldSkip(t,
            "Response status code does not indicate success: RequestEntityTooLarge (413); Substatus 0"));
        Assert.True(ExceptionClassifier.ShouldSkip(t,
            "Response status code does not indicate success: InternalServerError (500); Substatus 0"));
    }
}

public class StackFlattenerTests
{
    private const string RealDetails = """
        [{"severityLevel":"Error","outerId":"0","message":"boom","type":"System.NullReferenceException","id":"1","parsedStack":[
          {"assembly":"Dan.Core, Version=1.0.0.0, Culture=neutral","method":"Dan.Core.Helpers.EvidenceSourceHelper+<DoRequest>d__4`1.MoveNext","level":0,"line":97,"fileName":"/home/runner/work/core/core/Dan.Core/Helpers/EvidenceSourceHelper.cs"},
          {"assembly":"Dan.Core, Version=1.0.0.0, Culture=neutral","method":"Dan.Core.DirectFunctionExecutor+<ExecuteAsync>d__3.MoveNext","level":1,"line":44,"fileName":"/home/runner/work/core/core/obj/GeneratedFunctionExecutor.g.cs"},
          {"assembly":"System.Private.CoreLib, Version=1.0.0.0","method":"System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw","level":2,"line":0}
        ]}]
        """;

    [Fact]
    public void ProducesTextFramesTheLocatorCanParse()
    {
        var flat = StackFlattener.Flatten(RealDetails);
        Assert.Contains(
            "in /home/runner/work/core/core/Dan.Core/Helpers/EvidenceSourceHelper.cs:line 97", flat);
        Assert.Contains("   at ", flat);
    }

    [Fact]
    public void TopFirstPartyFrameSkipsGeneratedExecutor()
    {
        var top = StackFlattener.TopFirstPartyFrame(RealDetails);
        Assert.NotNull(top);
        Assert.EndsWith("EvidenceSourceHelper.cs", top!.Value.FileName);
        Assert.Equal(97, top.Value.Line);
    }

    [Fact]
    public void SkipsFrameworkFramesAndLineZero()
    {
        var top = StackFlattener.TopFirstPartyFrame(RealDetails);
        Assert.NotNull(top);
        Assert.StartsWith("Dan.", top!.Value.Assembly);
        Assert.True(top.Value.Line > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   at Foo.Bar() in /src/x.cs:line 1")]   // already text, not JSON
    [InlineData("[{\"parsedStack\":[{\"assembly\":\"Dan.Core")] // truncated mid-object
    public void ReturnsEmptyOnUnusableInput_SoCallerFallsBackToRawText(string? input)
    {
        Assert.Equal("", StackFlattener.Flatten(input));
        Assert.Null(StackFlattener.TopFirstPartyFrame(input));
    }
}

public class StackFlattenerRobustnessTests
{
    [Fact]
    public void RecoversFramesFromTruncatedJson()
    {
        const string truncated =
            """[{"severityLevel":"Error","parsedStack":[{"assembly":"Dan.Core, Version=1.0.0.0","method":"Dan.Core.Services.Repo.Update","level":0,"line":129,"fileName":"/home/runner/work/core/core/Dan.Core/Services/Repo.cs"},{"assembly":"Dan.Core, Vers""";
        var top = StackFlattener.TopFirstPartyFrame(truncated);
        Assert.NotNull(top);
        Assert.Equal(129, top!.Value.Line);
        Assert.Contains("Repo.cs", top.Value.FileName);
    }

    [Fact]
    public void ToleratesMarkdownFencedPayload()
    {
        const string fenced =
            "```\n[{\"parsedStack\":[{\"assembly\":\"Dan.Core, Version=1.0.0.0\",\"method\":\"X.Y\",\"level\":0,\"line\":42,\"fileName\":\"/src/X.cs\"}]}]\n```";
        Assert.NotEqual("", StackFlattener.Flatten(fenced));
        Assert.Equal(42, StackFlattener.TopFirstPartyFrame(fenced)!.Value.Line);
    }

    [Fact]
    public void IgnoresFramesWithoutFileOrLine()
    {
        const string j =
            """[{"parsedStack":[{"assembly":"Dan.Core, Version=1.0.0.0","method":"NoPdb","level":0,"line":0},{"assembly":"Dan.Core, Version=1.0.0.0","method":"Real","level":1,"line":88,"fileName":"/src/Real.cs"}]}]""";
        var top = StackFlattener.TopFirstPartyFrame(j);
        Assert.NotNull(top);
        Assert.Equal(88, top!.Value.Line);
    }
}
