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
}
