using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.Tray;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The popup windows as Windows shows them: what closing, hiding, the time set for a window
/// and a second press really do to a window. They put small empty windows on the screen for a
/// few seconds, so they run only when asked for: set HASS_AGENT_WINDOW_TESTS=1. No other
/// test makes such a window, and these run one after the other, so every window of the kind
/// that is open belongs to the test that is running.
/// </summary>
public sealed class PopupWindowOnScreenTests
{
    private const string Blank = "about:blank";
    private static readonly Size Small = new(320, 240);

    [Fact]
    public void A_window_that_does_not_keep_its_page_is_closed_when_it_goes()
    {
        OnAWindowThread(log =>
        {
            var window = new WebViewForm(Blank, Small, popup: false, log);
            window.Show();
            Pump(300);
            Assert.True(window.Visible);

            window.Dismiss();
            Pump(300);

            Assert.True(window.IsDisposed);
        });
    }

    [Fact]
    public void A_window_that_keeps_its_page_is_hidden_and_comes_back()
    {
        OnAWindowThread(log =>
        {
            var gone = 0;
            var window = new WebViewForm(Blank, Small, popup: false, log, keepLoaded: true);
            window.Dismissed += (_, _) => gone++;
            window.Show();
            Pump(300);

            window.Dismiss();
            Pump(200);
            Assert.False(window.IsDisposed);
            Assert.False(window.Visible);
            Assert.Equal(1, gone);

            window.ShowAgain();
            Pump(200);
            Assert.True(window.Visible);

            // Its own X, which is what Close() is to a window, hides it as well.
            window.Close();
            Pump(200);
            Assert.False(window.IsDisposed);
            Assert.False(window.Visible);

            window.CloseForGood();
            Pump(300);
            Assert.True(window.IsDisposed);
        });
    }

    [Fact]
    public void The_time_set_for_a_window_sends_it_away()
    {
        OnAWindowThread(log =>
        {
            var closed = new WebViewForm(Blank, Small, popup: false, log, keepLoaded: false, closeAfter: TimeSpan.FromSeconds(1));
            var hidden = new WebViewForm(Blank, Small, popup: false, log, keepLoaded: true, closeAfter: TimeSpan.FromSeconds(1));
            closed.Show();
            hidden.Show();
            Pump(400);
            Assert.True(closed.Visible);
            Assert.True(hidden.Visible);

            Pump(4000, () => closed.IsDisposed && !hidden.Visible);

            Assert.True(closed.IsDisposed);
            Assert.False(hidden.IsDisposed);
            Assert.False(hidden.Visible);

            // Shown again, its time starts again.
            hidden.ShowAgain();
            Pump(400);
            Assert.True(hidden.Visible);
            Pump(4000, () => !hidden.Visible);
            Assert.False(hidden.Visible);

            hidden.CloseForGood();
            Pump(300);
        });
    }

    [Fact]
    public void A_command_has_one_window_fresh_or_kept_and_a_close_finds_it_by_name()
    {
        OnAWindowThread(log =>
        {
            using var popups = new PopupWindows(log);
            var fresh = new PopupWindowRequest("custom_1", "Doorbell camera", Blank, new PopupWindowOptions(Small, KeepLoaded: false, CloseAfter: null));
            var kept = new PopupWindowRequest("custom_2", "Player", Blank, new PopupWindowOptions(Small, KeepLoaded: true, CloseAfter: null));

            // Pressed twice: the first window goes, a new one comes.
            popups.ShowForCommand(fresh);
            Pump(300);
            var first = Assert.Single(Windows());
            popups.ShowForCommand(fresh);
            Pump(300);
            var second = Assert.Single(Windows());
            Assert.NotSame(first, second);
            Assert.True(first.IsDisposed);

            // A kept one pressed twice is the same window.
            popups.ShowForCommand(kept);
            Pump(300);
            var player = Assert.Single(Windows(), window => window.KeepLoaded);
            popups.ShowForCommand(kept);
            Pump(200);
            Assert.Same(player, Assert.Single(Windows(), window => window.KeepLoaded));

            // A close by name is for that command only: the kept one is hidden, the other stays.
            Assert.Equal(1, popups.Close("player"));
            Pump(200);
            Assert.False(player.IsDisposed);
            Assert.False(player.Visible);
            Assert.True(second.Visible);
            Assert.Equal(0, popups.Close("player"));

            // Pressed again it is back, the same window; asked for another way, it is a new one.
            popups.ShowForCommand(kept);
            Pump(200);
            Assert.True(player.Visible);
            popups.ShowForCommand(kept with { Options = kept.Options with { Size = new Size(400, 300) } });
            Pump(300);
            Assert.True(player.IsDisposed);
            Assert.Equal(2, Windows().Count);

            // A close for all of them.
            Assert.Equal(2, popups.Close("*"));
            Pump(300);
            Assert.True(second.IsDisposed);
            var left = Assert.Single(Windows());
            Assert.False(left.Visible);

            popups.Dispose();
            Pump(300);
            Assert.Empty(Windows());
        });
    }

    [Fact]
    public void A_dashboard_kept_loaded_is_one_window_until_the_settings_say_otherwise()
    {
        OnAWindowThread(log =>
        {
            using var popups = new PopupWindows(log);

            popups.ShowDashboard(Blank, Small, keepLoaded: true);
            Pump(300);
            var dashboard = Assert.Single(Windows());
            popups.ShowDashboard(Blank, Small, keepLoaded: true);
            Pump(200);
            Assert.Same(dashboard, Assert.Single(Windows()));

            // The same settings saved again leave it alone, and so does "Open now", which has
            // a popup of its own.
            popups.SettingsChanged(Blank, Small, keepLoaded: true, []);
            popups.ShowPreview(Blank, new Size(400, 300));
            Pump(300);
            Assert.False(dashboard.IsDisposed);
            Assert.InRange(Windows().Count, 1, 2);
            popups.ShowPreview(Blank, new Size(400, 300));
            Pump(300);
            Assert.False(dashboard.IsDisposed);
            Assert.InRange(Windows().Count, 1, 2);

            // Another size, or the tick cleared, lets the page go at once.
            popups.SettingsChanged(Blank, new Size(400, 300), keepLoaded: true, []);
            Pump(300);
            Assert.True(dashboard.IsDisposed);

            popups.Dispose();
            Pump(300);
            Assert.Empty(Windows());
        });
    }

    [Fact]
    public void Saving_the_settings_sorts_out_the_windows_of_the_commands()
    {
        OnAWindowThread(log =>
        {
            using var popups = new PopupWindows(log);
            CustomCommandDefinition Command(string id, string name, string window) => new()
            {
                Id = id, Name = name, Type = CustomCommandTypes.WebView, Command = Blank, Arguments = window
            };
            PopupWindowRequest Press(CustomCommandDefinition command)
            {
                Assert.True(WebViewOptions.TryParseWindow(command.Arguments, out var options));
                return new PopupWindowRequest(command.Id, command.Name, command.Command, options);
            }

            var camera = Command("custom_1", "Doorbell camera", "320x240");
            var player = Command("custom_2", "Player", "320x240 keep");
            var radio = Command("custom_3", "Radio", "320x240 keep");
            var gone = Command("custom_4", "Gone", "320x240 keep");
            foreach (var command in new[] { camera, player, radio, gone })
            {
                popups.ShowForCommand(Press(command));
            }

            Pump(400);
            Assert.Equal(4, Windows().Count);
            Assert.Equal(3, popups.Close("player") + popups.Close("radio") + popups.Close("gone"));
            Pump(200);

            // The camera is as it was, the player has a new name, the radio no longer keeps its
            // page, and the fourth command is not there any more.
            player.Name = "Music";
            radio.Arguments = "320x240";
            popups.SettingsChanged(Blank, Small, keepLoaded: false, [camera, player, radio]);
            Pump(400);

            Assert.Equal(2, Windows().Count);
            Assert.Single(Windows(), window => window.Visible);
            Assert.Single(Windows(), window => window.KeepLoaded && !window.Visible);

            // The kept one is found by its new name, not by the old one.
            popups.ShowForCommand(Press(player));
            Pump(200);
            Assert.Equal(0, popups.Close("Player"));
            Assert.Equal(1, popups.Close("Music"));

            // A command that the tray app no longer runs has no window either.
            camera.Enabled = false;
            popups.SettingsChanged(Blank, Small, keepLoaded: false, [camera, player, radio]);
            Pump(300);
            Assert.Single(Windows());
        });
    }

    [Fact]
    public void A_window_asked_for_while_another_program_is_in_front_comes_over_it()
    {
        OnAWindowThread(log =>
        {
            // The tests run from a console or a build, never as the program in front: the place
            // a press from Home Assistant finds the app in.
            var inFront = GetForegroundWindow();
            Assert.SkipWhen(inFront == IntPtr.Zero, "No window is in front.");
            Assert.SkipWhen((GetWindowLong(inFront, -20) & 0x8) != 0, "The window in front is one that stays over all others.");

            using var popups = new PopupWindows(log);
            popups.ShowForCommand(new PopupWindowRequest("custom_1", "Camera", Blank, new PopupWindowOptions(Small, KeepLoaded: true, CloseAfter: null)));
            Pump(400);
            var window = Assert.Single(Windows());
            Assert.False(window.IsPopup);
            Assert.False(window.TopMost);
            Assert.True(IsOver(window.Handle, inFront));
            Assert.Equal(inFront, GetForegroundWindow());

            // Hidden and asked for again, it is over the others again.
            popups.Close("*");
            Pump(200);
            popups.ShowForCommand(new PopupWindowRequest("custom_1", "Camera", Blank, new PopupWindowOptions(Small, KeepLoaded: true, CloseAfter: null)));
            Pump(300);
            Assert.True(window.Visible);
            Assert.True(IsOver(window.Handle, inFront));
        });
    }

    [Fact]
    public void A_window_that_asks_for_it_is_one_like_the_dashboard_popup()
    {
        OnAWindowThread(log =>
        {
            using var popups = new PopupWindows(log);
            var request = new PopupWindowRequest("custom_1", "Camera", Blank, new PopupWindowOptions(Small, KeepLoaded: true, CloseAfter: null, AboveTray: true));
            popups.ShowForCommand(request);
            Pump(400);

            var window = Assert.Single(Windows());
            Assert.True(window.IsPopup);
            Assert.True(window.TopMost);
            Assert.Equal(FormBorderStyle.None, window.FormBorderStyle);
            Assert.False(window.ShowInTaskbar);

            // In the corner of the screen the pointer is on, where the tray is, not in the middle.
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Assert.InRange(area.Right - window.Bounds.Right, 0, 40);
            Assert.InRange(area.Bottom - window.Bounds.Bottom, 0, 40);

            // Asked for as an ordinary window instead, it is made anew.
            popups.ShowForCommand(request with { Options = request.Options with { AboveTray = false } });
            Pump(400);
            Assert.True(window.IsDisposed);
            Assert.False(Assert.Single(Windows()).IsPopup);
        });
    }

    // Whether a window is above another in the order Windows draws them in.
    private static bool IsOver(IntPtr window, IntPtr other)
    {
        for (var above = GetWindow(other, 3); above != IntPtr.Zero; above = GetWindow(above, 3)) // GW_HWNDPREV
        {
            if (above == window)
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint which);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    private static List<WebViewForm> Windows() =>
        Application.OpenForms.OfType<WebViewForm>().Where(window => !window.IsDisposed).ToList();

    // Lets the windows of this thread live for a while, or until something is so.
    private static void Pump(int milliseconds, Func<bool>? until = null)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end && until?.Invoke() != true)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }

        Application.DoEvents();
    }

    // Windows need a thread of their own kind, and the tests of a class run one after the other,
    // so the windows of one test are never there for the next.
    private static void OnAWindowThread(Action<FileLog> test)
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("HASS_AGENT_WINDOW_TESTS") == "1",
            "HASS_AGENT_WINDOW_TESTS is not set: this test shows small windows on the screen for a few seconds.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var log = new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
            try
            {
                test(log);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                foreach (var window in Windows())
                {
                    window.CloseForGood();
                }

                Pump(300);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
