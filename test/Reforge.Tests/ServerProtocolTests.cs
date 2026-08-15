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
        const string hostile = "__reforge_res_v2__ 0 0\nnot really a header";
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
    /// An older server sends bare output with no frame. It is no longer read as success — that is
    /// exactly how the silent-success bug survived a client upgrade — and the version gate in the
    /// port file means this client should never even reach such a server.
    /// </summary>
    [Theory]
    [InlineData("4 references of Foo\n  src/Foo.cs\n")]
    [InlineData("")]
    [InlineData("__reforge_exit__:0\nlegacy sentinel from a v0.26 prerelease")]
    [InlineData("__reforge_res_v1__ 0 0\nthe previous protocol's framing")]
    public void Response_WithoutCurrentFraming_IsRejectedRatherThanAssumedSuccessful(string response)
    {
        Assert.Null(ServerClient.ParseResponse(response));
    }

    [Theory]
    [InlineData("__reforge_res_v2__ 0")]                 // missing the stderr length
    [InlineData("__reforge_res_v2__ notanumber 0\nx")]   // unparseable exit code
    [InlineData("__reforge_res_v2__ 0 -1\nx")]           // negative length
    [InlineData("__reforge_res_v2__ 0 99\nshort")]       // length runs past the body
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
    public void Request_CarriesTheClientWorkingDirectoryAndArguments()
    {
        var parsed = ServerClient.ParseRequest(
            ServerClient.FormatRequest("/home/pete/projects/humans", ["snapshot", "--append", "out.csv"]));

        Assert.NotNull(parsed);
        Assert.Equal("/home/pete/projects/humans", parsed!.Value.WorkingDirectory);
        Assert.Equal(new[] { "snapshot", "--append", "out.csv" }, parsed.Value.Args);
    }

    /// <summary>
    /// The arguments this has to survive, each of which the previous join-and-re-split framing lost
    /// <b>silently</b>: a literal quote was deleted outright (Codex's example — <c>snapshot
    /// --append 'daily"run.csv'</c> relayed as <c>dailyrun.csv</c>, writing the wrong file and
    /// reporting success), a value with both a space and a quote came apart into two arguments, and
    /// an empty argument disappeared, shifting every positional argument after it.
    ///
    /// <para>Newlines and the framing's own header text are here because length prefixes make them
    /// free: there is no character with syntactic meaning left to escape.</para>
    /// </summary>
    [Theory]
    [InlineData("daily\"run.csv")]
    [InlineData("say \"hi\"")]
    [InlineData("")]
    [InlineData("My Type")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("__reforge_req_v2__ 0 0")]
    [InlineData("café — naïve")]
    [InlineData("\"")]
    public void Request_ArgumentsSurviveCharacterForCharacter(string hostileArgument)
    {
        var args = new[] { "snapshot", "--append", hostileArgument, "--trailing" };

        var parsed = ServerClient.ParseRequest(ServerClient.FormatRequest("/tmp", args));

        Assert.NotNull(parsed);
        Assert.Equal(args, parsed!.Value.Args);
    }

    /// <summary>
    /// The working directory gets the same treatment as an argument — it is a path, and a path may
    /// contain a space on every platform reforge runs on.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\Pete Drier\My Projects")]
    [InlineData("/home/pete/say \"hi\"")]
    [InlineData("/tmp")]
    public void Request_WorkingDirectorySurvivesCharacterForCharacter(string directory)
    {
        var parsed = ServerClient.ParseRequest(ServerClient.FormatRequest(directory, ["references", "Foo"]));

        Assert.NotNull(parsed);
        Assert.Equal(directory, parsed!.Value.WorkingDirectory);
    }

    [Fact]
    public void Request_NoArguments_ParsesAsAnEmptyArray()
    {
        var parsed = ServerClient.ParseRequest(ServerClient.FormatRequest("/tmp", []));

        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Value.Args);
    }

    [Theory]
    [InlineData("references Foo")]                          // an older client's bare command line
    [InlineData("__reforge_req_v1__\n/tmp\nreferences Foo")] // the previous protocol's framing
    [InlineData("__reforge_req_v2__ 4 1 3\n/tmp")]           // body shorter than the lengths claim
    [InlineData("__reforge_req_v2__ 4 1 3\n/tmpfoobar")]     // body longer than the lengths claim
    [InlineData("__reforge_req_v2__ 4 2 3\n/tmpfoo")]        // header lists fewer lengths than argCount
    [InlineData("__reforge_req_v2__ 4 1 -1\n/tmpx")]         // negative length
    [InlineData("__reforge_req_v2__ x 1 3\n/tmpfoo")]        // unparseable working-directory length
    [InlineData("__reforge_req_v2__ 4 1 3")]                 // no newline at all
    public void Request_Malformed_IsRejected(string request)
    {
        Assert.Null(ServerClient.ParseRequest(request));
    }

    /// <summary>
    /// A header claiming a huge argument count must be rejected before anything is sized by it — a
    /// length list the body cannot back is a malformed frame, not an allocation request.
    /// </summary>
    [Fact]
    public void Request_ImplausibleArgumentCount_IsRejectedWithoutAllocating()
    {
        Assert.Null(ServerClient.ParseRequest($"__reforge_req_v2__ 0 {int.MaxValue}\n"));
    }

    // ---------------- Port file: the out-of-band version handshake ----------------

    [Fact]
    public void PortFile_RoundTripsPortProtocolAndBuild()
    {
        var endpoint = ServerClient.ParsePortFile(ServerClient.FormatPortFile(54321));

        Assert.NotNull(endpoint);
        Assert.Equal(54321, endpoint!.Port);
        Assert.Equal(ServerClient.ProtocolVersion, endpoint.Protocol);
        Assert.Equal(ServerClient.BuildIdentity, endpoint.Build);
    }

    /// <summary>
    /// An older server writes only the port. Reporting protocol 0 is what makes the client take
    /// the cold path instead of relaying into a command table that doesn't know the newer commands.
    /// </summary>
    [Theory]
    [InlineData("54321")]
    [InlineData("54321\n")]
    [InlineData("  54321  ")]
    public void PortFile_FromAnUnversionedServer_ParsesWithProtocolZeroAndNoBuild(string content)
    {
        var endpoint = ServerClient.ParsePortFile(content);

        Assert.NotNull(endpoint);
        Assert.Equal(54321, endpoint!.Port);
        Assert.Equal(0, endpoint.Protocol);
        Assert.Equal("", endpoint.Build);
        Assert.NotEqual(ServerClient.ProtocolVersion, endpoint.Protocol);
    }

    /// <summary>
    /// The other direction of the handshake, and the reason the markers are on later lines: a client
    /// predating them reads the whole file and does <c>int.TryParse</c> on it. That has to FAIL
    /// against a current port file, so the old client finds no server and cold-starts rather than
    /// relaying into framing it would print verbatim — corrupting `--format json` output.
    /// </summary>
    [Fact]
    public void PortFile_CurrentFormat_IsUnparseableByAClientPredatingIt()
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
    /// an older one: the version marks a framing change, so a newer server's reply is by definition
    /// something this build cannot read — and finding that out after sending is finding out after
    /// the command may already have run.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public void PortFile_AnyProtocolOtherThanThisOne_IsNotADispatchTarget(int advertised)
    {
        var endpoint = ServerClient.ParsePortFile($"54321\nprotocol={advertised}");

        Assert.NotNull(endpoint);
        Assert.NotEqual(ServerClient.ProtocolVersion, endpoint!.Protocol);
    }

    // ---------------- Port file: the build-identity gate ----------------

    /// <summary>
    /// The protocol version is not a proxy for behavior. v0.26.0 and v0.27.0 both speak protocol 1,
    /// and yet <c>surface-score</c> on a degraded build scores it and exits 0 on the first while
    /// refusing and exiting 2 on the second. Nothing in the envelope distinguishes them, so a client
    /// that gated on protocol alone relayed into the old contract and reported the old answer with
    /// no warning. The build identity is what makes that case detectable.
    /// </summary>
    [Theory]
    [InlineData("0.26.0/1a2b3c4d")]     // an earlier release
    [InlineData("0.27.0/9f8e7d6c")]     // the same version, rebuilt — the dogfooding case
    [InlineData("")]                    // a server predating the marker entirely
    public void PortFile_AnyBuildOtherThanThisOne_IsNotADispatchTarget(string advertised)
    {
        var endpoint = ServerClient.ParsePortFile(
            $"54321\nprotocol={ServerClient.ProtocolVersion}\nbuild={advertised}");

        Assert.NotNull(endpoint);
        Assert.Equal(ServerClient.ProtocolVersion, endpoint!.Protocol);   // the envelope agrees...
        Assert.NotEqual(ServerClient.BuildIdentity, endpoint.Build);      // ...and it is still not us
    }

    /// <summary>
    /// Version alone would not separate two builds of the same version, which is exactly the state
    /// of any working tree between releases — the case where a stale server is most likely.
    /// </summary>
    [Fact]
    public void BuildIdentity_CarriesMoreThanTheVersion()
    {
        var identity = ServerClient.BuildIdentity;

        var slash = identity.IndexOf('/');
        Assert.True(slash > 0, $"expected '<version>/<module id>', got '{identity}'");
        Assert.NotEmpty(identity[(slash + 1)..]);
    }

    /// <summary>The identity has to survive the port file intact, or every relay is a mismatch.</summary>
    [Fact]
    public void BuildIdentity_ContainsNothingThePortFileFormatWouldMangle()
    {
        Assert.DoesNotContain("\n", ServerClient.BuildIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", ServerClient.BuildIdentity, StringComparison.Ordinal);
        Assert.Equal(ServerClient.BuildIdentity.Trim(), ServerClient.BuildIdentity);
    }

    [Fact]
    public void PortFile_CarriageReturns_AreTolerated()
    {
        var endpoint = ServerClient.ParsePortFile("54321\r\nprotocol=2\r\nbuild=0.27.0/1a2b3c4d\r\n");

        Assert.NotNull(endpoint);
        Assert.Equal(54321, endpoint!.Port);
        Assert.Equal(2, endpoint.Protocol);
        Assert.Equal("0.27.0/1a2b3c4d", endpoint.Build);
    }
}
