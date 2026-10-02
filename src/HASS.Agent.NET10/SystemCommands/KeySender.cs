using System.Runtime.InteropServices;

namespace HASS.Agent.Companion.SystemCommands;

/// <summary>
/// Sends key presses for the "key" custom command type. The command text is one or more
/// key combinations separated by spaces or commas ("win+r", "ctrl+shift+esc",
/// "ctrl+c ctrl+v"); the keys of a combination are joined with "+".
/// </summary>
internal static class KeySender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(50);

    private readonly record struct Key(ushort VirtualKey, bool Extended = false);

    private static readonly Dictionary<string, Key> NamedKeys = BuildNamedKeys();

    /// <summary>
    /// Parses the command text. Returns false with the offending key name when a key is
    /// not known, so the caller can say which one.
    /// </summary>
    public static bool TryParse(string text, out IReadOnlyList<IReadOnlyList<ushort>> combinations, out string unknownKey)
    {
        unknownKey = string.Empty;
        var parsed = new List<IReadOnlyList<ushort>>();
        combinations = parsed;

        foreach (var combination in (text ?? string.Empty).Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var keys = new List<ushort>();
            foreach (var name in combination.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryResolve(name, out var key, out var modifiers))
                {
                    unknownKey = name;
                    return false;
                }

                // What the layout needs to produce the character (shift for "?"), unless
                // the combination already holds that modifier.
                foreach (var modifier in modifiers)
                {
                    if (!keys.Contains(modifier))
                    {
                        keys.Add(modifier);
                    }
                }

                keys.Add(key.VirtualKey);
            }

            if (keys.Count > 0)
            {
                parsed.Add(keys);
            }
        }

        return parsed.Count > 0;
    }

    /// <summary>
    /// One combination for a global hotkey: at least one modifier (ctrl, alt, shift, win)
    /// and exactly one other key, in RegisterHotKey's terms.
    /// </summary>
    public static bool TryParseHotkey(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (!TryParse(text, out var combinations, out _) || combinations.Count != 1)
        {
            return false;
        }

        foreach (var key in combinations[0])
        {
            switch (key)
            {
                case 0x12: modifiers |= 0x0001; break; // MOD_ALT
                case 0x11: modifiers |= 0x0002; break; // MOD_CONTROL
                case 0x10: modifiers |= 0x0004; break; // MOD_SHIFT
                case 0x5B: modifiers |= 0x0008; break; // MOD_WIN
                default:
                    if (virtualKey != 0)
                    {
                        return false;
                    }

                    virtualKey = key;
                    break;
            }
        }

        return modifiers != 0 && virtualKey != 0;
    }

    /// <summary>
    /// Presses the combinations one after the other. Returns false when Windows refused the
    /// input: the foreground window is elevated and the agent is not, or the desktop is locked.
    /// </summary>
    public static async Task<bool> SendAsync(IReadOnlyList<IReadOnlyList<ushort>> combinations)
    {
        for (var index = 0; index < combinations.Count; index++)
        {
            if (index > 0)
            {
                await Task.Delay(StepDelay);
            }

            if (!SendCombination(combinations[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SendCombination(IReadOnlyList<ushort> keys)
    {
        // All keys down in the order given, then up in reverse, as one batch so nothing
        // the user types can land in between.
        var inputs = new Input[keys.Count * 2];
        for (var index = 0; index < keys.Count; index++)
        {
            inputs[index] = BuildInput(keys[index], keyUp: false);
            inputs[inputs.Length - 1 - index] = BuildInput(keys[index], keyUp: true);
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length)
        {
            return true;
        }

        if (sent > 0)
        {
            // Only part of the batch went in: release every key, so that no modifier
            // stays held down for whatever the user types next.
            var releases = new Input[keys.Count];
            for (var index = 0; index < keys.Count; index++)
            {
                releases[index] = BuildInput(keys[keys.Count - 1 - index], keyUp: true);
            }

            _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        }

        return false;
    }

    private static Input BuildInput(ushort virtualKey, bool keyUp)
    {
        var flags = keyUp ? KeyEventKeyUp : 0;
        if (IsExtended(virtualKey))
        {
            flags |= KeyEventExtendedKey;
        }

        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = (ushort)MapVirtualKey(virtualKey, 0),
                    Flags = flags
                }
            }
        };
    }

    private static bool IsExtended(ushort virtualKey)
    {
        foreach (var key in NamedKeys.Values)
        {
            if (key.VirtualKey == virtualKey)
            {
                return key.Extended;
            }
        }

        return false;
    }

    private static bool TryResolve(string name, out Key key, out IReadOnlyList<ushort> modifiers)
    {
        modifiers = [];
        var trimmed = name.Trim();
        if (NamedKeys.TryGetValue(trimmed, out key))
        {
            return true;
        }

        if (trimmed.Length != 1)
        {
            return false;
        }

        var character = char.ToUpperInvariant(trimmed[0]);
        if (character is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
        {
            key = new Key(character);
            return true;
        }

        // Anything else by where it sits on the current keyboard layout.
        var scan = VkKeyScan(trimmed[0]);
        if (scan == -1)
        {
            return false;
        }

        // The high byte says which modifiers the layout needs for it: shift for "?" on a
        // US keyboard, ctrl+alt (AltGr) for many characters on others.
        var needed = new List<ushort>();
        if ((scan & 0x0200) != 0) needed.Add(0x11);
        if ((scan & 0x0400) != 0) needed.Add(0x12);
        if ((scan & 0x0100) != 0) needed.Add(0x10);
        modifiers = needed;
        key = new Key((ushort)(scan & 0xFF));
        return true;
    }

    private static Dictionary<string, Key> BuildNamedKeys()
    {
        var keys = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = new(0x11),
            ["control"] = new(0x11),
            ["shift"] = new(0x10),
            ["alt"] = new(0x12),
            ["win"] = new(0x5B, true),
            ["windows"] = new(0x5B, true),
            ["enter"] = new(0x0D),
            ["return"] = new(0x0D),
            ["esc"] = new(0x1B),
            ["escape"] = new(0x1B),
            ["tab"] = new(0x09),
            ["space"] = new(0x20),
            ["backspace"] = new(0x08),
            ["delete"] = new(0x2E, true),
            ["del"] = new(0x2E, true),
            ["insert"] = new(0x2D, true),
            ["ins"] = new(0x2D, true),
            ["home"] = new(0x24, true),
            ["end"] = new(0x23, true),
            ["pageup"] = new(0x21, true),
            ["pgup"] = new(0x21, true),
            ["pagedown"] = new(0x22, true),
            ["pgdn"] = new(0x22, true),
            ["up"] = new(0x26, true),
            ["down"] = new(0x28, true),
            ["left"] = new(0x25, true),
            ["right"] = new(0x27, true),
            ["printscreen"] = new(0x2C, true),
            ["prtsc"] = new(0x2C, true),
            ["pause"] = new(0x13),
            ["capslock"] = new(0x14),
            ["numlock"] = new(0x90, true),
            ["scrolllock"] = new(0x91),
            ["menu"] = new(0x5D, true),
            ["plus"] = new(0xBB),
            ["minus"] = new(0xBD),
            ["comma"] = new(0xBC),
            ["period"] = new(0xBE),
            ["play_pause"] = new(0xB3, true),
            ["next_track"] = new(0xB0, true),
            ["prev_track"] = new(0xB1, true),
            ["media_stop"] = new(0xB2, true),
            ["volume_up"] = new(0xAF, true),
            ["volume_down"] = new(0xAE, true),
            ["volume_mute"] = new(0xAD, true),
            ["browser_back"] = new(0xA6, true),
            ["browser_forward"] = new(0xA7, true),
            ["browser_refresh"] = new(0xA8, true),
            ["browser_home"] = new(0xAC, true)
        };

        for (var index = 1; index <= 24; index++)
        {
            keys[$"f{index}"] = new Key((ushort)(0x70 + index - 1));
        }

        for (var index = 0; index <= 9; index++)
        {
            keys[$"num{index}"] = new Key((ushort)(0x60 + index));
        }

        return keys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // The mouse member is the largest one; it gives the union its native size.
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", EntryPoint = "VkKeyScanW", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScan(char character);
}
