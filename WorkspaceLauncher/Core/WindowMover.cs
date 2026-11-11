using System;
using System.Runtime.InteropServices;
using static WorkspaceLauncher.Native.NativeMethods;

namespace WorkspaceLauncher.Core;

/// <summary>
/// 창 좌표 읽기/쓰기. 모든 좌표는 "눈에 보이는 프레임" 기준이다.
/// Windows 10/11 의 보이지 않는 리사이즈 테두리(약 7px)를
/// DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) 로 계산해 보정한다.
/// </summary>
public static class WindowMover
{
    public readonly record struct Margins(int Left, int Top, int Right, int Bottom);

    public static Margins GetInvisibleMargins(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var wr)) return default;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) != 0)
            return default;
        static int P(int v) => v is >= 0 and <= 40 ? v : 0;
        return new Margins(P(fr.Left - wr.Left), P(fr.Top - wr.Top), P(wr.Right - fr.Right), P(wr.Bottom - fr.Bottom));
    }

    /// <summary>현재 눈에 보이는 창 영역</summary>
    public static RectI GetVisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) == 0 && fr.Width > 0)
            return MonitorService.ToRect(fr);
        GetWindowRect(hwnd, out var wr);
        return MonitorService.ToRect(wr);
    }

    /// <summary>최소화 상태일 때의 "복원 시" 좌표 (화면 좌표로 변환)</summary>
    public static RectI GetRestoredBounds(IntPtr hwnd, MonitorInfo? primary)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref wp);
        var r = MonitorService.ToRect(wp.rcNormalPosition);
        // rcNormalPosition 은 작업 영역 좌표계 → 주 모니터 작업 영역 오프셋만큼 보정
        if (primary != null)
        {
            int ox = primary.WorkArea.X - primary.Bounds.X;
            int oy = primary.WorkArea.Y - primary.Bounds.Y;
            r = r with { X = r.X + ox, Y = r.Y + oy };
        }
        // 보이지 않는 테두리 대략 보정
        return new RectI(r.X + 7, r.Y, Math.Max(0, r.W - 14), Math.Max(0, r.H - 7));
    }

    /// <summary>
    /// 창을 지정된 "보이는 영역"에 정확히 맞춘다.
    /// 최소화/최대화 상태면 먼저 복원하고, 다른 DPI 모니터로 옮길 때
    /// 창 크기가 다시 계산되는 경우를 대비해 두 번 적용한다.
    /// </summary>
    public static bool Place(IntPtr hwnd, RectI target, WindowShowState state)
    {
        if (!IsWindow(hwnd)) return false;

        if (IsIconic(hwnd) || IsZoomed(hwnd))
            ShowWindow(hwnd, SW_RESTORE);

        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        bool ok = false;
        for (int pass = 0; pass < 2; pass++)
        {
            var m = GetInvisibleMargins(hwnd);
            ok = SetWindowPos(hwnd, IntPtr.Zero,
                target.X - m.Left, target.Y - m.Top,
                target.W + m.Left + m.Right, target.H + m.Top + m.Bottom,
                flags);
            if (!ok) break;
            if (GetVisibleBounds(hwnd) == target) break;
        }

        switch (state)
        {
            case WindowShowState.Maximized: ShowWindow(hwnd, SW_MAXIMIZE); break;
            case WindowShowState.Minimized: ShowWindow(hwnd, SW_MINIMIZE); break;
        }
        return ok;
    }

    public static bool IsAlive(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    public static bool IsMaximized(IntPtr hwnd) => IsZoomed(hwnd);
}
