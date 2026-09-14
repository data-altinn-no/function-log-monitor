using Microsoft.Extensions.Logging;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class WorkerLoggingTests
{
    private const string AiProvider =
        "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider";

    [Fact]
    public void InformationLogsSurviveAfterTheApplicationInsightsRuleIsRemoved()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule(AiProvider, null, LogLevel.Warning, null));

        WorkerLogging.RemoveApplicationInsightsWarningFilter(options);

        Assert.DoesNotContain(options.Rules, r => r.ProviderName == AiProvider);
    }

    [Fact]
    public void OtherProviderRulesAreLeftAlone()
    {
        var options = new LoggerFilterOptions();
        options.Rules.Add(new LoggerFilterRule(AiProvider, null, LogLevel.Warning, null));
        options.Rules.Add(new LoggerFilterRule("Console", null, LogLevel.Debug, null));

        WorkerLogging.RemoveApplicationInsightsWarningFilter(options);

        Assert.Single(options.Rules);
        Assert.Equal("Console", options.Rules[0].ProviderName);
    }

    [Fact]
    public void NoRuleToRemoveIsNotAnError()
    {
        var options = new LoggerFilterOptions();
        WorkerLogging.RemoveApplicationInsightsWarningFilter(options);
        Assert.Empty(options.Rules);
    }
}
