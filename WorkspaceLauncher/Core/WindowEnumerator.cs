using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static WorkspaceLauncher.Native.NativeMethods;

namespace WorkspaceLauncher.Core;

public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public bool Minimized { get; init; }
    public bool Maximized { get; init; }

    public override string ToString() => $"[{ProcessName}] {Title}";
}

public static class WindowEnumerator
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "Windows.Internal.Shell.TabProxyWindow", "ForegroundStaging",
    };

    public static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "whale", "brave", "opera", "vivaldi", "iexplore",
    };

    private static readonly int OwnPid = Environment.ProcessId;

    /// <summary>작업 표시줄에 보이는 일반 최상위 창만 열거</summary>
    public static List<WindowInfo> GetTopLevelWindows()
    {
        var result = new List<WindowInfo>();
        var pathCache = new Dictionary<uint, string>();

        EnumWindows((hwnd, _) =>
        {
            try
            {
                var info = TryCreate(hwnd, pathCache);
                if (info != null) result.Add(info);
            }
            catch { /* 개별 창 오류는 무시 */ }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static WindowInfo? TryCreate(IntPtr hwnd, Dictionary<uint, string> pathCache)
    {
        if (!IsWindowVisible(hwnd)) return null;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return null;

        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((style & WS_CHILD) != 0) return null;
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow && (ex & WS_EX_TOOLWINDOW) != 0) return null;
        if (!appWindow && (ex & WS_EX_NOACTIVATE) != 0) return null;
        if (IsCloaked(hwnd)) return null;

        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return null;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cls = GetClassNameOf(hwnd);
        if (IgnoredClasses.Contains(cls)) return null;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return null;

        if (!pathCache.TryGetValue(pid, out var path))
        {
            path = GetProcessPath(pid);
            pathCache[pid] = path;
        }

        return new WindowInfo
        {
            Handle = hwnd,
            Title = title,
            ClassName = cls,
            ProcessId = pid,
            ProcessPath = path,
            ProcessName = string.IsNullOrEmpty(path) ? SafeProcessName(pid) : Path.GetFileNameWithoutExtension(path),
            Minimized = IsIconic(hwnd),
            Maximized = IsZoomed(hwnd),
        };
    }

    public static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetTitleOf(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    public static string GetProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    private static string SafeProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return ""; }
    }

    /// <summary>
    /// 열려 있는 탐색기 창의 HWND → 현재 폴더 경로.
    /// Shell.Application COM 자동화를 사용한다 (UI 스레드 = STA 에서 호출).
    /// </summary>
    public static Dictionary<IntPtr, string> GetExplorerFolderPaths()
    {
        var result = new Dictionary<IntPtr, string>();
        object? shellObj = null;
        object? windowsObj = null;
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return result;
            shellObj = Activator.CreateInstance(t);
            dynamic shell = shellObj!;
            windowsObj = shell.Windows();
            dynamic windows = windowsObj!;
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                object? wObj = null;
                try
                {
                    wObj = windows.Item(i);
                    if (wObj == null) continue;
                    dynamic w = wObj;
                    string fullName = w.FullName ?? "";
                    if (!fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var hwnd = new IntPtr(Convert.ToInt64(w.HWND));
                    string? path = null;
                    try { path = w.Document.Folder.Self.Path; } catch { }
                    if (!string.IsNullOrEmpty(path) && !result.ContainsKey(hwnd))
                        result[hwnd] = path!;
                }
                catch { }
                finally { if (wObj != null && Marshal.IsComObject(wObj)) Marshal.ReleaseComObject(wObj); }
            }
        }
        catch { }
        finally
        {
            if (windowsObj != null && Marshal.IsComObject(windowsObj)) Marshal.ReleaseComObject(windowsObj);
            if (shellObj != null && Marshal.IsComObject(shellObj)) Marshal.ReleaseComObject(shellObj);
        }
        return result;
    }

    public static bool SamePath(string a, string b)
    {
        static string N(string p)
        {
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                p = Uri.UnescapeDataString(p[8..]).Replace('/', '\\');
            return p.TrimEnd('\\', '/');
        }
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }
}
