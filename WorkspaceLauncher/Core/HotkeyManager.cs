using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;
using static WorkspaceLauncher.Native.NativeMethods;

namespace WorkspaceLauncher.Core;

/// <summary>RegisterHotKey 기반 전역 단축키. 메시지 전용 창으로 WM_HOTKEY 를 받는다.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _idToProfile = new();
    private int _nextId = 0xB000;

    public event Action<string>? HotkeyPressed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("WorkspaceLauncherHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0, Height = 0, WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <returns>등록에 실패한 (프로필 이름, 단축키) 목록</returns>
    public List<string> RegisterAll(IEnumerable<Profile> profiles)
    {
        UnregisterAll();
        var failed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prof in profiles.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey)))
        {
            if (!TryParse(prof.Hotkey, out uint mods, out uint vk) || !seen.Add(Normalize(prof.Hotkey)))
            {
                failed.Add($"{prof.Name} ({prof.Hotkey})");
                continue;
            }
            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk))
                _idToProfile[id] = prof.Id;
            else
                failed.Add($"{prof.Name} ({prof.Hotkey})");
        }
        return failed;
    }

    public void UnregisterAll()
    {
        foreach (var id in _idToProfile.Keys) UnregisterHotKey(_source.Handle, id);
        _idToProfile.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _idToProfile.TryGetValue(wParam.ToInt32(), out var profileId))
        {
            handled = true;
            HotkeyPressed?.Invoke(profileId);
        }
        return IntPtr.Zero;
    }

    private static string Normalize(string s) => string.Join("+", s.Split('+').Select(x => x.Trim().ToLowerInvariant()));

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": case "windows": mods |= MOD_WIN; continue;
            }
            if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
            if (!Enum.TryParse(part, true, out Key key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return mods != 0 && vk != 0;
    }

    /// <summary>키 입력 이벤트를 "Ctrl+Alt+1" 형태 문자열로</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
            or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.System) return null;
        if (modifiers == ModifierKeys.None) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var k = key.ToString();
        if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k[1..];
        else if (k.StartsWith("NumPad")) k = "NumPad" + k[6..];
        parts.Add(k);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
