using System;
using System.Collections.Generic;
using System.Drawing;
using WorkspaceLauncher.Core;
using WinForms = System.Windows.Forms;

namespace WorkspaceLauncher.UI;

/// <summary>작업 표시줄 트레이 아이콘 + 우클릭 메뉴</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Func<IReadOnlyList<Profile>> _profiles;
    private readonly Func<string> _currentSignature;
    private Action? _balloonAction;

    public event Action? OpenRequested;
    public event Action? CaptureRequested;
    public event Action<Profile>? ApplyRequested;
    public event Action? ExitRequested;

    public TrayIcon(Func<IReadOnlyList<Profile>> profiles, Func<string> currentSignature)
    {
        _profiles = profiles;
        _currentSignature = currentSignature;
        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        _menu.Opening += (_, _) => BuildMenu();

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "WSL — Workspace Launcher",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonAction; _balloonAction = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;
        BuildMenu();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var header = new WinForms.ToolStripMenuItem("레이아웃 적용") { Enabled = false };
        _menu.Items.Add(header);

        var sig = _currentSignature();
        var profiles = _profiles();
        if (profiles.Count == 0)
            _menu.Items.Add(new WinForms.ToolStripMenuItem("  (저장된 프로필 없음)") { Enabled = false });

        foreach (var p in profiles)
        {
            var prof = p;
            var mark = prof.MonitorSignature == sig ? "  ✓" : "";
            var mi = new WinForms.ToolStripMenuItem($"  {prof.Name}{mark}")
            {
                ShortcutKeyDisplayString = prof.Hotkey,
                ToolTipText = prof.MonitorSummary,
            };
            mi.Click += (_, _) => ApplyRequested?.Invoke(prof);
            _menu.Items.Add(mi);
        }

        _menu.Items.Add(new WinForms.ToolStripSeparator());
        var capture = new WinForms.ToolStripMenuItem("📸  현재 레이아웃 저장...");
        capture.Click += (_, _) => CaptureRequested?.Invoke();
        _menu.Items.Add(capture);
        var open = new WinForms.ToolStripMenuItem("열기") { Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        _menu.Items.Add(open);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        var exit = new WinForms.ToolStripMenuItem("종료");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);
    }

    public void ShowBalloon(string title, string text, Action? onClick = null, bool warning = false)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(5000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
    }

    public void SetBusy(bool busy, string? text = null)
    {
        var t = busy ? $"WSL — {text ?? "적용 중..."}" : "WSL — Workspace Launcher";
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    /// <summary>임베디드 Assets/app.ico 에서 현재 DPI 의 트레이 크기에 맞는 프레임을 읽는다.</summary>
    public static Icon LoadAppIcon()
    {
        var size = WinForms.SystemInformation.SmallIconSize;
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null)
            {
                using var s = res.Stream;
                return new Icon(s, size);
            }
        }
        catch { }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
