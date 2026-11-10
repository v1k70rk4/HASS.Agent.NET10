using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace HASS.Agent.Companion.Logging;

/// <summary>
/// Where an exception was thrown, as "Type.Method", for the log line of a failure that is
/// rare and cannot be made to happen again on demand. "Object reference not set to an
/// instance of an object" alone says nothing about which of a dozen calls it was.
/// </summary>
internal static partial class ExceptionPlace
{
    public static string Of(Exception exception) => Of(exception, typeof(ExceptionPlace).Assembly);

    /// <summary>
    /// The first method of <paramref name="own"/> on the way to the throw, and the method that
    /// threw when that is someone else's: "MediaSessionService.GetCurrentSession (in Number.Parse)".
    /// </summary>
    internal static string Of(Exception exception, Assembly own)
    {
        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        var thrower = frames.Select(frame => frame.GetMethod()).FirstOrDefault(method => method is not null);
        if (thrower is null)
        {
            return "an unknown place";
        }

        var ours = frames
            .Select(frame => frame.GetMethod())
            .FirstOrDefault(method => method?.Module.Assembly == own);
        if (ours is null || ours == thrower)
        {
            return Name(thrower);
        }

        return $"{Name(ours)} (in {Name(thrower)})";
    }

    private static string Name(MethodBase method)
    {
        // The compiler's own methods carry the one they came from in angle brackets: an async
        // method is MoveNext of a type named <Method>d__1, a lambda is <Method>b__2 of <>c.
        var type = method.DeclaringType;
        var name = method.Name;
        var written = CompilerName().Match(name);
        if (!written.Success && type is not null)
        {
            written = CompilerName().Match(type.Name);
        }

        if (written.Success)
        {
            name = written.Groups[1].Value;
        }

        while (type is not null && type.Name.StartsWith('<'))
        {
            type = type.DeclaringType;
        }

        return type is null ? name : $"{type.Name}.{name}";
    }

    [GeneratedRegex(@"^<(\w+)>")]
    private static partial Regex CompilerName();
}
