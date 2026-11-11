using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WorkspaceLauncher.Core;

public sealed class RestoreReport
{
    public int Reused { get; set; }
    public int Launched { get; set; }
    public int Placed { get; set; }
    public List<string> Problems { get; } = new();

    public string Summary =>
        $"배치 {Placed}개 (기존 창 {Reused} · 새로 실행 {Launched})" +
        (Problems.Count > 0 ? $" · 문제 {Problems.Count}건" : "");
}

/// <summary>
/// 프로필 복원 엔진.
/// 1) 이미 떠 있는 창을 찾아 중복 실행 없이 재배치
/// 2) 없는 항목만 실행
/// 3) 새 창(HWND)이 나타날 때까지 폴링하며 기다렸다가 배치
/// 4) 앱이 스스로 위치를 되돌리는 경우를 대비해 잠시 후 다시 맞춤
/// UI 스레드(STA)에서 호출한다 — 탐색기 COM 자동화 때문.
/// </summary>
public sealed class LayoutRestorer
{
    public int LaunchTimeoutSec { get; init; } = 20;
    private const int PollMs = 150;

    public async Task<RestoreReport> ApplyAsync(Profile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var report = new RestoreReport();
        var monitors = MonitorService.GetMonitors();
        var items = profile.Items.Where(i => i.Enabled).ToList();
        if (items.Count == 0) return report;

        var claimed = new HashSet<IntPtr>();
        var placed = new Dictionary<LayoutItem, IntPtr>();
        var launched = new HashSet<LayoutItem>();
        var pids = new Dictionary<LayoutItem, int>();
        var noFresh = new HashSet<IntPtr>();

        // ---- 1단계: 이미 떠 있는 창 재사용 ----
        var windows = WindowEnumerator.GetTopLevelWindows();
        var paths = ExplorerPathsIfNeeded(items);
        foreach (var (item, w) in Assign(items, windows, claimed, paths, pids, noFresh))
        {
            progress?.Report($"재배치: {item.Name}");
            PlaceItem(item, w.Handle, monitors);
            placed[item] = w.Handle;
            report.Reused++;
        }

        // ---- 2단계: 없는 항목 실행 ----
        var before = new HashSet<IntPtr>(windows.Select(w => w.Handle));
        var pending = new List<LayoutItem>();
        foreach (var item in items.Where(i => !placed.ContainsKey(i)))
        {
            if (!item.LaunchIfMissing || !Launcher.CanLaunch(item))
            {
                report.Problems.Add($"{item.Name}: 열린 창이 없어 건너뜀");
                continue;
            }
            try
            {
                if (item.DelayMs > 0) await Task.Delay(item.DelayMs, ct);
                progress?.Report($"실행: {item.Name}");
                pids[item] = Launcher.Launch(item);
                launched.Add(item);
                pending.Add(item);
                report.Launched++;
            }
            catch (Exception ex)
            {
                report.Problems.Add($"{item.Name}: 실행 실패 — {ex.Message}");
            }
        }

        // ---- 3단계: 새 창이 뜰 때까지 폴링 ----
        var sw = Stopwatch.StartNew();
        while (pending.Count > 0 && sw.Elapsed.TotalSeconds < LaunchTimeoutSec)
        {
            await Task.Delay(PollMs, ct);
            var current = WindowEnumerator.GetTopLevelWindows();
            var fresh = new HashSet<IntPtr>(current.Where(w => !before.Contains(w.Handle)).Select(w => w.Handle));
            bool folderPending = pending.Any(i => i.Kind == TargetKind.Folder);
            if (fresh.Count == 0 && !folderPending) continue;

            // 탐색기는 Win11 탭 모드에서 기존 창에 탭으로 열릴 수 있어 기존 탐색기 창도 후보에 포함
            var candidates = current.Where(w => fresh.Contains(w.Handle) ||
                                                (folderPending && LayoutCapture.IsExplorerWindow(w))).ToList();
            var p = folderPending ? WindowEnumerator.GetExplorerFolderPaths() : Empty;
            foreach (var (item, w) in Assign(pending, candidates, claimed, p, pids, fresh))
            {
                progress?.Report($"배치: {item.Name}");
                PlaceItem(item, w.Handle, monitors);
                placed[item] = w.Handle;
                pending.Remove(item);
            }
        }
        foreach (var item in pending)
            report.Problems.Add($"{item.Name}: {LaunchTimeoutSec}초 안에 창을 찾지 못함");

        // ---- 4단계: 안정화 — 앱이 자기 위치를 복원하거나 스플래시가 교체된 경우 재보정 ----
        if (launched.Count > 0)
        {
            for (int round = 0; round < 3; round++)
            {
                await Task.Delay(round == 0 ? 600 : 900, ct);
                var current = WindowEnumerator.GetTopLevelWindows();
                foreach (var item in launched.Where(placed.ContainsKey).ToList())
                {
                    var h = placed[item];
                    if (WindowMover.IsAlive(h))
                    {
                        var (rect, _) = MonitorService.MapToCurrent(item, monitors);
                        bool drifted = item.State switch
                        {
                            WindowShowState.Normal => WindowMover.GetVisibleBounds(h) != rect,
                            WindowShowState.Maximized => !WindowMover.IsMaximized(h),
                            _ => false,
                        };
                        if (drifted) PlaceItem(item, h, monitors);
                        continue;
                    }
                    // 스플래시 창이 닫히고 메인 창이 새로 뜬 경우
                    claimed.Remove(h);
                    var fresh = new HashSet<IntPtr>(current.Where(w => !before.Contains(w.Handle)).Select(w => w.Handle));
                    var match = Assign(new[] { item }, current.Where(w => fresh.Contains(w.Handle)), claimed, Empty, pids, fresh).FirstOrDefault();
                    if (match.w != null)
                    {
                        PlaceItem(item, match.w.Handle, monitors);
                        placed[item] = match.w.Handle;
                    }
                }
            }
        }

        report.Placed = placed.Count;
        progress?.Report(report.Summary);
        return report;
    }

    private static readonly IReadOnlyDictionary<IntPtr, string> Empty = new Dictionary<IntPtr, string>();

    private static IReadOnlyDictionary<IntPtr, string> ExplorerPathsIfNeeded(IEnumerable<LayoutItem> items) =>
        items.Any(i => i.Kind == TargetKind.Folder) ? WindowEnumerator.GetExplorerFolderPaths() : Empty;

    private static void PlaceItem(LayoutItem item, IntPtr hwnd, IReadOnlyList<MonitorInfo> monitors)
    {
        var (rect, _) = MonitorService.MapToCurrent(item, monitors);
        WindowMover.Place(hwnd, rect, item.State);
    }

    /// <summary>
    /// 항목 × 창 점수를 모두 계산한 뒤 높은 점수부터 1:1로 배정 (같은 프로그램 창이 여러 개일 때 제목이 가장 비슷한 창끼리 짝지음)
    /// </summary>
    internal static List<(LayoutItem item, WindowInfo w)> Assign(
        IEnumerable<LayoutItem> items, IEnumerable<WindowInfo> windows, HashSet<IntPtr> claimed,
        IReadOnlyDictionary<IntPtr, string> explorerPaths, IReadOnlyDictionary<LayoutItem, int> pids,
        IReadOnlySet<IntPtr> freshSet)
    {
        var winList = windows.Where(w => !claimed.Contains(w.Handle)).ToList();
        var pairs = new List<(int score, int order, LayoutItem item, WindowInfo w)>();
        int order = 0;
        foreach (var item in items)
        {
            pids.TryGetValue(item, out int pid);
            foreach (var w in winList)
            {
                int s = Score(item, w, explorerPaths, pid, freshSet.Contains(w.Handle));
                if (s > 0) pairs.Add((s, order, item, w));
            }
            order++;
        }

        var result = new List<(LayoutItem, WindowInfo)>();
        var used = new HashSet<LayoutItem>();
        foreach (var p in pairs.OrderByDescending(p => p.score).ThenBy(p => p.order))
        {
            if (used.Contains(p.item) || claimed.Contains(p.w.Handle)) continue;
            used.Add(p.item);
            claimed.Add(p.w.Handle);
            result.Add((p.item, p.w));
        }
        return result;
    }

    internal static string EffectiveProcessName(LayoutItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ProcessName)) return item.ProcessName.Trim();
        if (item.Kind == TargetKind.App && Launcher.Expand(item.Target).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(Launcher.Expand(item.Target));
        if (item.Kind is TargetKind.Url or TargetKind.File && !string.IsNullOrWhiteSpace(item.BrowserPath))
            return Path.GetFileNameWithoutExtension(Launcher.Expand(item.BrowserPath));
        return "";
    }

    internal static int Score(LayoutItem item, WindowInfo w, IReadOnlyDictionary<IntPtr, string> explorerPaths, int launchedPid, bool fresh)
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        if (!string.IsNullOrWhiteSpace(item.TitleMatch) && !w.Title.Contains(item.TitleMatch.Trim(), OIC))
            return 0;

        var proc = EffectiveProcessName(item);
        bool procOk = string.IsNullOrEmpty(proc) || proc.Equals(w.ProcessName, OIC);
        int bonus = launchedPid != 0 && w.ProcessId == launchedPid ? 30 : 0;

        switch (item.Kind)
        {
            case TargetKind.Folder:
            {
                if (!LayoutCapture.IsExplorerWindow(w)) return 0;
                if (explorerPaths.TryGetValue(w.Handle, out var path) && WindowEnumerator.SamePath(path, item.Target))
                    return 100;
                var folderName = Path.GetFileName(Launcher.Expand(item.Target).TrimEnd('\\'));
                if (fresh && !string.IsNullOrEmpty(folderName) && w.Title.Equals(folderName, OIC)) return 40;
                return 0;
            }
            case TargetKind.File:
            {
                if (!procOk) return 0;
                // 파일 이름이 창 제목에 들어 있는 창만 — 다른 문서를 연 엑셀 창은 건드리지 않고 이 파일을 새로 연다
                var stem = DocumentResolver.FileStem(item.Target);
                if (string.IsNullOrEmpty(stem)) return 0;
                return w.Title.Contains(stem, OIC) ? 60 + bonus + TitleScore(item.CapturedTitle, w.Title, false) : 0;
            }
            case TargetKind.Url:
            {
                bool browser = string.IsNullOrEmpty(proc) ? WindowEnumerator.BrowserProcesses.Contains(w.ProcessName) : procOk;
                if (!browser) return 0;
                int ts = TitleScore(item.CapturedTitle, w.Title, true);
                // URL 이 지정된 항목은, 제목이 비슷한 기존 창이 있을 때만 재사용 (아니면 새 창으로 연다)
                if (!fresh && !string.IsNullOrWhiteSpace(item.Target) && string.IsNullOrWhiteSpace(item.TitleMatch) && ts == 0)
                    return 0;
                return 10 + ts + bonus + (fresh ? 5 : 0);
            }
            default: // App, WindowOnly
            {
                if (!procOk) return 0;
                if (string.IsNullOrEmpty(proc) && string.IsNullOrWhiteSpace(item.TitleMatch)) return 0;
                int s = 10 + bonus + TitleScore(item.CapturedTitle, w.Title, false);
                if (!string.IsNullOrEmpty(item.WindowClass) && item.WindowClass == w.ClassName) s += 5;
                return s;
            }
        }
    }

    internal static int TitleScore(string captured, string actual, bool stripSuffix)
    {
        if (string.IsNullOrWhiteSpace(captured) || string.IsNullOrWhiteSpace(actual)) return 0;
        var a = stripSuffix ? LayoutCapture.StripAppSuffix(captured) : captured.Trim();
        var b = stripSuffix ? LayoutCapture.StripAppSuffix(actual) : actual.Trim();
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return 20;
        var ta = Tokens(a);
        var tb = Tokens(b);
        int overlap = ta.Count(tb.Contains);
        return Math.Min(15, overlap * 3);
    }

    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cur = new System.Text.StringBuilder();
        foreach (var ch in s + " ")
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(ch);
            else
            {
                if (cur.Length >= 2) set.Add(cur.ToString());
                cur.Clear();
            }
        }
        return set;
    }
}
