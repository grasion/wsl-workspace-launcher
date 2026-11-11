using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace WorkspaceLauncher.Core;

/// <summary>
/// GitHub Releases 기반 자동 업데이트 (Velopack).
/// 백그라운드에서 새 버전을 내려받아 두고, 사용자가 재시작하거나 프로그램을 종료할 때 적용한다.
/// 설치본(Setup.exe 로 설치)에서만 동작하며, 개발/포터블 실행에서는 조용히 건너뛴다.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/grasion/wsl-workspace-launcher";

    private readonly UpdateManager? _mgr;

    public UpdateInfo? Ready { get; private set; }

    public UpdateService()
    {
        try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch { _mgr = null; }
    }

    public bool IsInstalled => _mgr?.IsInstalled == true;

    public string CurrentVersion =>
        _mgr?.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public string? ReadyVersion => Ready?.TargetFullRelease.Version.ToString();

    /// <summary>이전에 내려받아 두었지만 아직 적용 못한 업데이트 (예: 종료 전에 PC 가 꺼진 경우)</summary>
    public VelopackAsset? PendingRestart => IsInstalled ? _mgr!.UpdatePendingRestart : null;

    /// <summary>새 버전이 있으면 내려받는다. 반환값: 내려받은 업데이트 (없으면 null)</summary>
    public async Task<UpdateInfo?> CheckAndDownloadAsync()
    {
        if (!IsInstalled) return null;
        var info = await _mgr!.CheckForUpdatesAsync();
        if (info == null) return null;
        await _mgr.DownloadUpdatesAsync(info);
        Ready = info;
        return info;
    }

    /// <summary>지금 바로 적용하고 재시작 (이 호출 후 프로세스가 종료된다)</summary>
    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        var asset = Ready?.TargetFullRelease ?? PendingRestart;
        if (_mgr != null && asset != null) _mgr.ApplyUpdatesAndRestart(asset, restartArgs);
    }

    /// <summary>프로그램이 종료되면 조용히 적용 (재시작 안 함)</summary>
    public void ApplyOnExit()
    {
        var asset = Ready?.TargetFullRelease;
        if (_mgr != null && asset != null) _mgr.WaitExitThenApplyUpdates(asset, silent: true, restart: false);
    }
}
