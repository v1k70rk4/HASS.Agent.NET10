using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Security;

namespace HASS.Agent.Companion.Tests;

public class LocalApiKeyTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("Bearer " + Key)]
    [InlineData("bearer " + Key)]
    [InlineData("Bearer   " + Key + "  ")]
    public void Right_key_is_let_in(string header)
    {
        Assert.True(LocalApiServer.IsAuthorized(header, Key));
    }

    [Theory]
    [InlineData("Bearer 0123456789abcdef0123456789abcdee")] // one character off
    [InlineData("Bearer " + Key + "0")]                      // longer
    [InlineData("Bearer 0123")]                              // a prefix of the key
    [InlineData("Bearer ")]
    [InlineData("Basic " + Key)]
    [InlineData(Key)]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? header)
    {
        Assert.False(LocalApiServer.IsAuthorized(header, Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_key_nothing_is_let_in(string? key)
    {
        Assert.False(LocalApiServer.IsAuthorized("Bearer ", key));
        Assert.False(LocalApiServer.IsAuthorized("Bearer anything", key));
    }
}

/// <summary>The HA WebSocket's receive side, on a loopback socket pair: no Home Assistant needed.</summary>
public class HaWebSocketMessageSizeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_message_past_the_cap_closes_the_connection(bool fragmented)
    {
        using var pair = await SocketPair.ConnectAsync();
        var big = Encoding.UTF8.GetBytes("{\"type\":\"event\",\"pad\":\"" + new string('x', HaWebSocketService.MaxIncomingMessage) + "\"}");
        _ = fragmented ? pair.SendInPiecesAsync(big, 64 * 1024) : pair.Server.SendAsync(big, WebSocketMessageType.Text, true, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => HaWebSocketService.ReceiveMessageAsync(pair.Client, CancellationToken.None));

        Assert.Equal(HaWebSocketService.MessageTooBig, ex.Message);
        Assert.Equal(WebSocketState.CloseSent, pair.Client.State);
    }

    [Fact]
    public async Task A_message_within_the_cap_is_read_whole_fragments_and_all()
    {
        using var pair = await SocketPair.ConnectAsync();
        var json = Encoding.UTF8.GetBytes("{\"type\":\"pong\",\"pad\":\"" + new string('x', 200_000) + "\"}");
        _ = pair.SendInPiecesAsync(json, 4096);

        using var message = await HaWebSocketService.ReceiveMessageAsync(pair.Client, CancellationToken.None);

        Assert.Equal("pong", message.RootElement.GetProperty("type").GetString());
        Assert.Equal(200_000, message.RootElement.GetProperty("pad").GetString()!.Length);
    }

    private sealed class SocketPair : IDisposable
    {
        private readonly TcpClient _clientTcp;
        private readonly TcpClient _serverTcp;

        public WebSocket Client { get; }
        public WebSocket Server { get; }

        private SocketPair(TcpClient clientTcp, TcpClient serverTcp)
        {
            _clientTcp = clientTcp;
            _serverTcp = serverTcp;
            Client = WebSocket.CreateFromStream(clientTcp.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
            Server = WebSocket.CreateFromStream(serverTcp.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        }

        public static async Task<SocketPair> ConnectAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptTcpClientAsync();
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new SocketPair(client, await accepting);
            }
            finally
            {
                listener.Stop();
            }
        }

        public async Task SendInPiecesAsync(byte[] message, int pieceSize)
        {
            try
            {
                for (var offset = 0; offset < message.Length; offset += pieceSize)
                {
                    var count = Math.Min(pieceSize, message.Length - offset);
                    await Server.SendAsync(new ArraySegment<byte>(message, offset, count), WebSocketMessageType.Text,
                        endOfMessage: offset + count == message.Length, CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // The receiver dropped the connection: what the test is about.
            }
        }

        public void Dispose()
        {
            Client.Abort();
            Server.Abort();
            _clientTcp.Dispose();
            _serverTcp.Dispose();
        }
    }
}

public class InstallerSignatureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Unsigned_program_is_refused()
    {
        // This test assembly is built here and never signed.
        var path = Path.Combine(_folder, "unsigned.dll");
        File.Copy(typeof(InstallerSignatureTests).Assembly.Location, path);

        Assert.False(InstallerSignature.IsTrusted(path, out var reason));
        Assert.Contains("no signature", reason);
        Assert.Throws<InvalidOperationException>(() => InstallerSignature.EnsureTrusted(path));
    }

    [Fact]
    public void Something_that_is_not_a_program_is_refused()
    {
        var path = Path.Combine(_folder, "HASS.Agent.NET10-Setup-10.9.1.exe");
        File.WriteAllText(path, "<html>Not Found</html>");

        Assert.False(InstallerSignature.IsTrusted(path, out _));
    }

    [Fact]
    public void Missing_file_is_refused()
    {
        Assert.False(InstallerSignature.IsTrusted(Path.Combine(_folder, "nothing.exe"), out _));
    }

    // The real installers are too big for the repository. CI points HASS_AGENT_SIGNED_INSTALLER at
    // the latest release's installer; locally, point it at one in artifacts\installer (build-exe.ps1).
    private static string? SignedInstaller =>
        Environment.GetEnvironmentVariable("HASS_AGENT_SIGNED_INSTALLER") is { Length: > 0 } path && File.Exists(path) ? path : null;

    [Fact]
    public void Signed_release_installer_is_trusted()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        Assert.True(InstallerSignature.IsTrusted(SignedInstaller!, out var reason), reason);
    }

    [Fact]
    public void Release_installer_carries_a_timestamp()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        Assert.True(InstallerSignature.HasTimestamp(SignedInstaller!));
    }

    [Fact]
    public void Program_without_a_signature_has_no_timestamp()
    {
        Assert.False(InstallerSignature.HasTimestamp(typeof(InstallerSignatureTests).Assembly.Location));
    }

    [Fact]
    public void Truncated_program_has_no_timestamp()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        // The first 4 KB of a signed installer: the headers point at a certificate table
        // that is not there any more.
        var path = Path.Combine(_folder, "truncated.exe");
        using (var source = File.OpenRead(SignedInstaller!))
        using (var target = File.Create(path))
        {
            var head = new byte[4096];
            target.Write(head, 0, source.Read(head, 0, head.Length));
        }

        Assert.False(InstallerSignature.HasTimestamp(path));
        Assert.False(InstallerSignature.IsTrusted(path, out _));
    }

    [Fact]
    public void Signed_installer_changed_by_one_byte_is_refused()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        var path = Path.Combine(_folder, "tampered.exe");
        var bytes = File.ReadAllBytes(SignedInstaller!);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.False(InstallerSignature.IsTrusted(path, out var reason));
        Assert.Contains("changed after it was signed", reason);
    }
}
