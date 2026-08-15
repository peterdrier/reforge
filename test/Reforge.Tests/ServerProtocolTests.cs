namespace Reforge.Tests;

/// <summary>
/// The relay protocol's exit-code channel. Before it existed the client could only report whether
/// a socket round-trip completed, so "the server ran this and it failed" and "the server answered
/// with help text" were the same answer: exit 0.
/// </summary>
public class ServerProtocolTests
{
    private static string Response(int exitCode, string payload) =>
        $"{ServerClient.ExitCodeSentinel}{exitCode}\n{payload}";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public void ParseResponse_ReturnsTheServersExitCode(int code)
    {
        var (exitCode, payload) = ServerClient.ParseResponse(Response(code, "4 references of Foo\n"));

        Assert.Equal(code, exitCode);
        Assert.Equal("4 references of Foo\n", payload);
    }

    /// <summary>
    /// The payload is handed back byte-for-byte — it is JSON for a scripting agent in the common
    /// case, so a trailing newline must be neither added nor dropped.
    /// </summary>
    [Fact]
    public void ParseResponse_PreservesThePayloadExactly()
    {
        const string payload = "{\"total\":0}";
        var (_, parsed) = ServerClient.ParseResponse(Response(0, payload));

        Assert.Equal(payload, parsed);
    }

    [Fact]
    public void ParseResponse_EmptyPayload_IsEmptyNotNull()
    {
        var (exitCode, payload) = ServerClient.ParseResponse(Response(0, ""));

        Assert.Equal(0, exitCode);
        Assert.Equal("", payload);
    }

    [Fact]
    public void ParseResponse_SentinelWithNoTrailingNewline_YieldsNoPayload()
    {
        var (exitCode, payload) = ServerClient.ParseResponse($"{ServerClient.ExitCodeSentinel}3");

        Assert.Equal(3, exitCode);
        Assert.Equal("", payload);
    }

    /// <summary>
    /// A server binary predating the sentinel sends output only. It must pass through unchanged and
    /// report success — a running older server should keep working, not start failing every call.
    /// </summary>
    [Fact]
    public void ParseResponse_WithoutASentinel_PassesOutputThroughAsSuccess()
    {
        const string legacy = "4 references of Foo\n  src/Foo.cs\n";
        var (exitCode, payload) = ServerClient.ParseResponse(legacy);

        Assert.Equal(0, exitCode);
        Assert.Equal(legacy, payload);
    }

    [Fact]
    public void ParseResponse_EmptyResponse_IsSuccessWithNoOutput()
    {
        var (exitCode, payload) = ServerClient.ParseResponse("");

        Assert.Equal(0, exitCode);
        Assert.Equal("", payload);
    }

    /// <summary>
    /// A malformed code is treated as success rather than throwing: the payload is still the useful
    /// thing, and a parse failure here should not take down a query that already ran.
    /// </summary>
    [Fact]
    public void ParseResponse_UnparseableExitCode_FallsBackToSuccess()
    {
        var (exitCode, payload) = ServerClient.ParseResponse($"{ServerClient.ExitCodeSentinel}not-a-number\nout\n");

        Assert.Equal(0, exitCode);
        Assert.Equal("out\n", payload);
    }
}
