using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using static WorkspaceLauncher.Native.NativeMethods;

namespace WorkspaceLauncher.Core;

/// <summary>
/// 문서 프로그램(엑셀·워드·한글·메모장·PDF 등) 창이 지금 열고 있는 파일 경로를 알아낸다.
/// 1) Office: 창 → COM 개체(AccessibleObjectFromWindow) → Workbook/Document/Presentation.FullName
/// 2) 창 제목에 들어 있는 전체 경로 (한글 "파일.hwp [D:\폴더\] - 한글", Notepad++ 등)
/// 3) 프로세스 명령줄 인자 (파일을 더블클릭해 연 경우)
/// 4) Windows '최근 항목' 바로가기와 창 제목 매칭
/// OneDrive/SharePoint URL 은 로컬 동기화 폴더 경로로 변환한다.
/// </summary>
public static class DocumentResolver
{
    public static readonly HashSet<string> DocumentApps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Microsoft Office
        "EXCEL", "WINWORD", "POWERPNT", "VISIO", "MSACCESS", "MSPUB", "ONENOTE",
        // 한컴오피스
        "Hwp", "HCell", "HShow", "Hword", "HwpViewer", "HPdf", "Hncpdf",
        // 텍스트/PDF/기타
        "notepad", "notepad++", "wordpad", "AcroRd32", "Acrobat", "FoxitPDFReader", "FoxitPDFEditor",
        "FoxitPhantomPDF", "SumatraPDF", "PDFXEdit", "ezPDFReader", "mspaint",
    };

    public static bool IsDocumentApp(string processName) => DocumentApps.Contains(processName);

    public static string? Resolve(WindowInfo w)
    {
        if (!IsDocumentApp(w.ProcessName)) return null;
        string? path = null;
        try { path = FromOfficeCom(w); } catch { }
        path ??= Safe(() => FromTitle(w.Title));
        path ??= Safe(() => FromCommandLine(w));
        path ??= Safe(() => FromRecentItems(w.Title));
        return path;
    }

    private static string? Safe(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }

    /// <summary>표시용 파일 이름 (URL 이면 디코딩)</summary>
    public static string DisplayName(string pathOrUrl)
    {
        var s = IsUrl(pathOrUrl) ? Uri.UnescapeDataString(pathOrUrl) : pathOrUrl;
        return Path.GetFileName(s.TrimEnd('/', '\\'));
    }

    /// <summary>창 제목 매칭에 쓰는 파일 이름(확장자 제외)</summary>
    public static string FileStem(string pathOrUrl)
    {
        var s = IsUrl(pathOrUrl) ? Uri.UnescapeDataString(pathOrUrl) : Launcher.Expand(pathOrUrl);
        return Path.GetFileNameWithoutExtension(s.TrimEnd('/', '\\'));
    }

    public static bool IsUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ 1) Office COM

    private static string? FromOfficeCom(WindowInfo w)
    {
        string? childClass = w.ProcessName.ToUpperInvariant() switch
        {
            "EXCEL" => "EXCEL7",
            "WINWORD" => "_WwG",
            "POWERPNT" => "mdiClass",
            _ => null,
        };
        if (childClass == null) return null;

        var child = FindChild(w.Handle, childClass);
        if (child == IntPtr.Zero) return null;

        var iid = IID_IDispatch;
        if (AccessibleObjectFromWindow(child, OBJID_NATIVEOM, ref iid, out object? obj) != 0 || obj == null)
            return null;
        try
        {
            dynamic win = obj;
            string? full = w.ProcessName.ToUpperInvariant() switch
            {
                "EXCEL" => (string)win.Parent.FullName,        // Window.Parent = Workbook
                "WINWORD" => (string)win.Document.FullName,
                "POWERPNT" => (string)win.Presentation.FullName,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(full)) return null;
            full = MapCloudUrl(full);
            // 저장한 적 없는 새 문서("통합 문서1")는 경로가 없음
            return File.Exists(full) || IsUrl(full) ? full : null;
        }
        finally
        {
            if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    private static IntPtr FindChild(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            if (WindowEnumerator.GetClassNameOf(h) == className) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>OneDrive/SharePoint URL → 로컬 동기화 폴더 경로 (매핑 실패 시 원래 값)</summary>
    public static string MapCloudUrl(string full)
    {
        if (!IsUrl(full)) return full;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\SyncEngines\Providers\OneDrive");
            if (root == null) return full;
            string? bestNs = null, bestMount = null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("UrlNamespace") is not string ns || k.GetValue("MountPoint") is not string mp) continue;
                if (full.StartsWith(ns, StringComparison.OrdinalIgnoreCase) && (bestNs == null || ns.Length > bestNs.Length))
                {
                    bestNs = ns;
                    bestMount = mp;
                }
            }
            if (bestNs != null && bestMount != null)
            {
                var rest = Uri.UnescapeDataString(full[bestNs.Length..]).Replace('/', '\\').TrimStart('\\');
                var local = Path.Combine(bestMount, rest);
                if (File.Exists(local)) return local;
            }
        }
        catch { }
        return full;
    }

    // ------------------------------------------------------------------ 2) 창 제목

    // 한글: "보고서.hwp [D:\문서\] - 한글"
    private static readonly Regex HwpTitle = new(@"^\*?(?<name>.+?)\s*\[(?<dir>(?:[A-Za-z]:\\|\\\\)[^\]]*)\]", RegexOptions.Compiled);
    // 제목 어딘가의 전체 경로: "C:\폴더\파일.txt - Notepad++"
    private static readonly Regex FullPath = new(@"(?<p>(?:[A-Za-z]:\\|\\\\)[^<>""|?*\r\n\[\]]+?\.[\p{L}\p{N}]{1,6})(?=$|\s|\]|\))", RegexOptions.Compiled);

    private static string? FromTitle(string title)
    {
        var m = HwpTitle.Match(title);
        if (m.Success)
        {
            var p = Path.Combine(m.Groups["dir"].Value.Trim(), m.Groups["name"].Value.Trim());
            if (File.Exists(p)) return p;
        }
        foreach (Match fm in FullPath.Matches(title))
        {
            var p = fm.Groups["p"].Value.Trim();
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ------------------------------------------------------------------ 3) 명령줄

    private static string? FromCommandLine(WindowInfo w)
    {
        var cmd = GetCommandLine(w.ProcessId);
        if (string.IsNullOrEmpty(cmd)) return null;
        IntPtr argv = CommandLineToArgvW(cmd, out int argc);
        if (argv == IntPtr.Zero) return null;
        try
        {
            for (int i = 1; i < argc; i++)
            {
                var arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
                if (arg.StartsWith('/') || arg.StartsWith('-')) continue;
                if (!File.Exists(arg)) continue;
                var stem = Path.GetFileNameWithoutExtension(arg);
                if (stem.Length > 0 && w.Title.Contains(stem, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(arg);
            }
        }
        finally { LocalFree(argv); }
        return null;
    }

    public static string? GetCommandLine(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
            if (len <= 0 || len > 1 << 20) return null;
            buf = Marshal.AllocHGlobal(len);
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out _) != 0) return null;
            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
            int byteLen = (ushort)Marshal.ReadInt16(buf);
            IntPtr strPtr = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return byteLen > 0 && strPtr != IntPtr.Zero ? Marshal.PtrToStringUni(strPtr, byteLen / 2) : null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    // ------------------------------------------------------------------ 4) 최근 항목

    private static string? FromRecentItems(string title)
    {
        var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentDir)) return null;

        // "정산.xlsx [호환 모드] - Excel" → "정산.xlsx"
        var bare = Regex.Replace(LayoutCapture.StripAppSuffix(title), @"\s*\[[^\]]*\]", "").TrimStart('*').Trim();

        var candidates = new DirectoryInfo(recentDir).EnumerateFiles("*.lnk")
            .Select(f => (file: f, name: Path.GetFileNameWithoutExtension(f.Name)))   // "정산.xlsx"
            .Where(x => Path.HasExtension(x.name) && x.name.Length >= 3 &&
                        (title.Contains(x.name, StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileNameWithoutExtension(x.name).Equals(bare, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.name.Length)
            .ThenByDescending(x => x.file.LastWriteTimeUtc)
            .Take(6)
            .ToList();
        if (candidates.Count == 0) return null;

        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return null;
        object shellObj = Activator.CreateInstance(t)!;
        try
        {
            dynamic shell = shellObj;
            foreach (var (file, name) in candidates)
            {
                object? lnkObj = null;
                try
                {
                    lnkObj = shell.CreateShortcut(file.FullName);
                    dynamic lnk = lnkObj!;
                    string target = lnk.TargetPath;
                    if (!string.IsNullOrEmpty(target) && File.Exists(target) &&
                        Path.GetFileName(target).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return target;
                }
                catch { }
                finally { if (lnkObj != null && Marshal.IsComObject(lnkObj)) Marshal.ReleaseComObject(lnkObj); }
            }
        }
        finally { Marshal.ReleaseComObject(shellObj); }
        return null;
    }
}
