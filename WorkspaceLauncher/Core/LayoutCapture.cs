using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WorkspaceLauncher.Core;

/// <summary>캡처 후보 — 캡처 대화상자에서 체크/해제할 수 있다.</summary>
public sealed class CaptureCandidate
{
    public bool Selected { get; set; } = true;
    public LayoutItem Item { get; init; } = new();
    public WindowInfo Window { get; init; } = new();

    public string KindLabel => KindNames.Get(Item.Kind);
    public string MonitorLabel => Item.MonitorIndex.ToString();
    public string RectLabel => Item.State == WindowShowState.Maximized ? "최대화" :
                               Item.State == WindowShowState.Minimized ? "최소화" :
                               $"{Item.X},{Item.Y}  {Item.Width}×{Item.Height}";
}

public static class KindNames
{
    public static readonly IReadOnlyList<KeyValuePair<TargetKind, string>> All = new[]
    {
        new KeyValuePair<TargetKind, string>(TargetKind.App, "프로그램"),
        new KeyValuePair<TargetKind, string>(TargetKind.Folder, "폴더"),
        new KeyValuePair<TargetKind, string>(TargetKind.File, "파일"),
        new KeyValuePair<TargetKind, string>(TargetKind.Url, "URL"),
        new KeyValuePair<TargetKind, string>(TargetKind.WindowOnly, "창만 배치"),
    };

    public static readonly IReadOnlyList<KeyValuePair<WindowShowState, string>> States = new[]
    {
        new KeyValuePair<WindowShowState, string>(WindowShowState.Normal, "보통"),
        new KeyValuePair<WindowShowState, string>(WindowShowState.Maximized, "최대화"),
        new KeyValuePair<WindowShowState, string>(WindowShowState.Minimized, "최소화"),
    };

    public static string Get(TargetKind k) => All.First(p => p.Key == k).Value;
}

public static class LayoutCapture
{
    /// <summary>현재 화면에 떠 있는 창들을 읽어 프로필 항목 후보로 만든다.</summary>
    public static List<CaptureCandidate> CaptureCurrent()
    {
        var monitors = MonitorService.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        var windows = WindowEnumerator.GetTopLevelWindows();
        var explorerPaths = windows.Any(IsExplorerWindow)
            ? WindowEnumerator.GetExplorerFolderPaths()
            : new Dictionary<IntPtr, string>();

        var result = new List<CaptureCandidate>();
        foreach (var w in windows)
        {
            var item = FromWindow(w, monitors, primary, explorerPaths);
            if (item == null) continue;
            result.Add(new CaptureCandidate
            {
                Item = item,
                Window = w,
                Selected = item.State != WindowShowState.Minimized,
            });
        }

        // Office COM 참조를 바로 해제 (Excel 이 종료 후에도 남아 있지 않도록)
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // 화면 위치 순으로 정렬 (모니터 → 왼쪽 → 위)
        return result.OrderBy(c => c.Item.MonitorIndex).ThenBy(c => c.Item.X).ThenBy(c => c.Item.Y).ToList();
    }

    public static bool IsExplorerWindow(WindowInfo w) =>
        w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
        w.ClassName is "CabinetWClass" or "ExploreWClass";

    public static LayoutItem? FromWindow(WindowInfo w, IReadOnlyList<MonitorInfo> monitors,
        MonitorInfo? primary, IReadOnlyDictionary<IntPtr, string> explorerPaths)
    {
        var mon = MonitorService.FromWindow(w.Handle, monitors);
        if (mon == null) return null;

        var item = new LayoutItem
        {
            ProcessName = w.ProcessName,
            WindowClass = w.ClassName,
            CapturedTitle = w.Title,
            MonitorIndex = mon.Index,
            MonitorDevice = mon.DeviceName,
            MonX = mon.Bounds.X, MonY = mon.Bounds.Y, MonW = mon.Bounds.W, MonH = mon.Bounds.H,
        };

        RectI rect;
        if (w.Maximized)
        {
            item.State = WindowShowState.Maximized;
            rect = mon.WorkArea;
        }
        else if (w.Minimized)
        {
            item.State = WindowShowState.Minimized;
            rect = WindowMover.GetRestoredBounds(w.Handle, primary);
        }
        else
        {
            rect = WindowMover.GetVisibleBounds(w.Handle);
        }
        item.X = rect.X; item.Y = rect.Y; item.Width = rect.W; item.Height = rect.H;

        if (IsExplorerWindow(w))
        {
            if (explorerPaths.TryGetValue(w.Handle, out var path))
            {
                item.Kind = TargetKind.Folder;
                item.Target = path;
                // 가상 폴더(::{GUID} — 홈, 갤러리, 내 PC 등)는 창 제목을 이름으로
                item.Name = "📁 " + (path.StartsWith("::") ? w.Title
                    : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path);
            }
            else
            {
                item.Kind = TargetKind.WindowOnly;
                item.Name = "📁 " + w.Title;
                item.TitleMatch = w.Title;
                item.LaunchIfMissing = false;
            }
            return item;
        }

        if (WindowEnumerator.BrowserProcesses.Contains(w.ProcessName))
        {
            item.Kind = TargetKind.Url;
            item.BrowserPath = w.ProcessPath;
            item.Name = $"🌐 {StripAppSuffix(w.Title)}";
            return item;
        }

        if (string.IsNullOrEmpty(w.ProcessPath) ||
            w.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            // 관리자 권한 프로세스 / UWP 앱: 실행 경로를 알 수 없으므로 배치만
            item.Kind = TargetKind.WindowOnly;
            item.LaunchIfMissing = false;
            item.TitleMatch = StripAppSuffix(w.Title);
            item.Name = w.Title;
            return item;
        }

        // 문서 프로그램이면 지금 열려 있는 파일을 찾아 "파일" 항목으로 저장 → 복원 시 그 파일을 같은 프로그램으로 연다
        if (DocumentResolver.IsDocumentApp(w.ProcessName))
        {
            var doc = DocumentResolver.Resolve(w);
            if (doc != null)
            {
                item.Kind = TargetKind.File;
                item.Target = doc;
                item.BrowserPath = w.ProcessPath;   // 열 프로그램
                item.Name = "📄 " + DocumentResolver.DisplayName(doc);
                return item;
            }
        }

        item.Kind = TargetKind.App;
        item.Target = w.ProcessPath;
        item.Name = FriendlyName(w);
        return item;
    }

    private static string FriendlyName(WindowInfo w)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(w.ProcessPath).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { }
        return w.ProcessName;
    }

    /// <summary>"문서 제목 - Google Chrome" → "문서 제목"</summary>
    public static string StripAppSuffix(string title)
    {
        int i = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (i <= 0) i = title.LastIndexOf(" — ", StringComparison.Ordinal);
        return i > 0 ? title[..i].Trim() : title.Trim();
    }
}
