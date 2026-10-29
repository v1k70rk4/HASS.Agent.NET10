using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.Security;

namespace HASS.Agent.Companion.Configuration;

/// <summary>
/// Kiosk mode, for a PC where the sensors need the tray app but the person in front of the
/// screen must not change anything. The tray app runs for them, and shows nothing but "Kiosk
/// mode" in the tray: no window, no menu, no Exit. The users named here keep the full app, and
/// so does every administrator. The file sits next to the program, in a folder only
/// administrators can write, like the service policy, so a kiosk user cannot take themselves
/// off it; the settings folder lets the kiosk users read and not change
/// (SettingsAccess.SetFolderRule). Written by the app run as administrator (--kiosk). Without
/// the file kiosk mode is off.
/// </summary>
internal sealed class KioskPolicy
{
    public const string FileName = "kiosk.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The users who keep the full app, by SID.</summary>
    [JsonPropertyName("fullApp")]
    public List<string> FullApp { get; init; } = [];

    /// <summary>The policy file next to the running program.</summary>
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);

    /// <summary>
    /// The policy in the file, or null when there is none: kiosk mode is off. A file that is
    /// there but cannot be read keeps kiosk mode on with nobody named, so a broken file never
    /// hands the full app to everyone; the administrators still have it.
    /// </summary>
    public static KioskPolicy? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KioskPolicy>(File.ReadAllText(path), JsonOptions) ?? new KioskPolicy();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new KioskPolicy();
        }
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        SafeFiles.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The users who keep the full app; an entry that is not a SID is nobody.</summary>
    public IReadOnlyList<SecurityIdentifier> FullAppSids()
    {
        var sids = new List<SecurityIdentifier>();
        foreach (var entry in FullApp)
        {
            try
            {
                sids.Add(new SecurityIdentifier(entry));
            }
            catch (ArgumentException)
            {
                // Not written by this app; it names no one.
            }
        }

        return sids.Distinct().ToList();
    }

    /// <summary>Whether this user gets the kiosk tray: neither named here nor an administrator.</summary>
    public bool IsKioskUser(SecurityIdentifier? user, bool administrator) =>
        !administrator && (user is null || !FullAppSids().Contains(user));

    /// <summary>Kiosk mode is on, and the user running this gets the kiosk tray.</summary>
    public static bool AppliesToCurrentUser()
    {
        var policy = Load(DefaultPath);
        if (policy is null)
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return policy.IsKioskUser(identity.User, IsAdministrator(identity));
    }

    /// <summary>
    /// An administrator of this PC, also when the app runs without elevation: with UAC an
    /// administrator gets a limited token, in which the Administrators group only denies, so
    /// asking for the role would say no. The elevation type of the token says it either way;
    /// without UAC there is one token, and the role is asked.
    /// </summary>
    internal static bool IsAdministrator(WindowsIdentity identity)
    {
        if (GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _)
            && type is TokenElevationTypeFull or TokenElevationTypeLimited)
        {
            return true;
        }

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private const int TokenElevationType = 18;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
}
