using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Bemo.Win32;

namespace Bemo.Win32
{
    [System.ComponentModel.DesignerCategory("CODE")]
    public class HotKeyControl : TextBox
    {
        // When UseManaged is true the control is created as a plain Edit window
        // (TextBox base) instead of the comctl32 "msctls_hotkey32" common
        // control. The managed code path captures key presses itself and
        // stores the same packed-int format (low byte = vk, high byte =
        // modifier flags) that HKM_GETHOTKEY would have returned, so callers
        // see the same HotKey int regardless of which code path is active.
        // The managed path lets the control respect WinForms BackColor /
        // ForeColor and SetWindowTheme(DarkMode_*) — the comctl32 hot key
        // control ignores both. Set this BEFORE the form that hosts the
        // control is constructed.
        public static bool UseManaged = false;

        // Localized "no hotkey set" label. Defaults to "None" but the F# side
        // overwrites this with the string for the current language before the
        // dialog is constructed, so newly-created HotKeyControls pick up the
        // user's chosen language.
        public static string NoneLabel = "None";

        // Storage for the managed code path (the comctl32 path keeps the value
        // inside the native control and accesses it via HKM_GETHOTKEY).
        private int _managedHotKey;

        public HotKeyControl()
        {
            if (UseManaged)
            {
                // ReadOnly so the user can't type literal text but can still
                // give focus and receive key events. Display the localized
                // "no hotkey" label until a hotkey is set.
                this.ReadOnly = true;
                this.Text = NoneLabel;
            }
        }

        public int HotKey
        {
            set
            {
                if (UseManaged)
                {
                    _managedHotKey = value;
                    this.Text = FormatHotKey(value);
                }
                else
                {
                    WinUserApi.SendMessage(Handle, HotKeyConstants.HKM_SETHOTKEY, new IntPtr(value), IntPtr.Zero);
                }
            }
            get
            {
                if (UseManaged)
                    return _managedHotKey;
                return (int)WinUserApi.SendMessage(Handle, HotKeyConstants.HKM_GETHOTKEY, IntPtr.Zero, IntPtr.Zero);
            }
        }

        // SettingsField supplies the same font-height rectangle used by EDIT.
        // The native hotkey class does not implement EM_SETRECT.
        private Rectangle _centeredTextBounds;
        public Rectangle CenteredTextBounds
        {
            get { return _centeredTextBounds; }
            set { if (_centeredTextBounds != value) { _centeredTextBounds = value; Invalidate(); } }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCaretPos(out Point point);
        [DllImport("user32.dll")]
        private static extern bool SetCaretPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern bool HideCaret(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowCaret(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int keyData, System.Text.StringBuilder text, int size);
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int key);

        private const System.Windows.Forms.TextFormatFlags NativeTextFlags =
            System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.Top |
            System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.NoPrefix |
            System.Windows.Forms.TextFormatFlags.SingleLine;

        private static string NativeKeyName(int vk, bool extended)
        {
            var text = new System.Text.StringBuilder(256);
            int keyData = (int)(MapVirtualKey((uint)vk, 0) << 16);
            if (extended) keyData |= 1 << 24;
            GetKeyNameText(keyData, text, text.Capacity);
            return text.ToString();
        }

        // The native hotkey class does not expose its display text via WM_GETTEXT.
        private static string FormatNativeHotKey(int packed)
        {
            int vk = packed & 0xFF;
            int mods = (packed >> 8) & 0xFF;
            var text = new System.Text.StringBuilder();
            if ((mods & 2) != 0) text.Append(NativeKeyName((int)Keys.ControlKey, false)).Append(" + ");
            if ((mods & 1) != 0) text.Append(NativeKeyName((int)Keys.ShiftKey, false)).Append(" + ");
            if ((mods & 4) != 0) text.Append(NativeKeyName((int)Keys.Menu, false)).Append(" + ");
            if (vk != 0) text.Append(NativeKeyName(vk, (mods & 8) != 0));
            return text.Length == 0 ? NoneLabel : text.ToString();
        }

        private string NativeDisplayText()
        {
            int packed = HotKey;
            if ((packed & 0xFF) == 0)
            {
                int mods = 0;
                if (Focused)
                {
                    if (GetKeyState((int)Keys.ControlKey) < 0) mods |= 2;
                    if (GetKeyState((int)Keys.ShiftKey) < 0) mods |= 1;
                    if (GetKeyState((int)Keys.Menu) < 0) mods |= 4;
                }
                packed = mods << 8;
            }
            return FormatNativeHotKey(packed);
        }

        private void PaintCenteredNativeText(Graphics graphics)
        {
            Color background = !Enabled && BackColor == SystemColors.Window ? SystemColors.Control : BackColor;
            using (Brush brush = new SolidBrush(background))
                graphics.FillRectangle(brush, ClientRectangle);
            TextRenderer.DrawText(graphics, NativeDisplayText(), Font, _centeredTextBounds,
                Enabled ? ForeColor : SystemColors.GrayText, NativeTextFlags);
        }

        public event EventHandler HotKeyChanged;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (!UseManaged)
                {
                    cp.ClassName = CommonControlClassNames.HOTKEY_CLASS;
                    cp.ExStyle = 0;
                    cp.Style = WindowsStyles.WS_CHILD | WindowsStyles.WS_VISIBLE;
                }
                return cp;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (UseManaged)
            {
                int vk = e.KeyValue;
                // Esc / Backspace / Delete clear the hotkey, matching the
                // behavior of the comctl32 hot key control (which clears on
                // Esc + similar keys).
                bool isClearKey =
                    vk == (int)Keys.Escape ||
                    vk == (int)Keys.Back ||
                    vk == (int)Keys.Delete;
                if (isClearKey && !e.Shift && !e.Control && !e.Alt)
                {
                    if (_managedHotKey != 0)
                    {
                        _managedHotKey = 0;
                        this.Text = FormatHotKey(0);
                        if (HotKeyChanged != null)
                            HotKeyChanged(this, EventArgs.Empty);
                    }
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    return;
                }
                // Modifier-only presses don't store a hotkey but are still
                // suppressed so they don't insert into the text box.
                bool isModifierOnly =
                    vk == (int)Keys.ShiftKey ||
                    vk == (int)Keys.ControlKey ||
                    vk == (int)Keys.Menu;
                if (!isModifierOnly)
                {
                    int modifiers = 0;
                    if (e.Shift) modifiers |= 1;   // HOTKEYF_SHIFT
                    if (e.Control) modifiers |= 2; // HOTKEYF_CONTROL
                    if (e.Alt) modifiers |= 4;     // HOTKEYF_ALT
                    int packed = (vk & 0xFF) | (modifiers << 8);
                    if (packed != _managedHotKey)
                    {
                        _managedHotKey = packed;
                        this.Text = FormatHotKey(packed);
                        if (HotKeyChanged != null)
                            HotKeyChanged(this, EventArgs.Empty);
                    }
                }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        // Rendered the way the comctl32 hot key control renders the same
        // value ("Ctrl + 1", "Alt + F5"), so the two code paths read alike;
        // in particular a digit is shown as the digit, not as the Keys enum
        // name "D1".
        private static string FormatHotKey(int packed)
        {
            int vk = packed & 0xFF;
            int mods = (packed >> 8) & 0xFF;
            if (vk == 0)
                return NoneLabel;
            var sb = new System.Text.StringBuilder();
            if ((mods & 2) != 0) sb.Append("Ctrl + ");
            if ((mods & 4) != 0) sb.Append("Alt + ");
            if ((mods & 1) != 0) sb.Append("Shift + ");
            if (vk >= (int)Keys.D0 && vk <= (int)Keys.D9)
                sb.Append((char)('0' + (vk - (int)Keys.D0)));
            else
                sb.Append(((Keys)vk).ToString());
            return sb.ToString();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (HotKeyChanged != null)
            {
                HotKeyChanged(this, EventArgs.Empty);
            }
        }

        protected override void  WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WindowMessages.WM_COMMAND:
                    if (WinDefApi.HIWORD(m.WParam) == WindowMessages.EN_CHANGE)
                    {
                        if (HotKeyChanged != null)
                        {
                            HotKeyChanged(this, EventArgs.Empty);
                        }
                    }
                    break;
            }
            base.WndProc(ref m);
            if (!UseManaged && _centeredTextBounds.Height > 0)
            {
                // Painting outside BeginPaint must not overwrite the visible caret.
                IntPtr caretWindow = Handle;
                bool caretHidden = Focused && (m.Msg == 0x000F || m.Msg == 0x0318) &&
                    HideCaret(caretWindow);
                try
                {
                    if (m.Msg == 0x000F || m.Msg == 0x0318) // WM_PAINT / WM_PRINTCLIENT
                    {
                        using (Graphics graphics = m.Msg == 0x000F
                            ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
                            PaintCenteredNativeText(graphics);
                    }
                    bool textChanged = m.Msg == 0x0100 || m.Msg == 0x0101 || m.Msg == 0x0104 ||
                        m.Msg == 0x0105 || m.Msg == HotKeyConstants.HKM_SETHOTKEY ||
                        m.Msg == 0x0007 || m.Msg == 0x0008 || m.Msg == 0x0051 || // Focus / WM_INPUTLANGCHANGE
                        ((m.Msg == 0x0111 || m.Msg == 0x2111) &&
                            WinDefApi.HIWORD(m.WParam) == WindowMessages.EN_CHANGE);
                    // Restrict this to updates: reading HotKey sends a nested window message.
                    if (Focused && (textChanged || m.Msg == 0x000F || m.Msg == 0x0318 ||
                        m.Msg == 0x0030 || m.Msg == 0x0201)) // WM_SETFONT / WM_LBUTTONDOWN
                    {
                        Point caret;
                        if (GetCaretPos(out caret))
                        {
                            using (Graphics graphics = Graphics.FromHwnd(Handle))
                            {
                                int width = TextRenderer.MeasureText(graphics, NativeDisplayText(), Font,
                                    new Size(int.MaxValue, int.MaxValue), NativeTextFlags).Width;
                                SetCaretPos(_centeredTextBounds.Left + width, _centeredTextBounds.Top);
                            }
                        }
                    }
                    if (textChanged) Invalidate();
                }
                finally
                {
                    if (caretHidden) ShowCaret(caretWindow);
                }
            }
        }
    }
}
