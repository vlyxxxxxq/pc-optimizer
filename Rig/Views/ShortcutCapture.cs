using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rig.Services;

namespace Rig.Views;

/// <summary>One click-to-record editor for every global action, including cancellation and cross-page conflicts.</summary>
internal sealed class ShortcutCapture
{
    readonly Button _button;
    readonly AppSettings _settings;
    readonly string _property;
    readonly Action _pause, _resume;
    bool _recording;
    public ShortcutCapture(Button button, AppSettings settings, string property, Action pause, Action resume)
    {
        (_button, _settings, _property, _pause, _resume) = (button, settings, property, pause, resume);
        button.Click += (_, _) => { if (_recording) return; _recording = true; _pause(); button.Content = "Press keys… (Esc cancels)"; button.Focus(); };
        button.PreviewKeyDown += KeyDown;
        button.LostKeyboardFocus += (_, _) => Finish();
        button.Unloaded += (_, _) => Finish();
        void AttachOwner()
        {
            if (Window.GetWindow(button) is { } owner) owner.Deactivated += (_, _) => Finish();
        }
        if (button.IsLoaded) AttachOwner(); else button.Loaded += (_, _) => AttachOwner();
        button.IsVisibleChanged += (_, _) => { if (!button.IsVisible) Finish(); else Render(); };
        var menu = new ContextMenu();
        var reset = new MenuItem { Header = "Restore default shortcut" };
        reset.Click += (_, _) => { if (!_recording) { _recording = true; _pause(); } Apply(Hotkey.Parse(Shortcuts.Get(new AppSettings(), property))!.Value); };
        var clear = new MenuItem { Header = "Unbind this shortcut" };
        clear.Click += (_, _) => { Shortcuts.Set(settings, property, ""); settings.Save(); Render(); _resume(); };
        menu.Items.Add(reset); menu.Items.Add(clear); button.ContextMenu = menu;
        Render();
    }
    public void Render() { if (!_recording) _button.Content = Shortcuts.Get(_settings, _property) is { Length: > 0 } key ? key.Replace("+", " + ") : "Not assigned"; }
    void KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Finish(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        Apply(new Hotkey(Keyboard.Modifiers, key));
    }
    void Apply(Hotkey key)
    {
        if (!key.IsUsable) { _button.Content = "Choose a key combination"; return; }
        if (Shortcuts.Conflict(_settings, _property, key) is { } conflict) { _button.Content = $"Used by {conflict}"; return; }
        using (var probe = new HotkeyHost())
            if (!probe.Register(key, () => { })) { _button.Content = "Unavailable — choose other keys"; return; }
        Shortcuts.Set(_settings, _property, key.ToString()); _settings.Save();
        if (_recording) Finish();
        else { Render(); _resume(); }
    }
    void Finish()
    {
        if (!_recording) return;
        _recording = false; Render(); _resume();
    }
}
