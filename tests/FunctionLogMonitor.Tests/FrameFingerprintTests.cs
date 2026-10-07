using FunctionLogMonitor.Services;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class FrameFingerprintTests
{
    [Theory]
    [InlineData("Dan.Core.Services.EvidenceHarvesterService+<Harvest>d__9.MoveNext", "Dan.Core.Services.EvidenceHarvesterService.Harvest")]
    [InlineData("Dan.Core.Api+<>c__DisplayClass5_0.<Run>b__0", "Dan.Core.Api.Run")]
    [InlineData("Dan.Core.Api+<>c.<Run>b__3_1", "Dan.Core.Api.Run")]
    [InlineData("Dan.Core.Api.<Main>g__Local|5_0", "Dan.Core.Api.Main.Local")]
    [InlineData("Dan.Core.Cache`1+<Get>d__4.MoveNext", "Dan.Core.Cache.Get")]
    [InlineData("Dan.Plugin.Dtos.SummertSkattegrunnlagDto..ctor", "Dan.Plugin.Dtos.SummertSkattegrunnlagDto.ctor")]
    public void CompilerGeneratedNamesReduceToTheSourceMethod(string frame, string expected)
    {
        Assert.Equal(expected, Fingerprint.NormalizeMethod(frame));
    }

    [Fact]
    public void ARebuildThatRenumbersAsyncStateMachinesKeepsTheFingerprint()
    {
        var before = Fingerprint.ComputeFromFrames("System.Exception", new[]
        {
            "Dan.Core.Helpers.EvidenceSourceHelper+<DoRequest>d__4.MoveNext",
            "Dan.Core.Services.EvidenceHarvesterService+<Harvest>d__9.MoveNext",
        });
        var after = Fingerprint.ComputeFromFrames("System.Exception", new[]
        {
            "Dan.Core.Helpers.EvidenceSourceHelper+<DoRequest>d__5.MoveNext",
            "Dan.Core.Services.EvidenceHarvesterService+<Harvest>d__8.MoveNext",
        });
        Assert.Equal(before, after);
    }

    [Fact]
    public void AMethodRecurringFromAnOuterExceptionDoesNotChangeTheFingerprint()
    {
        var single = Fingerprint.ComputeFromFrames("E", new[] { "Dan.A.DoRequest", "Dan.A.Harvest" });
        var chained = Fingerprint.ComputeFromFrames("E", new[] { "Dan.A.DoRequest", "Dan.A.Harvest", "Dan.A.DoRequest" });
        Assert.Equal(single, chained);
    }

    [Fact]
    public void DifferentTypesOrFailingMethodsStayApart()
    {
        var baseline = Fingerprint.ComputeFromFrames("E", new[] { "Dan.A.DoRequest" });
        Assert.NotEqual(baseline, Fingerprint.ComputeFromFrames("Other", new[] { "Dan.A.DoRequest" }));
        Assert.NotEqual(baseline, Fingerprint.ComputeFromFrames("E", new[] { "Dan.A.Harvest" }));
    }

    [Fact]
    public void FirstPartyFramesSkipFrameworkAndGeneratedCodeInnermostFirst()
    {
        const string chain = """
            [{"outerId":"0","id":"1","type":"Outer","message":"m","parsedStack":[
                {"assembly":"Dan.Core, Version=1.0.0.0","method":"Dan.Core.Outer.Run","level":0,"line":5,"fileName":"/src/Outer.cs"}]},
             {"outerId":"1","id":"2","type":"Inner","message":"m","parsedStack":[
                {"assembly":"System.Linq, Version=9.0.0.0","method":"System.Linq.Enumerable.Any","level":0,"line":0},
                {"assembly":"Dan.Core, Version=1.0.0.0","method":"Dan.Core.Executor.Execute","level":1,"line":9,"fileName":"/src/Executor.g.cs"},
                {"assembly":"Dan.Core, Version=1.0.0.0","method":"Dan.Core.Inner.Parse","level":2,"line":7,"fileName":"/src/Inner.cs"}]}]
            """;
        var methods = StackFlattener.FirstPartyFrames(chain).Select(f => f.Method).ToArray();
        Assert.Equal(new[] { "Dan.Core.Inner.Parse", "Dan.Core.Outer.Run" }, methods);
    }
}
