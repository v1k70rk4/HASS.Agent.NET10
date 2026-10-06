using System.Diagnostics;
using System.Windows.Forms;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// The dialogs of the About page's update check: a task dialog with the versions, the
/// release notes under an expander and buttons in the app's language. A message box gets
/// its buttons from Windows, so an English app asked "Igen / Nem" on a Hungarian PC, and
/// the notes, pasted in front of the question, made it a wall of text.
/// </summary>
internal static class UpdatePromptDialog
{
    /// <summary>True when the user chose to download the release.</summary>
    public static bool ShowUpdateAvailable(Form owner, AppUpdateState update)
    {
        var download = new TaskDialogButton(Strings.Get("About.UpdateDownload"));
        var notNow = new TaskDialogButton(Strings.Get("About.UpdateNotNow"));
        var page = new TaskDialogPage
        {
            Caption = AppIdentity.DisplayName,
            Heading = string.Format(Strings.Get("About.UpdateHeading"), DisplayVersion(update.LatestVersion)),
            Text = string.Format(Strings.Get("About.UpdateInstalled"), DisplayVersion(update.InstalledVersion)),
            Icon = owner.Icon is { } icon ? new TaskDialogIcon(icon) : TaskDialogIcon.Information,
            AllowCancel = true,
            SizeToContent = true,
            Buttons = { download, notNow },
            DefaultButton = download,
            EnableLinks = true,
        };

        var notes = AppUpdateService.SummarizeReleaseNotes(update.ReleaseNotes);
        if (notes.Length > 0)
        {
            page.Expander = new TaskDialogExpander
            {
                Text = notes,
                Expanded = true,
                CollapsedButtonText = Strings.Get("About.WhatsNew"),
                ExpandedButtonText = Strings.Get("About.UpdateHideNotes"),
                Position = TaskDialogExpanderPosition.AfterText,
            };
        }

        var releaseUrl = update.ReleaseUrl ?? $"{AppIdentity.GitHubRepositoryUrl}/releases/latest";
        page.Footnote = new TaskDialogFootnote($"<a href=\"{releaseUrl}\">{Strings.Get("About.UpdateReleaseNotesLink")}</a>");
        page.LinkClicked += (_, e) => OpenUrl(e.LinkHref);

        return TaskDialog.ShowDialog(owner, page) == download;
    }

    /// <summary>True when the user chose to run the downloaded installer now.</summary>
    public static bool ShowDownloaded(Form owner, string installerPath)
    {
        var install = new TaskDialogButton(Strings.Get("About.UpdateInstallNow"));
        var later = new TaskDialogButton(Strings.Get("About.UpdateLater"));
        var page = new TaskDialogPage
        {
            Caption = AppIdentity.DisplayName,
            Heading = Strings.Get("About.UpdateDownloadedHeading"),
            Text = string.Format(Strings.Get("About.UpdateDownloadedText"), installerPath),
            Icon = owner.Icon is { } icon ? new TaskDialogIcon(icon) : TaskDialogIcon.Information,
            AllowCancel = true,
            SizeToContent = true,
            Buttons = { install, later },
            DefaultButton = install,
        };

        return TaskDialog.ShowDialog(owner, page) == install;
    }

    /// <summary>The tag name without its leading "v": the caption already names the app.</summary>
    private static string DisplayVersion(string version)
    {
        return version.Length > 1 && (version[0] == 'v' || version[0] == 'V') && char.IsDigit(version[1])
            ? version[1..]
            : version;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // The browser is the user's business; nothing to do when it will not start.
        }
    }
}
