using System.Runtime.InteropServices;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Tray;

internal enum HotkeyAvailability
{
    Free,
    Taken,
    HeldByThisApp,
}

/// <summary>
/// Global hotkeys that go to Home Assistant as events. Only the combinations the user
/// put on the list are registered: Windows hands a registered combination to this app
/// alone, so every one of them is a key other programs no longer see. Nothing else that
/// is typed is looked at.
/// </summary>
internal sealed class HotkeyService : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ProbeId = 0xBFFF;

    // What this app holds right now, so that a check does not call our own hotkey taken.
    private static readonly HashSet<(uint Modifiers, uint VirtualKey)> Held = [];

    private readonly FileLog _log;
    private readonly Dictionary<int, HotkeyDefinition> _registered = [];

    // Hotkeys another program held when they were registered: tried again every minute,
    // so one starts working once that program lets it go.
    private readonly List<(HotkeyDefinition Hotkey, uint Modifiers, uint VirtualKey)> _waiting = [];
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 60_000 };
    private int _nextId = 1;

    public HotkeyService(FileLog log)
    {
        _log = log;
        // A message-only window: it exists to receive WM_HOTKEY and nothing else.
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        _retry.Tick += (_, _) => RetryWaiting();
    }

    /// <summary>A registered hotkey was pressed.</summary>
    public event EventHandler<HotkeyDefinition>? Pressed;

    /// <summary>Registers the enabled hotkeys of the list, replacing whatever was registered before.</summary>
    public void Apply(IEnumerable<HotkeyDefinition> hotkeys)
    {
        UnregisterAll();
        foreach (var hotkey in hotkeys.Where(hotkey => hotkey.Enabled))
        {
            if (!KeySender.TryParseHotkey(hotkey.Keys, out var modifiers, out var virtualKey))
            {
                _log.Warning($"Hotkey '{hotkey.Name}': '{hotkey.Keys}' is not a usable combination (a modifier and one key are needed).");
                continue;
            }

            if (!TryRegister(hotkey, modifiers, virtualKey))
            {
                // Usually another program holds the same combination.
                _log.Warning($"Hotkey '{hotkey.Name}' ({hotkey.Keys}) could not be registered; another program may already use it. It is tried again every minute.");
                _waiting.Add((hotkey, modifiers, virtualKey));
            }
        }

        if (_registered.Count > 0)
        {
            _log.Info($"{_registered.Count} hotkey(s) registered.");
        }

        _retry.Enabled = _waiting.Count > 0;
    }

    private bool TryRegister(HotkeyDefinition hotkey, uint modifiers, uint virtualKey)
    {
        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            return false;
        }

        _registered[id] = hotkey;
        Held.Add((modifiers, virtualKey));
        return true;
    }

    private void RetryWaiting()
    {
        for (var index = _waiting.Count - 1; index >= 0; index--)
        {
            var (hotkey, modifiers, virtualKey) = _waiting[index];
            if (TryRegister(hotkey, modifiers, virtualKey))
            {
                _log.Info($"Hotkey '{hotkey.Name}' ({hotkey.Keys}) is registered now; the program that held it let it go.");
                _waiting.RemoveAt(index);
            }
        }

        _retry.Enabled = _waiting.Count > 0;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && _registered.TryGetValue(message.WParam.ToInt32(), out var hotkey))
        {
            Pressed?.Invoke(this, hotkey);
        }

        base.WndProc(ref message);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(Handle, id);
        }

        _registered.Clear();
        Held.Clear();
        _waiting.Clear();
        _retry.Enabled = false;
    }

    /// <summary>
    /// Whether Windows would give this combination to the app: registering it and letting
    /// it go at once is the only way Windows tells. On the UI thread, like the hotkeys.
    /// </summary>
    public static HotkeyAvailability Check(uint modifiers, uint virtualKey)
    {
        if (Held.Contains((modifiers, virtualKey)))
        {
            return HotkeyAvailability.HeldByThisApp;
        }

        if (!RegisterHotKey(IntPtr.Zero, ProbeId, modifiers | ModNoRepeat, virtualKey))
        {
            return HotkeyAvailability.Taken;
        }

        UnregisterHotKey(IntPtr.Zero, ProbeId);
        return HotkeyAvailability.Free;
    }

    public void Dispose()
    {
        UnregisterAll();
        _retry.Dispose();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
