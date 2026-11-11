using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WorkspaceLauncher.Core;

public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WSL";
    private const string LegacyAppName = "WorkspaceLauncher"; // 이전(포터블) 버전의 등록 이름

    public static string ExePath => Environment.ProcessPath ?? "";

    public static void SetStartWithWindows(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
        if (enable) key.SetValue(AppName, $"\"{ExePath}\" --startup");
        else key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>바탕화면에 "이 프로필 적용" 바로가기(.lnk) 생성</summary>
    public static string CreateDesktopShortcut(Profile profile)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safe = new string(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var lnkPath = Path.Combine(desktop, $"{safe} (레이아웃).lnk");

        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 을 사용할 수 없습니다.");
        object shellObj = Activator.CreateInstance(t)!;
        object? lnkObj = null;
        try
        {
            dynamic shell = shellObj;
            lnkObj = shell.CreateShortcut(lnkPath);
            dynamic lnk = lnkObj!;
            lnk.TargetPath = ExePath;
            lnk.Arguments = $"--apply \"{profile.Name}\"";
            lnk.WorkingDirectory = Path.GetDirectoryName(ExePath);
            lnk.IconLocation = ExePath + ",0";
            lnk.Description = $"WSL — {profile.Name} 레이아웃 적용";
            lnk.Save();
        }
        finally
        {
            if (lnkObj != null) Marshal.ReleaseComObject(lnkObj);
            Marshal.ReleaseComObject(shellObj);
        }
        return lnkPath;
    }
}
