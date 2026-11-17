using System.Net;
using System.Net.Sockets;
using System.Text;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

public class UpdateProxyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData("  proxy.lan:3128 ", "http://proxy.lan:3128/")]
    [InlineData("https://proxy.example:443", "https://proxy.example/")]
    [InlineData("socks5://127.0.0.1:10808", "socks5://127.0.0.1:10808/")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080/")]
    public void A_proxy_address_is_read(string text, string expected)
    {
        Assert.True(UpdateProxy.TryParseAddress(text, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("http://user:secret@127.0.0.1:7890")] // the login has fields of its own
    [InlineData("http://127.0.0.1:7890/path")]
    [InlineData("http://127.0.0.1:7890?x=1")]
    [InlineData("http://127.0.0.1 :7890")]
    [InlineData("file:///C:/proxy.pac")]
    public void Not_a_proxy_address_is_refused(string text)
    {
        Assert.False(UpdateProxy.TryParseAddress(text, out _));
    }

    [Fact]
    public void The_windows_proxy_is_the_default_with_the_windows_login()
    {
        using var handler = UpdateProxy.From(new CompanionSettings()).CreateHandler();

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy); // HttpClient.DefaultProxy: what Windows is set to
        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
    }

    [Fact]
    public void The_windows_proxy_gets_the_login_typed_in()
    {
        using var handler = UpdateProxy.Create(false, "ignored", true, "office\\anna", "pw").CreateHandler();

        var login = Assert.IsType<NetworkCredential>(handler.DefaultProxyCredentials);
        Assert.Equal("office\\anna", login.UserName);
        Assert.Equal("pw", login.Password);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void A_proxy_set_here_never_gets_the_windows_login()
    {
        using var handler = UpdateProxy.Create(true, "127.0.0.1:7890", false, "", "").CreateHandler();

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890/"), proxy.Address);
        Assert.Null(proxy.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
    }

    [Fact]
    public void Without_the_tick_the_user_name_and_password_are_not_used()
    {
        var proxy = UpdateProxy.Create(true, "127.0.0.1:7890", false, "anna", "pw");

        Assert.Null(proxy.Login);
    }

    [Fact]
    public void A_proxy_address_that_cannot_be_used_fails_the_check_instead_of_going_around_it()
    {
        var proxy = UpdateProxy.Create(true, "not a proxy", false, null, null);

        Assert.NotNull(proxy.Problem);
        Assert.Throws<InvalidOperationException>(() => proxy.CreateHandler());
    }

    [Fact]
    public async Task An_address_with_a_password_in_it_is_refused_without_showing_the_password()
    {
        var proxy = UpdateProxy.Create(true, "http://anna:very-secret@proxy.lan:3128", false, null, null);

        var state = await AppUpdateService.CheckAsync("10.13.1", false, proxy, TestContext.Current.CancellationToken);

        Assert.NotNull(state.Error);
        Assert.Contains("cannot be used", state.Error);
        Assert.DoesNotContain("very-secret", state.Error);
        Assert.DoesNotContain("very-secret", proxy.ToString());
    }

    [Fact]
    public void The_log_line_never_has_the_password()
    {
        var text = UpdateProxy.Create(true, "http://127.0.0.1:7890", true, "anna", "very-secret").ToString();

        Assert.Contains("127.0.0.1:7890", text);
        Assert.Contains("anna", text);
        Assert.DoesNotContain("very-secret", text);
    }

    [Fact]
    public void The_proxy_password_is_kept_protected_and_left_out_of_an_export()
    {
        var settings = new CompanionSettings { UpdateProxyCustom = true, UpdateProxyAddress = "127.0.0.1:7890", UpdateProxyLogin = true, UpdateProxyUser = "anna" };
        settings.Normalize();
        settings.SetUpdateProxyPassword("secret-proxy");

        Assert.DoesNotContain("secret-proxy", settings.UpdateProxyPasswordProtected);
        Assert.Equal("secret-proxy", settings.GetUpdateProxyPassword());
        var json = SettingsStore.ExportJson(settings);
        Assert.DoesNotContain("secret-proxy", json);
        Assert.DoesNotContain("updateProxyPasswordProtected", json);
        Assert.Contains("\"updateProxyUser\": \"anna\"", json);
    }

    [Fact]
    public async Task A_request_goes_through_the_proxy_set_here_with_its_login()
    {
        using var proxyServer = new LoginProxy("anna", "pw");
        var proxy = UpdateProxy.Create(true, $"127.0.0.1:{proxyServer.Port}", true, "anna", "pw");
        using var http = new HttpClient(proxy.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };

        // A name that does not resolve: only the proxy can answer it.
        var body = await http.GetStringAsync("http://update-check.invalid/releases", TestContext.Current.CancellationToken);

        Assert.Equal("through the proxy", body);
        Assert.Equal("GET http://update-check.invalid/releases HTTP/1.1", proxyServer.LastRequestLine);
        Assert.True(proxyServer.Challenged); // asked for the login first, as a proxy does
    }

    [Fact]
    public async Task A_wrong_password_is_refused_by_the_proxy()
    {
        using var proxyServer = new LoginProxy("anna", "pw");
        var proxy = UpdateProxy.Create(true, $"127.0.0.1:{proxyServer.Port}", true, "anna", "wrong");
        using var http = new HttpClient(proxy.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };

        var response = await http.GetAsync("http://update-check.invalid/releases", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
    }

    /// <summary>
    /// A plain HTTP proxy on 127.0.0.1 that asks for a Basic login and answers every request
    /// itself: nothing goes out to the internet.
    /// </summary>
    private sealed class LoginProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _expected;
        private readonly CancellationTokenSource _stop = new();

        public LoginProxy(string user, string password)
        {
            _expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public string? LastRequestLine { get; private set; }

        public bool Challenged { get; private set; }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                while (!_stop.IsCancellationRequested)
                {
                    var requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        return;
                    }

                    string? authorization = null;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            authorization = line["Proxy-Authorization:".Length..].Trim();
                        }
                    }

                    LastRequestLine = requestLine;
                    string response;
                    if (authorization != _expected)
                    {
                        Challenged = true;
                        response = "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n";
                    }
                    else
                    {
                        const string body = "through the proxy";
                        response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}";
                    }

                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
