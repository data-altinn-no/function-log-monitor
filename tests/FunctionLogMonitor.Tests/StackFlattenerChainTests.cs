using FunctionLogMonitor.Services;
using Xunit;

namespace FunctionLogMonitor.Tests;

public class StackFlattenerChainTests
{
    private const string Chain = """
        [{"outerId":"0","id":"1","type":"System.ArgumentException","message":"Could not read response",
          "parsedStack":[
            {"assembly":"Dan.Plugin.Foo, Version=1.0.0.0","method":"Dan.Plugin.Foo.Client.Get","level":0,"line":97,"fileName":"/src/Client.cs"},
            {"assembly":"Dan.Plugin.Foo, Version=1.0.0.0","method":"Dan.Plugin.Foo.Api.Run","level":1,"line":12,"fileName":"/src/Api.cs"}]},
         {"outerId":"1","id":"2","type":"Newtonsoft.Json.JsonReaderException","message":"Unexpected character\n  encountered: <",
          "parsedStack":[
            {"assembly":"Newtonsoft.Json, Version=13.0.0.0","method":"Newtonsoft.Json.JsonTextReader.ParseValue","level":0,"line":0},
            {"assembly":"Dan.Plugin.Foo, Version=1.0.0.0","method":"Dan.Plugin.Foo.Client.Parse","level":1,"line":81,"fileName":"/src/Client.cs"}]}]
        """;

    [Fact]
    public void ChainKeepsEachExceptionsTypeAndMessageOuterFirst()
    {
        var lines = StackFlattener.Flatten(Chain).Split('\n');
        Assert.Equal("System.ArgumentException: Could not read response", lines[0]);
        Assert.Equal(" ---> Newtonsoft.Json.JsonReaderException: Unexpected character encountered: <", lines[1]);
    }

    [Fact]
    public void InnerFramesComeFirstAndAreNotInterleavedWithOuterFrames()
    {
        var flat = StackFlattener.Flatten(Chain);
        var inner = flat.IndexOf("Client.Parse", StringComparison.Ordinal);
        var separator = flat.IndexOf("--- End of inner exception stack trace ---", StringComparison.Ordinal);
        var outer = flat.IndexOf("Client.Get", StringComparison.Ordinal);
        Assert.True(inner < separator && separator < outer);
    }

    [Fact]
    public void TopFrameIsTheInnermostFirstPartyFrame()
    {
        var top = StackFlattener.TopFirstPartyFrame(Chain);
        Assert.Equal(81, top!.Value.Line);
    }

    [Fact]
    public void TopFrameMovesOutwardWhenTheInnermostHasNoFirstPartyFrames()
    {
        const string socket = """
            [{"outerId":"0","id":"1","type":"System.IO.IOException","message":"read failed",
              "parsedStack":[{"assembly":"Dan.Core, Version=1.0.0.0","method":"Dan.Core.Http.Send","level":0,"line":44,"fileName":"/src/Http.cs"}]},
             {"outerId":"1","id":"2","type":"System.Net.Sockets.SocketException","message":"reset","parsedStack":[]}]
            """;
        Assert.Equal(44, StackFlattener.TopFirstPartyFrame(socket)!.Value.Line);
    }

    [Fact]
    public void LongMessagesAreCappedInTheHeader()
    {
        var json = Chain.Replace("Could not read response", new string('x', 1000));
        var header = StackFlattener.Flatten(json).Split('\n')[0];
        Assert.True(header.Length < 400);
        Assert.EndsWith("...", header);
    }
}
