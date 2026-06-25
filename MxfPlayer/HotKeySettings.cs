using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace MxfPlayer
{
    public enum HotKeyAction
    {
        OpenFiles,
        OpenFolder,
        Quit,
        Play,
        Pause,
        PlayPause,
        StepBackward,
        StepForward,
        MoveFirst,
        MoveLast,
        Rewind,
        FastForward,
        JumpBackward10,
        JumpForward10,
        JumpBackward5,
        JumpForward5,
        ToggleMetersWindow,
        ToggleInlineMeters,
        ToggleChannel1,
        ToggleChannel2,
        ToggleChannel3,
        ToggleChannel4,
        ToggleChannel5,
        ToggleChannel6,
        ToggleChannel7,
        ToggleChannel8
    }

    public sealed record HotKeyDefinition(HotKeyAction Action, string DisplayName);

    public static class HotKeySettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MxfPlayer",
            "hotkeys.json");

        public static readonly HotKeyDefinition[] Definitions =
        {
            new(HotKeyAction.OpenFiles, "Open files"),
            new(HotKeyAction.OpenFolder, "Open folder"),
            new(HotKeyAction.Quit, "Quit"),
            new(HotKeyAction.Play, "Play"),
            new(HotKeyAction.Pause, "Pause"),
            new(HotKeyAction.PlayPause, "Play/Pause"),
            new(HotKeyAction.StepBackward, "Step backward"),
            new(HotKeyAction.StepForward, "Step forward"),
            new(HotKeyAction.MoveFirst, "Move first"),
            new(HotKeyAction.MoveLast, "Move last"),
            new(HotKeyAction.Rewind, "Rewind"),
            new(HotKeyAction.FastForward, "Fast forward"),
            new(HotKeyAction.JumpBackward10, "Jump backward 10 seconds"),
            new(HotKeyAction.JumpForward10, "Jump forward 10 seconds"),
            new(HotKeyAction.JumpBackward5, "Jump backward 5 seconds"),
            new(HotKeyAction.JumpForward5, "Jump forward 5 seconds"),
            new(HotKeyAction.ToggleMetersWindow, "Show/hide meter window"),
            new(HotKeyAction.ToggleInlineMeters, "Show/hide inline meters"),
            new(HotKeyAction.ToggleChannel1, "Toggle CH1"),
            new(HotKeyAction.ToggleChannel2, "Toggle CH2"),
            new(HotKeyAction.ToggleChannel3, "Toggle CH3"),
            new(HotKeyAction.ToggleChannel4, "Toggle CH4"),
            new(HotKeyAction.ToggleChannel5, "Toggle CH5"),
            new(HotKeyAction.ToggleChannel6, "Toggle CH6"),
            new(HotKeyAction.ToggleChannel7, "Toggle CH7"),
            new(HotKeyAction.ToggleChannel8, "Toggle CH8")
        };

        public static Dictionary<HotKeyAction, Keys> CreateDefaultBindings()
        {
            return new Dictionary<HotKeyAction, Keys>
            {
                [HotKeyAction.OpenFiles] = Keys.Control | Keys.I,
                [HotKeyAction.OpenFolder] = Keys.Control | Keys.F,
                [HotKeyAction.Quit] = Keys.Control | Keys.Q,
                [HotKeyAction.PlayPause] = Keys.Space,
                [HotKeyAction.StepBackward] = Keys.Left,
                [HotKeyAction.StepForward] = Keys.Right,
                [HotKeyAction.MoveFirst] = Keys.Home,
                [HotKeyAction.MoveLast] = Keys.End,
                [HotKeyAction.Rewind] = Keys.Shift | Keys.Left,
                [HotKeyAction.FastForward] = Keys.Shift | Keys.Right,
                [HotKeyAction.JumpBackward10] = Keys.Control | Keys.Left,
                [HotKeyAction.JumpForward10] = Keys.Control | Keys.Right,
                [HotKeyAction.JumpBackward5] = Keys.Down,
                [HotKeyAction.JumpForward5] = Keys.Up,
                [HotKeyAction.ToggleMetersWindow] = Keys.M,
                [HotKeyAction.ToggleChannel1] = Keys.Alt | Keys.D1,
                [HotKeyAction.ToggleChannel2] = Keys.Alt | Keys.D2,
                [HotKeyAction.ToggleChannel3] = Keys.Alt | Keys.D3,
                [HotKeyAction.ToggleChannel4] = Keys.Alt | Keys.D4,
                [HotKeyAction.ToggleChannel5] = Keys.Alt | Keys.D5,
                [HotKeyAction.ToggleChannel6] = Keys.Alt | Keys.D6,
                [HotKeyAction.ToggleChannel7] = Keys.Alt | Keys.D7,
                [HotKeyAction.ToggleChannel8] = Keys.Alt | Keys.D8
            };
        }

        public static Dictionary<HotKeyAction, Keys> CloneBindings(IReadOnlyDictionary<HotKeyAction, Keys> source)
        {
            return source.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        public static Dictionary<HotKeyAction, Keys> Load()
        {
            Dictionary<HotKeyAction, Keys> bindings = CreateDefaultBindings();

            try
            {
                if (!File.Exists(SettingsPath))
                    return bindings;

                string json = File.ReadAllText(SettingsPath);
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                foreach ((string actionName, string shortcutText) in saved)
                {
                    if (!Enum.TryParse(actionName, out HotKeyAction action))
                        continue;

                    if (string.IsNullOrWhiteSpace(shortcutText))
                        bindings.Remove(action);
                    else if (TryParseShortcut(shortcutText, out Keys keys) && keys != Keys.None)
                        bindings[action] = keys;
                }
            }
            catch
            {
            }

            return bindings;
        }

        public static void Save(IReadOnlyDictionary<HotKeyAction, Keys> bindings)
        {
            try
            {
                string? directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var serializable = Definitions.ToDictionary(
                    definition => definition.Action.ToString(),
                    definition => bindings.TryGetValue(definition.Action, out Keys keys)
                        ? FormatShortcut(keys)
                        : string.Empty);

                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(serializable, JsonOptions));
            }
            catch
            {
            }
        }

        public static string FormatShortcut(Keys keyData)
        {
            if (keyData == Keys.None)
                return string.Empty;

            var parts = new List<string>();
            if ((keyData & Keys.Control) == Keys.Control)
                parts.Add("Ctrl");
            if ((keyData & Keys.Shift) == Keys.Shift)
                parts.Add("Shift");
            if ((keyData & Keys.Alt) == Keys.Alt)
                parts.Add("Alt");

            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.None)
                return string.Empty;

            parts.Add(GetShortcutKeyName(keyCode));
            return $"<{string.Join("+", parts)}>";
        }

        public static bool TryParseShortcut(string text, out Keys keyData)
        {
            keyData = Keys.None;
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (text.StartsWith("<", StringComparison.Ordinal) && text.EndsWith(">", StringComparison.Ordinal))
                text = text[1..^1];

            Keys modifiers = Keys.None;
            Keys keyCode = Keys.None;
            foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (rawPart.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    rawPart.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= Keys.Control;
                    continue;
                }

                if (rawPart.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= Keys.Shift;
                    continue;
                }

                if (rawPart.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= Keys.Alt;
                    continue;
                }

                keyCode = ParseKeyName(rawPart);
                if (keyCode == Keys.None)
                    return false;
            }

            if (keyCode == Keys.None)
                return false;

            keyData = modifiers | keyCode;
            return true;
        }

        public static Keys Normalize(Keys keyData)
        {
            return (keyData & (Keys.Control | Keys.Shift | Keys.Alt)) | (keyData & Keys.KeyCode);
        }

        private static string GetShortcutKeyName(Keys keyCode)
        {
            return keyCode switch
            {
                Keys.Space => "Space",
                Keys.Return => "Enter",
                Keys.Escape => "Esc",
                Keys.Left => "Left",
                Keys.Right => "Right",
                Keys.Up => "Up",
                Keys.Down => "Down",
                Keys.Prior => "PgUp",
                Keys.Next => "PgDn",
                Keys.Insert => "Insert",
                Keys.Home => "Home",
                Keys.End => "End",
                Keys.Tab => "Tab",
                Keys.D0 => "0",
                Keys.D1 => "1",
                Keys.D2 => "2",
                Keys.D3 => "3",
                Keys.D4 => "4",
                Keys.D5 => "5",
                Keys.D6 => "6",
                Keys.D7 => "7",
                Keys.D8 => "8",
                Keys.D9 => "9",
                Keys.OemMinus => "-",
                Keys.Oemplus => "=",
                Keys.Oemcomma => ",",
                Keys.OemPeriod => ".",
                Keys.OemQuestion => "/",
                Keys.OemSemicolon => ";",
                Keys.OemQuotes => "'",
                Keys.OemOpenBrackets => "[",
                Keys.OemCloseBrackets => "]",
                Keys.OemPipe => "\\",
                Keys.Oemtilde => "`",
                _ => keyCode.ToString()
            };
        }

        private static Keys ParseKeyName(string keyName)
        {
            return keyName.ToUpperInvariant() switch
            {
                "SPACE" => Keys.Space,
                "ENTER" => Keys.Return,
                "ESC" or "ESCAPE" => Keys.Escape,
                "LEFT" => Keys.Left,
                "RIGHT" => Keys.Right,
                "UP" => Keys.Up,
                "DOWN" => Keys.Down,
                "PGUP" or "PAGEUP" => Keys.Prior,
                "PGDN" or "PAGEDOWN" => Keys.Next,
                "INSERT" or "INS" => Keys.Insert,
                "HOME" => Keys.Home,
                "END" => Keys.End,
                "TAB" => Keys.Tab,
                "0" => Keys.D0,
                "1" => Keys.D1,
                "2" => Keys.D2,
                "3" => Keys.D3,
                "4" => Keys.D4,
                "5" => Keys.D5,
                "6" => Keys.D6,
                "7" => Keys.D7,
                "8" => Keys.D8,
                "9" => Keys.D9,
                "-" => Keys.OemMinus,
                "=" => Keys.Oemplus,
                "," => Keys.Oemcomma,
                "." => Keys.OemPeriod,
                "/" => Keys.OemQuestion,
                ";" => Keys.OemSemicolon,
                "'" => Keys.OemQuotes,
                "[" => Keys.OemOpenBrackets,
                "]" => Keys.OemCloseBrackets,
                "\\" => Keys.OemPipe,
                "`" => Keys.Oemtilde,
                _ => Enum.TryParse(keyName, ignoreCase: true, out Keys parsed) ? parsed : Keys.None
            };
        }
    }
}
