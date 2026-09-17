using System.Runtime.InteropServices;
using System.Text;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Windows' own console tools (sc, powercfg, schtasks) write their localized messages in the
/// OEM code page - 852 on a Hungarian system - and not in UTF-8 or the ANSI code page. Read
/// as anything else, every accented letter comes out as a replacement character.
/// </summary>
internal static class ConsoleOutputEncoding
{
    public static Encoding Oem { get; } = Create();

    private static Encoding Create()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
