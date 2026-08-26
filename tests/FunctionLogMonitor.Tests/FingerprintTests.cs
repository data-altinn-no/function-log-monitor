using FunctionLogMonitor.Services;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class FingerprintTests
{
    [Fact]
    public void IsStableForIdenticalInput()
    {
        var a = Fingerprint.Compute("System.NullReferenceException", "at Foo.Bar()");
        var b = Fingerprint.Compute("System.NullReferenceException", "at Foo.Bar()");
        Assert.Equal(a, b);
    }

    [Fact]
    public void IsSixteenHexChars()
    {
        var fp = Fingerprint.Compute("System.Exception", "at Foo.Bar()");
        Assert.Equal(16, fp.Length);
        Assert.All(fp, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
    }

    [Fact]
    public void DiffersByExceptionType()
    {
        var a = Fingerprint.Compute("System.NullReferenceException", "at Foo.Bar()");
        var b = Fingerprint.Compute("System.ArgumentNullException", "at Foo.Bar()");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HandlesNullInput()
    {
        var fp = Fingerprint.Compute(null, null);
        Assert.Equal(16, fp.Length);
    }

    [Fact]
    public void CollapsesDashedGuids()
    {
        var a = Fingerprint.Compute("E", "aid=4c7d5464-e858-470d-bb68-9a283933531c failed");
        var b = Fingerprint.Compute("E", "aid=1a2b3c4d-0000-1111-2222-333344445555 failed");
        Assert.Equal(a, b);
    }

    [Fact]
    public void CollapsesPlainIntegers()
    {
        var a = Fingerprint.Compute("E", "at Foo.Bar() line 129");
        var b = Fingerprint.Compute("E", "at Foo.Bar() line 415");
        Assert.Equal(a, b);
    }
}
