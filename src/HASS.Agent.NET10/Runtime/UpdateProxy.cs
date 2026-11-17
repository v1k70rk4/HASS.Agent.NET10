using System.Net;
using HASS.Agent.Companion.Configuration;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// How the update checks and downloads reach GitHub, the app's only traffic to the internet:
/// through the proxy Windows is set to (as a browser does, its login included), or through
/// one set on the About page, for a proxy app that is not Windows' proxy (a local Clash or
/// v2rayN, say). Everything else the app talks to is on the home network and never goes
/// through it.
/// </summary>
internal sealed class UpdateProxy
{
    private static readonly string[] Schemes = ["http", "https", "socks4", "socks4a", "socks5"];

    private UpdateProxy(Uri? address, NetworkCredential? login, string? problem = null)
    {
        Address = address;
        Login = login;
        Problem = problem;
    }

    /// <summary>Why the proxy set here cannot be used (its address), or null.</summary>
    public string? Problem { get; }

    /// <summary>The proxy set on the About page, or null for the proxy Windows is set to.</summary>
    public Uri? Address { get; }

    /// <summary>The user name and password the proxy asks for, or null.</summary>
    public NetworkCredential? Login { get; }

    /// <summary>The proxy Windows is set to, with the Windows login: as before the setting.</summary>
    public static UpdateProxy Windows { get; } = new(null, null);

    public static UpdateProxy From(CompanionSettings settings) =>
        Create(settings.UpdateProxyCustom, settings.UpdateProxyAddress,
            settings.UpdateProxyLogin, settings.UpdateProxyUser, settings.GetUpdateProxyPassword());

    /// <summary>
    /// The proxy of these values. One whose address cannot be read carries a
    /// <see cref="Problem"/>, and the update check fails with it rather than going around the
    /// proxy someone set.
    /// </summary>
    public static UpdateProxy Create(bool custom, string? address, bool login, string? user, string? password)
    {
        var credential = login && !string.IsNullOrWhiteSpace(user)
            ? new NetworkCredential(user.Trim(), password ?? string.Empty)
            : null;
        if (!custom)
        {
            return new UpdateProxy(null, credential);
        }

        return TryParseAddress(address, out var uri)
            ? new UpdateProxy(uri, credential)
            : new UpdateProxy(null, null, $"the proxy address \"{address}\" cannot be used; it looks like http://127.0.0.1:7890");
    }

    /// <summary>
    /// "http://host:port", also "https://", "socks5://" and the like; "host:port" alone is
    /// taken as http. Only an address of a proxy: no path, no query, no user in it.
    /// </summary>
    public static bool TryParseAddress(string? text, out Uri address)
    {
        address = null!;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        address = uri;
        return true;
    }

    /// <summary>A handler for the update's HttpClient that goes through this proxy.</summary>
    public SocketsHttpHandler CreateHandler()
    {
        if (Problem is not null)
        {
            throw new InvalidOperationException(Problem);
        }

        var handler = new SocketsHttpHandler();
        if (Address is null)
        {
            // HttpClient.DefaultProxy: the proxy Windows is set to, a PAC script or automatic
            // detection included. A proxy that asks for a login gets the one typed in here, or
            // else the Windows login (NTLM / Kerberos): the signed-in user's in the tray app,
            // the computer account in the service.
            handler.DefaultProxyCredentials = Login ?? CredentialCache.DefaultCredentials;
        }
        else
        {
            // Only what was typed in here goes to a proxy set here, never the Windows login.
            handler.Proxy = new WebProxy(Address) { Credentials = Login };
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <summary>What the log says about it: never the password.</summary>
    public override string ToString() =>
        Problem is not null ? "a proxy whose address cannot be used" :
        (Address is null ? "the proxy Windows is set to" : $"the proxy {Address.GetLeftPart(UriPartial.Authority)}")
        + (Login is null ? string.Empty : $", as {Login.UserName}");
}
