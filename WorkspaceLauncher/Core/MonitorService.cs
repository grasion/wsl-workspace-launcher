using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WorkspaceLauncher.Native;
using static WorkspaceLauncher.Native.NativeMethods;

namespace WorkspaceLauncher.Core;

public sealed class MonitorInfo
{
    /// <summary>1부터 시작. 왼쪽→오른쪽, 위→아래 순서</summary>
    public int Index { get; set; }
    public string DeviceName { get; set; } = "";
    public RectI Bounds { get; set; }
    public RectI WorkArea { get; set; }
    public bool IsPrimary { get; set; }
    public IntPtr Handle { get; set; }

    public override string ToString() => $"{Index}{(IsPrimary ? " (주)" : "")}  {Bounds.W}×{Bounds.H}";
}

public static class MonitorService
{
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                list.Add(new MonitorInfo
                {
                    Handle = hMon,
                    DeviceName = mi.szDevice,
                    Bounds = ToRect(mi.rcMonitor),
                    WorkArea = ToRect(mi.rcWork),
                    IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                });
            }
            return true;
        }, IntPtr.Zero);

        var ordered = list.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Index = i + 1;
        return ordered;
    }

    internal static RectI ToRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    /// <summary>모니터 구성 지문 — 해상도와 배치가 같으면 같은 문자열</summary>
    public static string GetSignature(IReadOnlyList<MonitorInfo> monitors) =>
        string.Join("|", monitors.Select(m => $"{m.Bounds.W}x{m.Bounds.H}@{m.Bounds.X},{m.Bounds.Y}"));

    public static string GetSummary(IReadOnlyList<MonitorInfo> monitors) =>
        $"모니터 {monitors.Count}대: " + string.Join(", ", monitors.Select(m => $"{m.Bounds.W}×{m.Bounds.H}{(m.IsPrimary ? "(주)" : "")}"));

    public static MonitorInfo? FromWindow(IntPtr hwnd, IReadOnlyList<MonitorInfo> monitors)
    {
        var h = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return monitors.FirstOrDefault(m => m.Handle == h) ?? monitors.FirstOrDefault(m => m.IsPrimary);
    }

    /// <summary>
    /// 저장된 항목 좌표를 현재 모니터 구성에 맞게 변환한다.
    /// 같은 번호의 모니터가 있으면 거기로, 없으면 주 모니터로 옮기고,
    /// 해상도가 다르면 모니터 안에서의 상대 위치/비율을 유지한다.
    /// </summary>
    public static (RectI rect, MonitorInfo monitor) MapToCurrent(LayoutItem item, IReadOnlyList<MonitorInfo> monitors)
    {
        var target = monitors.FirstOrDefault(m => m.Index == item.MonitorIndex)
                     ?? monitors.FirstOrDefault(m => m.IsPrimary)
                     ?? monitors.First();

        var saved = item.SavedMonitor;
        RectI rect;
        if (saved.W <= 0 || saved.H <= 0 || saved == target.Bounds)
        {
            // 같은 모니터(또는 모니터 정보 없음) → 절대 좌표 그대로
            rect = item.Rect;
        }
        else
        {
            double sx = target.Bounds.W / (double)saved.W;
            double sy = target.Bounds.H / (double)saved.H;
            rect = new RectI(
                target.Bounds.X + (int)Math.Round((item.X - saved.X) * sx),
                target.Bounds.Y + (int)Math.Round((item.Y - saved.Y) * sy),
                (int)Math.Round(item.Width * sx),
                (int)Math.Round(item.Height * sy));
        }

        return (Clamp(rect, target.WorkArea), target);
    }

    /// <summary>창이 모니터 작업 영역 밖으로 완전히 빠져나가지 않도록 보정</summary>
    public static RectI Clamp(RectI r, RectI area)
    {
        int w = Math.Clamp(r.W, 120, Math.Max(120, area.W));
        int h = Math.Clamp(r.H, 80, Math.Max(80, area.H));
        int x = Math.Clamp(r.X, area.X, Math.Max(area.X, area.Right - w));
        int y = Math.Clamp(r.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
        return new RectI(x, y, w, h);
    }

    public static bool Intersects(RectI a, RectI b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    public static RectI Union(IEnumerable<MonitorInfo> monitors)
    {
        var list = monitors.ToList();
        if (list.Count == 0) return default;
        int l = list.Min(m => m.Bounds.X), t = list.Min(m => m.Bounds.Y);
        int r = list.Max(m => m.Bounds.Right), b = list.Max(m => m.Bounds.Bottom);
        return new RectI(l, t, r - l, b - t);
    }
}
