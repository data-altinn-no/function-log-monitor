using System.ComponentModel.DataAnnotations;
using Xunit;

namespace FunctionLogMonitor.Tests;

[Collection("env")]
public class MonitorOptionsTests
{
    [Fact]
    public void UnsetExceptionThresholdDoesNotFilterRareDefects()
    {
        var previous = Environment.GetEnvironmentVariable("MIN_OCCURRENCES_EXCEPTIONS");
        Environment.SetEnvironmentVariable("MIN_OCCURRENCES_EXCEPTIONS", null);
        try
        {
            var opts = new MonitorOptions();
            opts.LoadFromEnvironment();
            Assert.Equal(1, opts.MinOccurrencesExceptions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIN_OCCURRENCES_EXCEPTIONS", previous);
        }
    }

    [Fact]
    public void ExceptionThresholdIsReadFromEnvironmentWhenSet()
    {
        var previous = Environment.GetEnvironmentVariable("MIN_OCCURRENCES_EXCEPTIONS");
        Environment.SetEnvironmentVariable("MIN_OCCURRENCES_EXCEPTIONS", "7");
        try
        {
            var opts = new MonitorOptions();
            opts.LoadFromEnvironment();
            Assert.Equal(7, opts.MinOccurrencesExceptions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIN_OCCURRENCES_EXCEPTIONS", previous);
        }
    }

    [Theory]
    [InlineData("TriageLabelErrors")]
    [InlineData("TriageLabelExceptions")]
    public void MissingTriageLabelFailsValidationRatherThanFilingUnroutableIssues(string property)
    {
        var opts = new MonitorOptions
        {
            AppInsightsAppId = "id",
            AppInsightsApiKey = "key",
            GitHubInputOwner = "owner",
            GitHubInputRepo = "repo",
            TriageLabelErrors = "auto-triage-errors",
            TriageLabelExceptions = "auto-triage-exceptions",
        };
        typeof(MonitorOptions).GetProperty(property)!.SetValue(opts, "");

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            opts, new ValidationContext(opts), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, r => r.MemberNames.Contains(property));
    }
}
