using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MxfPlayer
{
    public class HotKeySettingsForm : Form
    {
        private readonly Color _background = Color.FromArgb(31, 35, 38);
        private readonly Color _panelBackground = Color.FromArgb(27, 31, 34);
        private readonly Color _keyBackground = Color.FromArgb(35, 39, 42);
        private readonly Color _keyDisabled = Color.FromArgb(62, 68, 72);
        private readonly Color _assignedKey = Color.FromArgb(0, 225, 232);
        private readonly Color _border = Color.FromArgb(220, 230, 235);
        private readonly Color _selection = Color.FromArgb(77, 118, 136);
        private readonly Color _hoverSelection = Color.FromArgb(56, 84, 96);
        private readonly Color _listBackground = Color.FromArgb(31, 36, 39);
        private ListView _shortcutList = null!;
        private TextBox _shortcutEditor = null!;
        private ListViewItem? _editingShortcutItem;
        private ListViewItem? _hoveredShortcutItem;
        private Dictionary<HotKeyAction, Keys> _bindings;
        private readonly List<KeyboardKeyButton> _keyboardButtons = new();

        public IReadOnlyDictionary<HotKeyAction, Keys> Bindings => _bindings;

        public HotKeySettingsForm(IReadOnlyDictionary<HotKeyAction, Keys>? bindings = null)
        {
            _bindings = bindings == null
                ? HotKeySettings.CreateDefaultBindings()
                : HotKeySettings.CloneBindings(bindings);

            Text = "HotKey Setting";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimumSize = new Size(900, 640);
            Size = new Size(990, 730);
            BackColor = _background;
            ForeColor = Color.White;
            ShowInTaskbar = false;
            KeyPreview = true;

            BuildLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            _shortcutList.SelectedItems.Clear();
            _shortcutList.FocusedItem = null;
            _editingShortcutItem = null;
            _hoveredShortcutItem = null;
            _shortcutEditor.Visible = false;
            RefreshKeyboardPanel();
            BeginInvoke(new Action(ClearShortcutListSelection));
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(6),
                BackColor = _background
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 320));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            root.Controls.Add(BuildKeyboardPanel(), 0, 0);
            root.Controls.Add(BuildFunctionPanel(), 0, 1);
            root.Controls.Add(BuildButtonBar(), 0, 2);
        }

        private Control BuildKeyboardPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _panelBackground,
                BorderStyle = BorderStyle.FixedSingle
            };

            int x = 8;
            int y = 8;
            int key = 46;
            int gap = 4;

            AddKey(panel, x, y, key, 44, "Esc", disabled: true);
            x += key + gap + 2;

            string[] functionKeys = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" };
            foreach (string label in functionKeys)
            {
                AddKey(panel, x, y, key, 44, label);
                x += key + gap;
            }

            AddKey(panel, x, y, key, 44, "Insert");
            x += key + gap;
            AddKey(panel, x, y, key, 44, "Del", disabled: true);
            x += key + gap + 2;
            AddKey(panel, x, y, key, 44, "Home");
            x += key + gap;
            AddKey(panel, x, y, key, 44, "End");
            x += key + gap;
            AddKey(panel, x, y, key, 44, "PgU");
            x += key + gap;
            AddKey(panel, x, y, key, 44, "PgD");

            AddMainKeyboardRows(panel, 8, 76);
            AddNumberPad(panel, 760, 76);
            RefreshKeyboardPanel();

            return panel;
        }

        private void AddMainKeyboardRows(Control parent, int left, int top)
        {
            int key = 46;
            int gap = 4;
            int row = 48;

            AddKey(parent, left, top, key, 44, "`");
            AddRow(parent, left + key + gap, top, key, gap, "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=");
            AddKey(parent, left + (key + gap) * 13, top, 46, 44, "Back", disabled: true);

            top += row;
            AddKey(parent, left, top, 70, 44, "Tab");
            AddKey(parent, left + 74, top, key, 44, "Q");
            AddKey(parent, left + 124, top, key, 44, "W");
            AddKey(parent, left + 174, top, key, 44, "E");
            AddKey(parent, left + 224, top, key, 44, "R");
            AddKey(parent, left + 274, top, key, 44, "T");
            AddKey(parent, left + 324, top, key, 44, "Y");
            AddKey(parent, left + 374, top, key, 44, "U");
            AddKey(parent, left + 424, top, key, 44, "I");
            AddKey(parent, left + 474, top, key, 44, "O");
            AddKey(parent, left + 524, top, key, 44, "P");
            AddKey(parent, left + 574, top, key, 44, "[");
            AddKey(parent, left + 624, top, key, 44, "]");
            AddKey(parent, left + 674, top, key, 44, "\\");

            top += row;
            AddKey(parent, left, top, 70, 44, "CapsLock");
            AddKey(parent, left + 74, top, key, 44, "A");
            AddKey(parent, left + 124, top, key, 44, "S");
            AddKey(parent, left + 174, top, key, 44, "D");
            AddKey(parent, left + 224, top, key, 44, "F");
            AddKey(parent, left + 274, top, key, 44, "G");
            AddKey(parent, left + 324, top, key, 44, "H");
            AddKey(parent, left + 374, top, key, 44, "J");
            AddKey(parent, left + 424, top, key, 44, "K");
            AddKey(parent, left + 474, top, key, 44, "L");
            AddKey(parent, left + 524, top, key, 44, ";");
            AddKey(parent, left + 574, top, key, 44, "'");
            AddKey(parent, left + 624, top, 96, 44, "Enter", disabled: true);

            top += row;
            AddKey(parent, left, top, 96, 44, "Shift");
            AddRow(parent, left + 100, top, key, gap, "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/");
            AddKey(parent, left + 600, top, key, 44, "Up");
            AddKey(parent, left + 650, top, 46, 44, "Shift");

            top += row;
            AddKey(parent, left, top, 70, 44, "Ctrl");
            AddKey(parent, left + 74, top, 70, 44, "Alt");
            AddKey(parent, left + 150, top, 296, 44, "Space");
            AddKey(parent, left + 450, top, key, 44, "ALT");
            AddKey(parent, left + 500, top, key, 44, "Ctrl");
            AddKey(parent, left + 550, top, key, 44, "Left");
            AddKey(parent, left + 600, top, key, 44, "Down");
            AddKey(parent, left + 650, top, key, 44, "Right");
        }

        private void AddNumberPad(Control parent, int left, int top)
        {
            int key = 46;
            int gap = 4;
            int row = 48;

            AddKey(parent, left, top, key, 44, "NumL", keyCode: Keys.NumLock);
            AddKey(parent, left + (key + gap), top, key, 44, "/", keyCode: Keys.Divide);
            AddKey(parent, left + (key + gap) * 2, top, key, 44, "*", keyCode: Keys.Multiply);
            AddKey(parent, left + (key + gap) * 3, top, key, 44, "-", keyCode: Keys.Subtract);
            top += row;
            AddKey(parent, left, top, key, 44, "7", keyCode: Keys.NumPad7);
            AddKey(parent, left + (key + gap), top, key, 44, "8", keyCode: Keys.NumPad8);
            AddKey(parent, left + (key + gap) * 2, top, key, 44, "9", keyCode: Keys.NumPad9);
            AddKey(parent, left + 150, top, key, 92, "+", keyCode: Keys.Add);
            top += row;
            AddKey(parent, left, top, key, 44, "4", keyCode: Keys.NumPad4);
            AddKey(parent, left + (key + gap), top, key, 44, "5", keyCode: Keys.NumPad5);
            AddKey(parent, left + (key + gap) * 2, top, key, 44, "6", keyCode: Keys.NumPad6);
            top += row;
            AddKey(parent, left, top, key, 44, "1", keyCode: Keys.NumPad1);
            AddKey(parent, left + (key + gap), top, key, 44, "2", keyCode: Keys.NumPad2);
            AddKey(parent, left + (key + gap) * 2, top, key, 44, "3", keyCode: Keys.NumPad3);
            AddKey(parent, left + 150, top, key, 92, "Enter", disabled: true, keyCode: Keys.Return);
            top += row;
            AddKey(parent, left, top, 96, 44, "0", keyCode: Keys.NumPad0);
            AddKey(parent, left + 100, top, key, 44, ".", keyCode: Keys.Decimal);
        }

        private Control BuildFunctionPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(5),
                BackColor = _panelBackground,
                ForeColor = Color.White,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _panelBackground
            };

            var lblSections = new Label
            {
                Text = "Sections:",
                Left = 0,
                Top = 8,
                AutoSize = true,
                ForeColor = Color.White
            };

            var cboSections = new ComboBox
            {
                Left = 58,
                Top = 5,
                Width = 130,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(44, 57, 66),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            cboSections.Items.Add("All");
            cboSections.SelectedIndex = 0;

            var hint = new Label
            {
                Text = "You can clear the shortcut using Delete or Backspace.",
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                ForeColor = Color.White
            };
            hint.Left = 660;
            hint.Top = 8;

            header.Controls.Add(lblSections);
            header.Controls.Add(cboSections);
            header.Controls.Add(hint);
            header.Resize += (_, _) => hint.Left = Math.Max(210, header.ClientSize.Width - hint.Width - 4);

            panel.Controls.Add(header, 0, 0);
            panel.Controls.Add(BuildShortcutList(), 0, 1);

            return panel;
        }

        private Control BuildShortcutList()
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                GridLines = false,
                MultiSelect = false,
                OwnerDraw = true,
                BorderStyle = BorderStyle.None,
                BackColor = _listBackground,
                ForeColor = Color.White,
                HeaderStyle = ColumnHeaderStyle.None
            };
            _shortcutList = list;

            list.Columns.Add("Function", 410);
            list.Columns.Add("Shortcut", 510);
            list.MouseDown += OnShortcutListMouseDown;
            list.MouseMove += OnShortcutListMouseMove;
            list.MouseLeave += (_, _) => SetHoveredShortcutItem(null);
            list.Resize += (_, _) => RepositionShortcutEditor();
            list.ColumnWidthChanged += (_, _) => RepositionShortcutEditor();
            list.MouseWheel += (_, _) => HideShortcutEditor();
            list.SelectedIndexChanged += (_, _) =>
            {
                _shortcutList.Invalidate();
                RepositionShortcutEditor();
            };
            list.DrawColumnHeader += (_, e) => e.DrawBackground();
            list.DrawSubItem += OnShortcutListDrawSubItem;

            foreach (HotKeyDefinition definition in HotKeySettings.Definitions)
            {
                var item = new ListViewItem(definition.DisplayName)
                {
                    Tag = definition.Action
                };
                item.SubItems.Add(GetShortcutText(definition.Action));
                list.Items.Add(item);
            }

            ClearShortcutListSelection();

            _shortcutEditor = CreateShortcutEditor();
            list.Controls.Add(_shortcutEditor);

            return list;
        }

        private TextBox CreateShortcutEditor()
        {
            var editor = new TextBox
            {
                Visible = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = _selection,
                ForeColor = Color.White,
                Font = new Font("Microsoft JhengHei", 9f, FontStyle.Regular)
            };

            editor.KeyDown += OnShortcutEditorKeyDown;
            editor.TextChanged += (_, _) =>
            {
                if (_editingShortcutItem != null)
                {
                    _editingShortcutItem.SubItems[1].Text = editor.Text;
                    SyncBindingFromShortcutItem(_editingShortcutItem);
                    RefreshKeyboardPanel();
                }
            };
            editor.LostFocus += (_, _) => HideShortcutEditor();

            return editor;
        }

        private void OnShortcutListMouseDown(object? sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _shortcutList.HitTest(e.Location);
            if (hit.Item == null)
            {
                HideShortcutEditor();
                _shortcutList.SelectedItems.Clear();
                return;
            }

            BeginShortcutEdit(hit.Item, e.Location);
        }

        private void OnShortcutListMouseMove(object? sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _shortcutList.HitTest(e.Location);
            SetHoveredShortcutItem(hit.Item);
        }

        private void SetHoveredShortcutItem(ListViewItem? item)
        {
            if (_hoveredShortcutItem == item)
                return;

            ListViewItem? previous = _hoveredShortcutItem;
            _hoveredShortcutItem = item;
            InvalidateShortcutItem(previous);
            InvalidateShortcutItem(_hoveredShortcutItem);
        }

        private void InvalidateShortcutItem(ListViewItem? item)
        {
            if (item == null || item.ListView == null)
                return;

            Rectangle bounds = item.Bounds;
            if (bounds.Height <= 0)
                return;

            bounds.X = 0;
            bounds.Width = _shortcutList.ClientSize.Width;
            _shortcutList.Invalidate(bounds);
        }

        private void ClearShortcutListSelection()
        {
            if (_shortcutList == null)
                return;

            foreach (ListViewItem item in _shortcutList.Items)
                item.Selected = false;

            _shortcutList.FocusedItem = null;
            _shortcutList.Invalidate();
        }

        private void BeginShortcutEdit(ListViewItem item, Point clickLocation)
        {
            _editingShortcutItem = item;
            _shortcutEditor.Text = item.SubItems[1].Text;
            _shortcutEditor.Visible = true;
            RepositionShortcutEditor();
            _shortcutEditor.BringToFront();

            int caretPointX = Math.Max(0, clickLocation.X - _shortcutEditor.Left);
            BeginInvoke(new Action(() =>
            {
                if (!_shortcutEditor.Visible)
                    return;

                _shortcutEditor.Focus();
                int caretIndex = _shortcutEditor.GetCharIndexFromPosition(new Point(caretPointX, _shortcutEditor.Height / 2));
                if (clickLocation.X >= _shortcutEditor.Right - 4)
                    caretIndex = _shortcutEditor.TextLength;
                _shortcutEditor.SelectionStart = Math.Clamp(caretIndex, 0, _shortcutEditor.TextLength);
                _shortcutEditor.SelectionLength = 0;
            }));
        }

        private void RepositionShortcutEditor()
        {
            if (_shortcutEditor == null || !_shortcutEditor.Visible || _editingShortcutItem == null)
                return;

            Rectangle itemBounds = _editingShortcutItem.Bounds;
            if (itemBounds.Height <= 0)
            {
                HideShortcutEditor();
                return;
            }

            int left = _shortcutList.Columns[0].Width;
            int width = Math.Max(80, _shortcutList.ClientSize.Width - left - 4);
            _shortcutEditor.Bounds = new Rectangle(left, itemBounds.Top, width, itemBounds.Height);
        }

        private void HideShortcutEditor()
        {
            if (_shortcutEditor == null)
                return;

            _shortcutEditor.Visible = false;
            _editingShortcutItem = null;
            _shortcutList.Invalidate();
        }

        private void OnShortcutEditorKeyDown(object? sender, KeyEventArgs e)
        {
            Keys keyCode = e.KeyData & Keys.KeyCode;
            if (keyCode == Keys.None)
                return;

            if (_editingShortcutItem?.Tag is not HotKeyAction action)
                return;

            if (keyCode == Keys.Delete || keyCode == Keys.Back)
            {
                _bindings.Remove(action);
                _shortcutEditor.Text = string.Empty;
                RefreshKeyboardPanel();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (IsModifierOnlyKey(keyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            Keys shortcut = HotKeySettings.Normalize(e.KeyData);
            ClearDuplicateShortcut(action, shortcut);
            _bindings[action] = shortcut;
            _shortcutEditor.Text = HotKeySettings.FormatShortcut(shortcut);
            RefreshKeyboardPanel();
            _shortcutEditor.SelectionStart = _shortcutEditor.TextLength;
            _shortcutEditor.SelectionLength = 0;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private string GetShortcutText(HotKeyAction action)
        {
            return _bindings.TryGetValue(action, out Keys shortcut)
                ? HotKeySettings.FormatShortcut(shortcut)
                : string.Empty;
        }

        private void SyncBindingFromShortcutItem(ListViewItem item)
        {
            if (item.Tag is not HotKeyAction action)
                return;

            string shortcutText = item.SubItems.Count > 1 ? item.SubItems[1].Text : string.Empty;
            if (string.IsNullOrWhiteSpace(shortcutText))
            {
                _bindings.Remove(action);
                return;
            }

            if (HotKeySettings.TryParseShortcut(shortcutText, out Keys shortcut) && shortcut != Keys.None)
                _bindings[action] = shortcut;
        }

        private void ClearDuplicateShortcut(HotKeyAction currentAction, Keys shortcut)
        {
            foreach (ListViewItem item in _shortcutList.Items)
            {
                if (item.Tag is not HotKeyAction action || action == currentAction)
                    continue;

                if (_bindings.TryGetValue(action, out Keys existing) && existing == shortcut)
                {
                    _bindings.Remove(action);
                    item.SubItems[1].Text = string.Empty;
                }
            }
        }

        private void ResetShortcutRows()
        {
            _bindings = HotKeySettings.CreateDefaultBindings();
            foreach (ListViewItem item in _shortcutList.Items)
            {
                if (item.Tag is HotKeyAction action)
                    item.SubItems[1].Text = GetShortcutText(action);
            }

            HideShortcutEditor();
            ClearShortcutListSelection();
            RefreshKeyboardPanel();
        }

        private void CommitShortcutRows()
        {
            foreach (ListViewItem item in _shortcutList.Items)
                SyncBindingFromShortcutItem(item);
        }

        private void OnShortcutListDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            ListViewItem? item = e.Item;
            if (item == null)
                return;

            bool selected = item.Selected || item == _editingShortcutItem;
            bool hovered = item == _hoveredShortcutItem;
            Color backColor = selected ? _selection : hovered ? _hoverSelection : _listBackground;

            using (var backBrush = new SolidBrush(backColor))
                e.Graphics.FillRectangle(backBrush, e.Bounds);

            string text = e.SubItem?.Text ?? string.Empty;
            var textBounds = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                text,
                _shortcutList.Font,
                textBounds,
                Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (selected && e.ColumnIndex == _shortcutList.Columns.Count - 1)
            {
                using var pen = new Pen(Color.FromArgb(140, 210, 230));
                var focusRect = new Rectangle(0, e.Bounds.Top, _shortcutList.ClientSize.Width - 1, e.Bounds.Height - 1);
                e.Graphics.DrawRectangle(pen, focusRect);
            }
        }

        private static bool IsModifierOnlyKey(Keys keyCode)
        {
            return keyCode == Keys.ControlKey
                || keyCode == Keys.ShiftKey
                || keyCode == Keys.Menu
                || keyCode == Keys.LControlKey
                || keyCode == Keys.RControlKey
                || keyCode == Keys.LShiftKey
                || keyCode == Keys.RShiftKey
                || keyCode == Keys.LMenu
                || keyCode == Keys.RMenu;
        }

        private Control BuildButtonBar()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _background
            };

            var cancel = CreateActionButton("CANCEL");
            cancel.DialogResult = DialogResult.Cancel;
            var save = CreateActionButton("Save");
            save.Click += (_, _) =>
            {
                CommitShortcutRows();
                DialogResult = DialogResult.OK;
                Close();
            };
            var reset = CreateActionButton("Reset");
            reset.Click += (_, _) => ResetShortcutRows();

            cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            save.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            reset.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

            cancel.Location = new Point(panel.Width - 58, 7);
            save.Location = new Point(panel.Width - 118, 7);
            reset.Location = new Point(panel.Width - 178, 7);

            panel.Controls.Add(reset);
            panel.Controls.Add(save);
            panel.Controls.Add(cancel);

            panel.Resize += (_, _) =>
            {
                cancel.Location = new Point(panel.ClientSize.Width - cancel.Width - 4, 7);
                save.Location = new Point(cancel.Left - save.Width - 8, 7);
                reset.Location = new Point(save.Left - reset.Width - 8, 7);
            };

            AcceptButton = save;
            CancelButton = cancel;

            return panel;
        }

        private Button CreateActionButton(string text)
        {
            var button = new Button
            {
                Text = text,
                Size = new Size(54, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(74, 84, 90),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(98, 110, 118);
            return button;
        }

        private void AddRow(Control parent, int left, int top, int keyWidth, int gap, params string[] labels)
        {
            for (int i = 0; i < labels.Length; i++)
                AddKey(parent, left + (keyWidth + gap) * i, top, keyWidth, 44, labels[i]);
        }

        private void AddKey(Control parent, int left, int top, int width, int height, string label, string? action = null, bool assigned = false, bool disabled = false, Keys keyCode = Keys.None)
        {
            Keys actualKeyCode = keyCode == Keys.None ? GetKeyboardKeyCode(label) : keyCode;
            if (!disabled)
            {
                assigned = IsKeyboardKeyAssigned(actualKeyCode);
                string dynamicAction = GetKeyboardActionText(actualKeyCode);
                action = string.IsNullOrEmpty(dynamicAction) ? null : dynamicAction;
            }

            var button = new Button
            {
                Text = action == null ? label : $"{label}\n{action}",
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = disabled ? _keyDisabled : assigned ? _assignedKey : _keyBackground,
                ForeColor = assigned ? Color.Black : disabled ? Color.FromArgb(38, 42, 45) : Color.White,
                Font = new Font("Microsoft JhengHei", action == null ? 8.5f : 7f, action == null ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };
            button.FlatAppearance.BorderColor = disabled ? _keyDisabled : _border;
            button.FlatAppearance.MouseDownBackColor = assigned ? Color.FromArgb(0, 200, 210) : Color.FromArgb(72, 76, 82);
            button.FlatAppearance.MouseOverBackColor = assigned ? Color.FromArgb(0, 235, 240) : Color.FromArgb(55, 60, 64);

            parent.Controls.Add(button);
            _keyboardButtons.Add(new KeyboardKeyButton(button, label, actualKeyCode, disabled));
        }

        private void RefreshKeyboardPanel()
        {
            foreach (KeyboardKeyButton keyButton in _keyboardButtons)
            {
                Button button = keyButton.Button;
                if (keyButton.Disabled)
                {
                    button.Text = keyButton.Label;
                    button.BackColor = _keyDisabled;
                    button.ForeColor = Color.FromArgb(38, 42, 45);
                    button.Font = new Font("Microsoft JhengHei", 8.5f, FontStyle.Bold);
                    button.FlatAppearance.BorderColor = _keyDisabled;
                    button.FlatAppearance.MouseDownBackColor = _keyDisabled;
                    button.FlatAppearance.MouseOverBackColor = _keyDisabled;
                    continue;
                }

                bool assigned = IsKeyboardKeyAssigned(keyButton.KeyCode);
                string actionText = GetKeyboardActionText(keyButton.KeyCode);
                button.Text = string.IsNullOrEmpty(actionText)
                    ? keyButton.Label
                    : $"{keyButton.Label}\n{actionText}";
                button.BackColor = assigned ? _assignedKey : _keyBackground;
                button.ForeColor = assigned ? Color.Black : Color.White;
                button.Font = new Font(
                    "Microsoft JhengHei",
                    string.IsNullOrEmpty(actionText) ? 8.5f : 7f,
                    string.IsNullOrEmpty(actionText) ? FontStyle.Bold : FontStyle.Regular);
                button.FlatAppearance.BorderColor = _border;
                button.FlatAppearance.MouseDownBackColor = assigned ? Color.FromArgb(0, 200, 210) : Color.FromArgb(72, 76, 82);
                button.FlatAppearance.MouseOverBackColor = assigned ? Color.FromArgb(0, 235, 240) : Color.FromArgb(55, 60, 64);
            }
        }

        private bool IsKeyboardKeyAssigned(Keys keyCode)
        {
            if (keyCode == Keys.None)
                return false;

            if (keyCode == Keys.ControlKey)
                return _bindings.Values.Any(shortcut => (shortcut & Keys.Control) == Keys.Control);
            if (keyCode == Keys.ShiftKey)
                return _bindings.Values.Any(shortcut => (shortcut & Keys.Shift) == Keys.Shift);
            if (keyCode == Keys.Menu)
                return _bindings.Values.Any(shortcut => (shortcut & Keys.Alt) == Keys.Alt);

            return _bindings.Values.Any(shortcut => (shortcut & Keys.KeyCode) == keyCode);
        }

        private string GetKeyboardActionText(Keys keyCode)
        {
            if (keyCode == Keys.None || keyCode == Keys.ControlKey || keyCode == Keys.ShiftKey || keyCode == Keys.Menu)
                return string.Empty;

            var actions = HotKeySettings.Definitions
                .Where(definition =>
                    _bindings.TryGetValue(definition.Action, out Keys shortcut) &&
                    (shortcut & Keys.KeyCode) == keyCode)
                .Select(definition => GetKeyboardActionName(definition.Action))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct()
                .Take(2)
                .ToArray();

            return string.Join("\n", actions);
        }

        private static string GetKeyboardActionName(HotKeyAction action)
        {
            return action switch
            {
                HotKeyAction.OpenFiles => "Open",
                HotKeyAction.OpenFolder => "Folder",
                HotKeyAction.Quit => "Quit",
                HotKeyAction.Play => "Play",
                HotKeyAction.Pause => "Pause",
                HotKeyAction.PlayPause => "Play/Pause",
                HotKeyAction.StepBackward => "Step -",
                HotKeyAction.StepForward => "Step +",
                HotKeyAction.MoveFirst => "First",
                HotKeyAction.MoveLast => "Last",
                HotKeyAction.Rewind => "Rewind",
                HotKeyAction.FastForward => "Fast Fwd",
                HotKeyAction.JumpBackward10 => "-10s",
                HotKeyAction.JumpForward10 => "+10s",
                HotKeyAction.JumpBackward5 => "-5s",
                HotKeyAction.JumpForward5 => "+5s",
                HotKeyAction.ToggleMetersWindow => "Meters",
                HotKeyAction.ToggleInlineMeters => "Inline",
                HotKeyAction.ToggleChannel1 => "CH1",
                HotKeyAction.ToggleChannel2 => "CH2",
                HotKeyAction.ToggleChannel3 => "CH3",
                HotKeyAction.ToggleChannel4 => "CH4",
                HotKeyAction.ToggleChannel5 => "CH5",
                HotKeyAction.ToggleChannel6 => "CH6",
                HotKeyAction.ToggleChannel7 => "CH7",
                HotKeyAction.ToggleChannel8 => "CH8",
                _ => string.Empty
            };
        }

        private static Keys GetKeyboardKeyCode(string label)
        {
            return label.ToUpperInvariant() switch
            {
                "ESC" => Keys.Escape,
                "INSERT" => Keys.Insert,
                "DEL" => Keys.Delete,
                "HOME" => Keys.Home,
                "END" => Keys.End,
                "PGU" => Keys.Prior,
                "PGD" => Keys.Next,
                "BACK" => Keys.Back,
                "TAB" => Keys.Tab,
                "CAPSLOCK" => Keys.CapsLock,
                "ENTER" => Keys.Return,
                "SHIFT" => Keys.ShiftKey,
                "CTRL" => Keys.ControlKey,
                "ALT" => Keys.Menu,
                "SPACE" => Keys.Space,
                "LEFT" => Keys.Left,
                "RIGHT" => Keys.Right,
                "UP" => Keys.Up,
                "DOWN" => Keys.Down,
                "`" => Keys.Oemtilde,
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
                _ when label.Length == 1 && label[0] is >= 'A' and <= 'Z' => Enum.Parse<Keys>(label),
                _ when label.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                       int.TryParse(label[1..], out int functionKey) &&
                       functionKey is >= 1 and <= 12 => (Keys)((int)Keys.F1 + functionKey - 1),
                _ => Keys.None
            };
        }

        private sealed record KeyboardKeyButton(Button Button, string Label, Keys KeyCode, bool Disabled);

        private static IEnumerable<(string Name, string Shortcut)> GetShortcutRows()
        {
            yield return ("開啟檔案", "<Ctrl+I>");
            yield return ("開啟檔案夾", "<Ctrl+F>");
            yield return ("退出程式", "<Ctrl+Q>");
            yield return ("播放/暫停", "<Space>");
            yield return ("快速回播", "<Left>");
            yield return ("快速播放", "<Right><Shift+S>");
            yield return ("回播", "<R>");
            yield return ("快播", "<F>");
            yield return ("移至最前", "<Shift+I>");
            yield return ("移至最後", "<Shift+O>");
            yield return ("倒退10秒", "<Ctrl+Left>");
            yield return ("前進10秒", "<Ctrl+Right>");
            yield return ("倒退5格", "<Down>");
            yield return ("前進5格", "<Up>");
            yield return ("上一格", "<Left>");
            yield return ("下一格", "<Right>");
            yield return ("顯示/隱藏音量表", "<M>");
            yield return ("選擇 CH1", "<Alt+1>");
            yield return ("選擇 CH2", "<Alt+2>");
            yield return ("選擇 CH3", "<Alt+3>");
            yield return ("選擇 CH4", "<Alt+4>");
            yield return ("選擇 CH5", "<Alt+5>");
            yield return ("選擇 CH6", "<Alt+6>");
            yield return ("選擇 CH7", "<Alt+7>");
            yield return ("選擇 CH8", "<Alt+8>");
        }
    }
}
