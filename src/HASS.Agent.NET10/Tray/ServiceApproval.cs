using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.Security;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemService;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// After the settings are saved: when they ask the service to run a command or a command
/// sensor that no administrator has approved yet, the app says so and asks Windows to run
/// itself as administrator to approve it (see <see cref="ServicePolicy"/>). Declining keeps
/// everything working except that the service skips those.
/// </summary>
internal static class ServiceApproval
{
    public static async Task RequestIfNeededAsync(IWin32Window owner, CompanionSettings settings, FileLog log)
    {
        if (!CompanionServiceManager.IsInstalled())
        {
            return;
        }

        var current = ServicePolicy.Load(ServicePolicy.DefaultPath);
        var pending = PendingNames(settings, current);
        if (pending.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", pending);
        // What each one runs, in full and line by line, not only its name: the settings can be
        // written by others, and the administrator approves exactly what is shown here.
        var shown = string.Join(Environment.NewLine + Environment.NewLine, PendingDescriptions(settings, current));
        if (!Ask(owner, Strings.Get("SvcMgr.ApprovalNeeded"), shown))
        {
            log.Info($"Service approval skipped by the user: {names}.");
            return;
        }

        var (outcome, error) = await ElevatedRun.StartAsync($"--approve-service-commands --quiet --expect {current.PendingKey(settings)}");
        if (error is not null)
        {
            log.Warning($"Unable to start the service approval: {error}");
        }

        if (outcome == ElevatedOutcome.Busy)
        {
            // Not started at all: an earlier administrator run is still going. Nothing is
            // pending from this one, so it is not promised either.
            log.Warning($"The service approval was not started, an earlier administrator run is still going: {names}.");
            MessageBox.Show(owner, Strings.Get("Access.Busy"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (outcome == ElevatedOutcome.StillRunning)
        {
            // Not a refusal: the approval may still finish, and the service picks it up when it does.
            log.Warning($"The service approval has not finished yet: {names}.");
            MessageBox.Show(owner, string.Format(Strings.Get("SvcMgr.ApprovalPending"), names), AppIdentity.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var approved = ServicePolicy.Load(ServicePolicy.DefaultPath);
        if (PendingNames(settings, approved).Count == 0)
        {
            log.Info($"Approved for the service: {names}.");
            return;
        }

        log.Warning($"Not approved for the service: {names}.");
        MessageBox.Show(owner, string.Format(Strings.Get("SvcMgr.ApprovalDeclined"), names), AppIdentity.DisplayName,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// "Name [type]:" and below it everything it runs, whole and with its own line breaks: a
    /// script cut short, or put on one line, could hide the part after a comment sign. The name
    /// is on one line: the settings can be written by others, and a name with line breaks in it
    /// would read as entries of its own above the one that is approved.
    /// </summary>
    internal static IReadOnlyList<string> PendingDescriptions(CompanionSettings settings, ServicePolicy policy) =>
        settings.CustomCommands.Where(command => ServicePolicy.NeedsApproval(command) && !policy.Allows(command))
            .Select(command => Describe(
                command.Name,
                command.Type,
                string.IsNullOrEmpty(command.Arguments) ? command.Command : $"{command.Command} {command.Arguments}",
                TargetFile(command.Command, script: command.IsPowerShell || command.IsPwsh)))
            .Concat(settings.CustomSensors.Where(sensor => ServicePolicy.NeedsApproval(sensor) && !policy.Allows(sensor))
                .Select(sensor => Describe(
                    sensor.Name,
                    sensor.Type,
                    sensor.Parameter,
                    TargetFile(sensor.Parameter, script: sensor.IsCommandPowerShell || sensor.IsCommandPwsh))))
            .ToList();

    private static string Describe(string name, string type, string? runs, string? targetFile)
    {
        var lines = (runs ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => "    " + Visible(line)).ToList();
        // What is approved is the text above, not what the file it names says at the time.
        // The file's path is from that text: shown like it, with nothing hidden in it, or a
        // character in a folder's name could turn this line around.
        if (targetFile is not null && FileTrust.CanBeChangedWithoutAdministrator(targetFile))
        {
            lines.Add("    " + string.Format(Strings.Get("SvcMgr.ApprovalWritable"), Visible(targetFile)));
        }

        // Letters written from right to left are shown as they are, and can have the parts
        // around them shown in another order than they are in: said, in a line of its own.
        if (HasRightToLeftLetters(runs))
        {
            lines.Add("    " + Strings.Get("SvcMgr.ApprovalRightToLeft"));
        }

        return $"{OneLine(name)} [{OneLine(type)}]:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    /// <summary>
    /// The program, or the script file, an entry names by its full path; null when it names
    /// none (a command by its name alone, a PowerShell command typed out).
    /// </summary>
    internal static string? TargetFile(string? runs, bool script)
    {
        var text = (runs ?? string.Empty).Trim();
        var file = script ? text.Trim('"') : SystemCommandService.SplitProcessCommand(text).FileName;
        if (script && !file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.IsPathFullyQualified(file) ? file : null;
    }

    /// <summary>
    /// The text with every line break and control character in it turned into a space, and
    /// with them the characters that are not shown but change how the rest is (the ones that
    /// turn the reading direction around, for one: a name could read as something else).
    /// </summary>
    internal static string OneLine(string? text)
    {
        text ??= string.Empty;
        var line = new StringBuilder(text.Length);
        foreach (var (index, length, _, shown) in Characters(text))
        {
            if (shown)
            {
                line.Append(text, index, length);
            }
            else
            {
                line.Append(' ');
            }
        }

        return line.ToString().Trim();
    }

    /// <summary>
    /// A line of what a command runs, or the path of a file it names, with the characters that
    /// are not shown written out as their code, U+202E as &lt;U+202E&gt;: one that turns the
    /// reading direction around would have the administrator read another text than the one
    /// that is approved and run. Written out, not left out: what is shown stays the text that
    /// is approved, with nothing missing from it. Between angle brackets, which no name of a
    /// file or folder can have: in a path the code cannot be read as a part of the path, as a
    /// backslash and a letter before it could (a folder, and a file named u202E...). A command
    /// that has the code typed out in it, brackets and all, reads the same as one with the
    /// character; nothing is hidden by that.
    /// </summary>
    internal static string Visible(string line)
    {
        var visible = new StringBuilder(line.Length);
        foreach (var (index, length, code, shown) in Characters(line))
        {
            if (shown || code == '\t')
            {
                visible.Append(line, index, length);
            }
            else
            {
                visible.Append("<U+").Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append('>');
            }
        }

        return visible.ToString();
    }

    /// <summary>
    /// Whether the text has letters of a script written from right to left (Hebrew, Arabic and
    /// the others). No character has to be hidden for those to change what is read: the window
    /// lays them out from the right, and takes digits and separators next to them along, so
    /// the parts of a path can be shown in another order than they are in. They are shown as
    /// they are, since written out as codes a name in such a script could not be read at all;
    /// the prompt says so below the entry instead.
    /// </summary>
    internal static bool HasRightToLeftLetters(string? text)
    {
        foreach (var (_, _, code, _) in Characters(text ?? string.Empty))
        {
            // The blocks whose characters are right to left by default (Unicode's
            // bidirectional algorithm, UAX #9), assigned or not yet.
            if (code is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
                or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF))
            {
                return true;
            }
        }

        return false;
    }

    // The text character by character: where it is, how many chars long, its code, and
    // whether it is shown. By character, not by char: one past U+FFFF is two chars in a
    // string (a surrogate pair), and neither of the two says what the character is. Half of a
    // pair on its own is no character at all, and counts as not shown, under its own code.
    private static IEnumerable<(int Index, int Length, int Code, bool Shown)> Characters(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (!Rune.TryGetRuneAt(text, index, out var rune))
            {
                yield return (index, 1, text[index], false);
                index++;
                continue;
            }

            yield return (index, rune.Utf16SequenceLength, rune.Value, IsShown(rune));
            index += rune.Utf16SequenceLength;
        }
    }

    /// <summary>
    /// Whether the character is one that is seen: a letter, a mark on one (an accent written
    /// as a character of its own), a digit, punctuation, a symbol, or the plain space. Asked
    /// this way round, by what is known to be seen, and not by a list of what is not: the
    /// rest is control and format characters (the ones that turn the reading direction
    /// around), separators, the other spaces (a no-break space in a folder's name reads as
    /// "Program Files"), private and unassigned characters (the unassigned tags). Among
    /// the letters, marks and symbols a few are blank all the same, and are named here.
    /// </summary>
    internal static bool IsShown(Rune rune)
    {
        var code = rune.Value;
        if (code == ' ')
        {
            return true;
        }

        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol:
                break;
            default:
                return false;
        }

        return code is not (
            0x034F                              // combining grapheme joiner
            or 0x115F or 0x1160 or 0x3164 or 0xFFA0   // the Hangul fillers
            or 0x17B4 or 0x17B5                 // Khmer inherent vowels
            or (>= 0x180B and <= 0x180D) or 0x180F    // Mongolian variation selectors
            or 0x2800                           // the blank Braille pattern
            or (>= 0xFE00 and <= 0xFE0F)        // variation selectors
            or (>= 0xE0100 and <= 0xE01EF));    // variation selectors, the supplement
    }

    /// <summary>The question above, and what is to be approved in a box that scrolls, as long as it is.</summary>
    private static bool Ask(IWin32Window owner, string question, string details)
    {
        using var form = new Form
        {
            Text = AppIdentity.DisplayName,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9.5F),
            BackColor = Color.White,
        };
        int D(int value) => (int)(value * form.DeviceDpi / 96f);
        form.ClientSize = new Size(D(620), D(420));
        form.MinimumSize = new Size(D(420), D(300));

        var label = new Label { Text = question, Dock = DockStyle.Top, AutoSize = false, Height = D(92), Padding = new Padding(D(4)) };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            // Wrapped: what is approved is what is in view, not what a long line keeps to the right.
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.FromArgb(248, 250, 252),
            Text = details,
        };
        var ok = new Button { Text = Strings.Get("Editor.Ok"), DialogResult = DialogResult.OK, Size = new Size(D(96), D(30)) };
        var cancel = new Button { Text = Strings.Get("Btn.Cancel"), DialogResult = DialogResult.Cancel, Size = new Size(D(96), D(30)) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = D(44), Padding = new Padding(0, D(8), 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(D(12)) };
        body.Controls.Add(box);
        body.Controls.Add(buttons);
        body.Controls.Add(label);
        form.Controls.Add(body);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => box.Select(0, 0);

        return form.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>The names of what the settings ask the service to run and the policy does not approve.</summary>
    internal static IReadOnlyList<string> PendingNames(CompanionSettings settings, ServicePolicy policy) =>
        settings.CustomCommands.Where(command => ServicePolicy.NeedsApproval(command) && !policy.Allows(command)).Select(command => OneLine(command.Name))
            .Concat(settings.CustomSensors.Where(sensor => ServicePolicy.NeedsApproval(sensor) && !policy.Allows(sensor)).Select(sensor => OneLine(sensor.Name)))
            .ToList();
}
