using System;
using System.Diagnostics;
using System.IO;

namespace WorkspaceLauncher.Core;

/// <summary>항목 유형별 실행 방법</summary>
public static class Launcher
{
    public static bool CanLaunch(LayoutItem i) => i.Kind switch
    {
        TargetKind.WindowOnly => false,
        TargetKind.Url => !string.IsNullOrWhiteSpace(i.Target) || !string.IsNullOrWhiteSpace(i.BrowserPath),
        _ => !string.IsNullOrWhiteSpace(i.Target),
    };

    /// <returns>새로 만들어진 프로세스 ID (알 수 없으면 0)</returns>
    public static int Launch(LayoutItem item)
    {
        string target = Expand(item.Target);
        ProcessStartInfo psi;

        switch (item.Kind)
        {
            case TargetKind.App:
                psi = new ProcessStartInfo(target) { Arguments = item.Arguments ?? "", UseShellExecute = true };
                if (File.Exists(target) && Path.GetDirectoryName(target) is { } dir)
                    psi.WorkingDirectory = dir;
                break;

            case TargetKind.Folder:
                bool virtualPath = target.StartsWith("::") || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
                if (!virtualPath && !Directory.Exists(target))
                    throw new DirectoryNotFoundException($"폴더가 없습니다: {target}");
                psi = new ProcessStartInfo("explorer.exe", Quote(target)) { UseShellExecute = true };
                break;

            case TargetKind.File:
            {
                bool isUrl = DocumentResolver.IsUrl(target);
                if (!isUrl && !File.Exists(target))
                    throw new FileNotFoundException($"파일이 없습니다: {target}");
                // 캡처한 프로그램(예: EXCEL.EXE)으로 연다. 스토어 앱(WindowsApps)은 직접 실행이 안 되므로 연결 프로그램 사용
                var app = Expand(item.BrowserPath);
                if (app.Length > 0 && File.Exists(app) && !app.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var withApp = new ProcessStartInfo(app, $"{item.Arguments} {Quote(target)}".Trim()) { UseShellExecute = true };
                        using var pa = Process.Start(withApp);
                        try { return pa?.Id ?? 0; } catch { return 0; }
                    }
                    catch { /* 실패하면 연결 프로그램으로 */ }
                }
                psi = new ProcessStartInfo(target) { UseShellExecute = true };
                break;
            }

            case TargetKind.Url:
                if (!string.IsNullOrWhiteSpace(item.BrowserPath))
                {
                    var browser = Expand(item.BrowserPath);
                    var name = Path.GetFileNameWithoutExtension(browser);
                    var newWindow = name.Equals("firefox", StringComparison.OrdinalIgnoreCase) ? "-new-window" : "--new-window";
                    var args = $"{item.Arguments} {newWindow}".Trim();
                    if (!string.IsNullOrWhiteSpace(target)) args += " " + Quote(target);
                    psi = new ProcessStartInfo(browser, args) { UseShellExecute = true };
                }
                else
                {
                    psi = new ProcessStartInfo(target) { UseShellExecute = true };
                }
                break;

            default:
                return 0;
        }

        using var p = Process.Start(psi);
        try { return p?.Id ?? 0; } catch { return 0; }
    }

    public static string Expand(string s) => Environment.ExpandEnvironmentVariables((s ?? "").Trim().Trim('"'));

    private static string Quote(string s) => s.StartsWith('"') ? s : $"\"{s}\"";
}
