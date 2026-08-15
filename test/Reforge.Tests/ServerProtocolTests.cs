using System.Text;

namespace Reforge.Tests;

/// <summary>
/// The relay wire format. Before it was framed, the client could only report whether a socket
/// round-trip completed, so "the server ran this and it failed" and "the server answered with help
/// text" were the same answer: exit 0.
/// </summary>
public class ServerProtocolTests
{
    // ---------------- Response framing ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(42)]
    public void Response_RoundTripsTheExitCode(int code)
    {
        var parsed = ServerClient.ParseResponse(ServerClient.FormatResponse(code, "", "4 references of Foo\n"));

        Assert.NotNull(parsed);
        Assert.Equal(code, parsed!.Value.ExitCode);
    }

    [Fact]
    public void Response_KeepsStdoutAndStderrApart()
    {
        var parsed = ServerClient.ParseResponse(
            ServerClient.FormatResponse(1, "WARNING: --section 'Nope' is not a section\n", "{\"sections\":[]}"));

        Assert.NotNull(parsed);
        Assert.Equal("WARNING: --section 'Nope' is not a section\n", parsed!.Value.Stderr);
        Assert.Equal("{\"sections\":[]}", parsed.Value.Stdout);
    }

    /// <summary>
    /// stdout is JSON a script parses. It has to survive character for character — no added or
    /// dropped trailing newline — which is why the framing is length-prefixed rather than
    /// delimited.
    /// </summary>
    [Theory]
    [InlineData("{\"total\":0}")]
    [InlineData("{\"total\":0}\n")]
    [InlineData("")]
    public void Response_PreservesStdoutExactly(string stdout)
    {
        var parsed = ServerClient.ParseResponse(ServerClient.FormatResponse(0, "some warning\n", stdout));

        Assert.NotNull(parsed);
        Assert.Equal(stdout, parsed!.Value.Stdout);
    }

    /// <summary>
    /// The length prefix, not a delimiter, is what makes this safe: output containing the header
    /// text cannot forge a section boundary.
    /// </summary>
    [Fact]
    public void Response_OutputContainingTheHeaderText_DoesNotForgeABoundary()
    {
        const string hostile = "__reforge_res_v1__ 0 0\nnot really a header";
        var parsed = ServerClient.ParseResponse(ServerClient.FormatResponse(7, "", hostile));

        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.Value.ExitCode);
        Assert.Equal(hostile, parsed.Value.Stdout);
        Assert.Equal("", parsed.Value.Stderr);
    }

    [Fact]
    public void Response_MultibyteText_SurvivesTheLengthPrefix()
    {
        const string stderr = "WARNING: café — naïve\n";
        const string stdout = "résultat: 4 références\n";

        var parsed = ServerClient.ParseResponse(ServerClient.FormatResponse(0, stderr, stdout));

        Assert.NotNull(parsed);
        Assert.Equal(stderr, parsed!.Value.Stderr);
        Assert.Equal(stdout, parsed.Value.Stdout);
    }

    /// <summary>
    /// A pre-v1 server sends bare output with no frame. It is no longer read as success — that is
    /// exactly how the silent-success bug survived a client upgrade — and the version gate in the
    /// port file means a v1 client should never even reach such a server.
    /// </summary>
    [Theory]
    [InlineData("4 references of Foo\n  src/Foo.cs\n")]
    [InlineData("")]
    [InlineData("__reforge_exit__:0\nlegacy sentinel from a v0.26 prerelease")]
    public void Response_WithoutV1Framing_IsRejectedRatherThanAssumedSuccessful(string response)
    {
        Assert.Null(ServerClient.ParseResponse(response));
    }

    [Theory]
    [InlineData("__reforge_res_v1__ 0")]                 // missing the stderr length
    [InlineData("__reforge_res_v1__ notanumber 0\nx")]   // unparseable exit code
    [InlineData("__reforge_res_v1__ 0 -1\nx")]           // negative length
    [InlineData("__reforge_res_v1__ 0 99\nshort")]       // length runs past the body
    public void Response_Malformed_IsRejected(string response)
    {
        Assert.Null(ServerClient.ParseResponse(response));
    }

    // ---------------- Request framing ----------------

    /// <summary>
    /// The client's working directory travels with the request so a relative --config, --baseline
    /// or `snapshot --append` path resolves against the caller's directory, not against whatever
    /// directory `reforge serve` was started in.
    /// </summary>
    [Fact]
    public async Task Request_CarriesTheClientWorkingDirectoryAndCommandLine()
    {
        var request = ServerClient.FormatRequest("/home/pete/projects/humans", "snapshot --append out.csv");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));
        using var reader = new StreamReader(stream);

        Assert.Equal("__reforge_req_v1__", await reader.ReadLineAsync());
        Assert.Equal("/home/pete/projects/humans", await reader.ReadLineAsync());
        Assert.Equal("snapshot --append out.csv", await reader.ReadLineAsync());
    }

    /// <summary>
    /// A whole line per field, so a directory containing spaces needs no quoting rule — the case a
    /// space-separated header would get wrong.
    /// </summary>
    [Fact]
    public async Task Request_WorkingDirectoryWithSpaces_NeedsNoQuoting()
    {
        var request = ServerClient.FormatRequest(@"C:\Users\Pete Drier\My Projects", "references Foo");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));
        using var reader = new StreamReader(stream);

        await reader.ReadLineAsync();
        Assert.Equal(@"C:\Users\Pete Drier\My Projects", await reader.ReadLineAsync());
    }

    [Fact]
    public void BuildCommandLine_QuotesArgumentsContainingSpaces()
    {
        Assert.Equal("references \"My Type\"", ServerClient.BuildCommandLine(["references", "My Type"]));
        Assert.Equal("references Foo", ServerClient.BuildCommandLine(["references", "Foo"]));
    }

    // ---------------- Port file: the out-of-band version handshake ----------------

    [Fact]
    public void PortFile_RoundTripsPortAndProtocol()
    {
        var endpoint = ServerClient.ParsePortFile(ServerClient.FormatPortFile(54321));

        Assert.NotNull(endpoint);
        Assert.Equal(54321, endpoint!.Port);
        Assert.Equal(ServerClient.ProtocolVersion, endpoint.Protocol);
    }

    /// <summary>
    /// A pre-v1 server writes only the port. Reporting protocol 0 is what makes the client take
    /// the cold path instead of relaying into a command table that doesn't know the newer commands.
    /// </summary>
    [Theory]
    [InlineData("54321")]
    [InlineData("54321\n")]
    [InlineData("  54321  ")]
    public void PortFile_FromAPreV1Server_ParsesWithProtocolZero(string content)
    {
        var endpoint = ServerClient.ParsePortFile(content);

        Assert.NotNull(endpoint);
        Assert.Equal(54321, endpoint!.Port);
        Assert.Equal(0, endpoint.Protocol);
        Assert.True(endpoint.Protocol < ServerClient.ProtocolVersion);
    }

    /// <summary>
    /// The other direction of the handshake, and the reason the marker is on a second line: a
    /// pre-v1 client reads the whole file and does <c>int.TryParse</c> on it. That has to FAIL
    /// against a v1 port file, so the old client finds no server and cold-starts rather than
    /// relaying into framing it would print verbatim — corrupting `--format json` output.
    /// </summary>
    [Fact]
    public void PortFile_V1Format_IsUnparseableByAPreV1Client()
    {
        var content = ServerClient.FormatPortFile(54321);

        Assert.False(int.TryParse(content.Trim(), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("not-a-port\nprotocol=1")]
    public void PortFile_Unparseable_IsNull(string content)
    {
        Assert.Null(ServerClient.ParsePortFile(content));
    }

    /// <summary>
    /// The gate is an exact match, not a minimum. A newer server is no safer to dispatch to than
    /// an older one: the version marks a framing change, so a v2 server's reply is by definition
    /// something this build cannot read — and finding that out after sending is finding out after
    /// the command may already have run.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void PortFile_AnyProtocolOtherThanThisOne_IsNotADispatchTarget(int advertised)
    {
        var endpoint = ServerClient.ParsePortFile($"54321\nprotocol={advertised}");

        Assert.NotNull(endpoint);
        Assert.NotEqual(ServerClient.ProtocolVersion, endpoint!.Protocol);
    }

    [Fact]
    public void PortFile_CarriageReturns_AreTolerated()
    {
        var endpoint = ServerClient.ParsePortFile("54321\r\nprotocol=1\r\n");

        Assert.NotNull(endpoint);
        Assert.Equal(54321, endpoint!.Port);
        Assert.Equal(1, endpoint.Protocol);
    }
}
