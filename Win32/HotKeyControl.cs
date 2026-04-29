using System;
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
        // overwrites this with the localized string (e.g. "なし" in Japanese)
        // before the dialog is constructed so newly-created HotKeyControls
        // pick up the user's chosen language.
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

        private static string FormatHotKey(int packed)
        {
            int vk = packed & 0xFF;
            int mods = (packed >> 8) & 0xFF;
            if (vk == 0)
                return NoneLabel;
            var sb = new System.Text.StringBuilder();
            if ((mods & 2) != 0) sb.Append("Ctrl+");
            if ((mods & 4) != 0) sb.Append("Alt+");
            if ((mods & 1) != 0) sb.Append("Shift+");
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
        }
    }
}
