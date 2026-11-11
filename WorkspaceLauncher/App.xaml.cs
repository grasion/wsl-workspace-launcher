using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WorkspaceLauncher.Core;
using WorkspaceLauncher.UI;

namespace WorkspaceLauncher;

public partial class App : Application
{
    private const string MutexName = "WorkspaceLauncher_SingleInstance_7F3A";
    private const string ShowEventName = "WorkspaceLauncher_Show_7F3A";

    public static new App Current => (App)Application.Current;

    public ProfileStore Store { get; } = new();
    public bool IsApplying { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action? ProfilesChanged;
    public event Action? MonitorsChanged;
    public event Action? UpdateReady;

    public UpdateService Updates { get; } = new();

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private MainWindow? _main;
    private DispatcherTimer? _displayTimer;
    private string _lastSignature = "";
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        string? applyName = GetArgValue(e.Args, "--apply");
        // --startup: Windows 시작 프로그램(레지스트리 Run)으로 실행된 경우
        bool isStartup = e.Args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        bool startInTray = isStartup || e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            if (applyName != null)
            {
                // 이미 실행 중이면 이 프로세스에서 적용만 하고 종료 (바탕화면 바로가기용)
                Store.Load();
                Dispatcher.BeginInvoke(async () =>
                {
                    var p = FindProfile(applyName);
                    if (p != null)
                        await new LayoutRestorer { LaunchTimeoutSec = Store.Data.Settings.LaunchTimeoutSec }.ApplyAsync(p);
                    else
                        MessageBox.Show($"'{applyName}' 프로필을 찾을 수 없습니다.", "WSL");
                    Shutdown();
                });
                return;
            }
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        // 지난번에 내려받아 두고 적용하지 못한 업데이트가 있으면 지금 적용 후 같은 인자로 재시작
        try
        {
            if (Updates.PendingRestart != null)
            {
                Updates.ApplyAndRestart(e.Args);
                return;
            }
        }
        catch { /* 적용 실패 시 그냥 현재 버전으로 실행 */ }

        Store.Load();
        _lastSignature = CurrentSignature();

        // 다른 인스턴스가 "창 보여줘" 신호를 보내면 메인 창 표시
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting) break;
                Dispatcher.BeginInvoke(ShowMain);
            }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();

        _tray = new TrayIcon(() => Store.Data.Profiles, CurrentSignature);
        _tray.OpenRequested += ShowMain;
        _tray.CaptureRequested += () => CaptureNewProfile(null);
        _tray.ApplyRequested += p => _ = ApplyProfileAsync(p);
        _tray.ExitRequested += ExitApp;

        _hotkeys = new HotkeyManager();
        _hotkeys.HotkeyPressed += id =>
        {
            var p = Store.Data.Profiles.FirstOrDefault(x => x.Id == id);
            if (p != null) _ = ApplyProfileAsync(p);
        };
        RegisterHotkeys();

        // 모니터 연결/해제 감지 (독 연결 등) — 연속 이벤트를 2초 디바운스
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _displayTimer.Tick += (_, _) => { _displayTimer.Stop(); OnDisplayChanged(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Windows 시작 시 실행 설정과 레지스트리 동기화 (exe 위치·인자가 바뀌었을 수 있으므로 항상 다시 기록)
        try
        {
            if (Store.Data.Settings.StartWithWindows) SystemIntegration.SetStartWithWindows(true);
            else if (SystemIntegration.IsStartWithWindows()) SystemIntegration.SetStartWithWindows(false);
        }
        catch { }

        _main = new MainWindow();
        bool hidden = applyName != null || (startInTray && Store.Data.Settings.StartMinimized);
        if (!hidden) _main.Show();

        if (applyName != null)
        {
            var p = FindProfile(applyName);
            if (p != null) _ = ApplyProfileAsync(p);
            else _tray.ShowBalloon("프로필 없음", $"'{applyName}' 프로필을 찾을 수 없습니다.", warning: true);
        }
        else if (isStartup && Store.Data.Profiles.FirstOrDefault(p => p.Id == Store.Data.Settings.StartupProfileId) is { } startupProfile)
        {
            _ = ApplyStartupProfileAsync(startupProfile);
        }
        else if (hidden)
        {
            SuggestForCurrentSetup(startup: true);
        }

        _ = UpdateLoopAsync();
    }

    // ------------------------------------------------------------------ 자동 업데이트

    /// <summary>시작 20초 후, 이후 6시간마다 GitHub 에서 새 버전 확인 → 백그라운드 다운로드</summary>
    private async Task UpdateLoopAsync()
    {
        if (!Updates.IsInstalled) return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (!_exiting && Updates.Ready == null)
        {
            try
            {
                if (await Updates.CheckAndDownloadAsync() != null)
                {
                    OnUpdateReady();
                    return;
                }
            }
            catch { /* 네트워크 오류 등은 조용히 다음 주기에 재시도 */ }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private void OnUpdateReady()
    {
        UpdateReady?.Invoke();
        SetStatus($"새 버전 v{Updates.ReadyVersion} 준비 완료 — 종료할 때 자동으로 적용됩니다.");
        _tray?.ShowBalloon($"WSL v{Updates.ReadyVersion} 업데이트 준비 완료",
            "클릭하면 지금 재시작해 적용합니다.\n(그냥 두면 프로그램을 종료할 때 자동 적용)", ApplyUpdateNow);
    }

    /// <summary>수동 "업데이트 확인" — 결과: null=설치본 아님, ""=최신, 그 외=준비된 새 버전</summary>
    public async Task<string?> CheckForUpdateNowAsync()
    {
        if (!Updates.IsInstalled) return null;
        if (Updates.Ready != null) return Updates.ReadyVersion;
        var info = await Updates.CheckAndDownloadAsync();
        if (info == null) return "";
        OnUpdateReady();
        return Updates.ReadyVersion;
    }

    public void ApplyUpdateNow()
    {
        if (IsApplying) { SetStatus("레이아웃 적용이 끝난 뒤 다시 시도하세요."); return; }
        try { Store.Save(); } catch { }
        _exiting = true;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        try { Updates.ApplyAndRestart(); }
        catch (Exception ex)
        {
            _exiting = false;
            MessageBox.Show($"업데이트를 적용하지 못했습니다.\n{ex.Message}", "WSL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Windows 시작 시 선택된 레이아웃을, 다른 시작 프로그램이 뜰 시간을 준 뒤 적용</summary>
    private async Task ApplyStartupProfileAsync(Profile profile)
    {
        int delay = Math.Clamp(Store.Data.Settings.StartupDelaySec, 0, 300);
        if (delay > 0)
        {
            SetStatus($"Windows 시작 — {delay}초 후 '{profile.Name}' 레이아웃 적용");
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
        if (_exiting) return;
        await ApplyProfileAsync(profile);
    }

    private static string? GetArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private Profile? FindProfile(string name) =>
        Store.Data.Profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public string CurrentSignature() => MonitorService.GetSignature(MonitorService.GetMonitors());

    public void SetStatus(string text) => StatusChanged?.Invoke(text);

    // ------------------------------------------------------------------ 적용

    public async Task ApplyProfileAsync(Profile profile)
    {
        if (IsApplying) { SetStatus("다른 레이아웃을 적용하는 중입니다. 잠시 후 다시 시도하세요."); return; }
        IsApplying = true;
        _tray?.SetBusy(true, $"'{profile.Name}' 적용 중");
        SetStatus($"'{profile.Name}' 적용 중...");
        try
        {
            var restorer = new LayoutRestorer { LaunchTimeoutSec = Math.Clamp(Store.Data.Settings.LaunchTimeoutSec, 3, 120) };
            var report = await restorer.ApplyAsync(profile, new Progress<string>(SetStatus));
            SetStatus($"'{profile.Name}' 적용 완료 — {report.Summary}");
            if (report.Problems.Count > 0)
            {
                var msg = string.Join("\n", report.Problems.Take(4)) + (report.Problems.Count > 4 ? $"\n외 {report.Problems.Count - 4}건" : "");
                _tray?.ShowBalloon($"'{profile.Name}' 적용 — 일부 항목 문제", msg, ShowMain, warning: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"적용 실패: {ex.Message}");
            _tray?.ShowBalloon("적용 실패", ex.Message, warning: true);
        }
        finally
        {
            IsApplying = false;
            _tray?.SetBusy(false);
        }
    }

    // ------------------------------------------------------------------ 캡처

    /// <summary>현재 화면을 캡처해 새 프로필로 저장</summary>
    public Profile? CaptureNewProfile(Window? owner)
    {
        var dlg = new CaptureDialog($"레이아웃 {DateTime.Now:MM-dd HH:mm}", isRecapture: false);
        if (!ShowDialog(dlg, owner)) return null;

        var monitors = MonitorService.GetMonitors();
        var profile = new Profile
        {
            Name = UniqueName(dlg.ProfileName),
            Items = dlg.SelectedItems,
            MonitorSignature = MonitorService.GetSignature(monitors),
            MonitorSummary = MonitorService.GetSummary(monitors),
        };
        Store.Data.Profiles.Add(profile);
        SaveAndNotify();
        SetStatus($"'{profile.Name}' 저장됨 — 창 {profile.Items.Count}개");
        return profile;
    }

    /// <summary>기존 프로필을 현재 화면으로 다시 캡처 (사용자가 입력한 URL·인자 등은 유지)</summary>
    public bool RecaptureProfile(Profile profile, Window? owner)
    {
        var dlg = new CaptureDialog(profile.Name, isRecapture: true);
        if (!ShowDialog(dlg, owner)) return false;

        var oldItems = profile.Items.ToList();
        var newItems = dlg.SelectedItems;
        foreach (var n in newItems)
        {
            var old = oldItems.FirstOrDefault(o => o.Kind == n.Kind &&
                string.Equals(o.ProcessName, n.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (n.Kind == TargetKind.Url || string.Equals(o.Target, n.Target, StringComparison.OrdinalIgnoreCase)));
            if (old == null) continue;
            oldItems.Remove(old);
            n.Name = old.Name;
            n.Arguments = old.Arguments;
            n.TitleMatch = old.TitleMatch;
            n.DelayMs = old.DelayMs;
            n.LaunchIfMissing = old.LaunchIfMissing;
            if (n.Kind == TargetKind.Url && string.IsNullOrWhiteSpace(n.Target)) n.Target = old.Target;
        }

        var monitors = MonitorService.GetMonitors();
        profile.Name = string.IsNullOrWhiteSpace(dlg.ProfileName) ? profile.Name : dlg.ProfileName.Trim();
        profile.Items = newItems;
        profile.MonitorSignature = MonitorService.GetSignature(monitors);
        profile.MonitorSummary = MonitorService.GetSummary(monitors);
        profile.UpdatedAt = DateTime.Now;
        SaveAndNotify();
        SetStatus($"'{profile.Name}' 다시 캡처됨 — 창 {profile.Items.Count}개");
        return true;
    }

    private static bool ShowDialog(Window dlg, Window? owner)
    {
        if (owner != null && owner.IsVisible) dlg.Owner = owner;
        else { dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen; dlg.Topmost = true; }
        return dlg.ShowDialog() == true;
    }

    public string UniqueName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "새 프로필" : name.Trim();
        var n = name;
        for (int i = 2; Store.Data.Profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++)
            n = $"{name} ({i})";
        return n;
    }

    // ------------------------------------------------------------------ 저장/단축키

    public void SaveAndNotify()
    {
        try { Store.Save(); }
        catch (Exception ex) { SetStatus($"저장 실패: {ex.Message}"); }
        RegisterHotkeys();
        ProfilesChanged?.Invoke();
    }

    public List<string> RegisterHotkeys()
    {
        if (_hotkeys == null) return new();
        var failed = _hotkeys.RegisterAll(Store.Data.Profiles);
        if (failed.Count > 0)
            SetStatus("단축키 등록 실패 (다른 프로그램이 사용 중이거나 중복): " + string.Join(", ", failed));
        return failed;
    }

    // ------------------------------------------------------------------ 모니터 변경

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _displayTimer?.Stop(); _displayTimer?.Start(); });

    private void OnDisplayChanged()
    {
        var sig = CurrentSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        MonitorsChanged?.Invoke();
        SuggestForCurrentSetup(startup: false);
    }

    private void SuggestForCurrentSetup(bool startup)
    {
        var settings = Store.Data.Settings;
        if (settings.OnMonitorChange == MonitorChangeAction.None || _tray == null) return;

        var sig = CurrentSignature();
        var matches = Store.Data.Profiles.Where(p => p.MonitorSignature == sig).ToList();
        var auto = matches.FirstOrDefault(p => p.AutoSwitch);
        var summary = MonitorService.GetSummary(MonitorService.GetMonitors());

        if (settings.OnMonitorChange == MonitorChangeAction.AutoApply && auto != null && !startup)
        {
            _tray.ShowBalloon("모니터 구성 변경 감지", $"{summary}\n'{auto.Name}' 레이아웃으로 전환합니다.");
            _ = ApplyProfileAsync(auto);
            return;
        }

        var candidate = auto ?? matches.FirstOrDefault();
        if (candidate == null) return;
        _tray.ShowBalloon(startup ? "이 모니터 구성에 맞는 프로필" : "모니터 구성 변경 감지",
            $"{summary}\n클릭하면 '{candidate.Name}' 레이아웃을 적용합니다.",
            () => _ = ApplyProfileAsync(candidate));
    }

    // ------------------------------------------------------------------ 창/종료

    public void ShowMain()
    {
        if (_main == null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; _main.Topmost = false;
        _main.Focus();
    }

    public void NotifyHiddenToTray()
    {
        if (Store.Data.Settings.TrayHintShown) return;
        Store.Data.Settings.TrayHintShown = true;
        try { Store.Save(); } catch { }
        _tray?.ShowBalloon("WSL 은 트레이에서 실행 중", "아이콘을 우클릭하면 레이아웃을 바로 적용할 수 있습니다. 완전히 끄려면 '종료'를 선택하세요.");
    }

    public bool IsExiting => _exiting;

    public void ExitApp()
    {
        _exiting = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        try { Store.Save(); } catch { }
        try { Updates.ApplyOnExit(); } catch { }   // 받아 둔 업데이트가 있으면 종료 후 조용히 적용
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _showEvent?.Set();
        try { _main?.Close(); } catch (InvalidOperationException) { /* 이미 닫히는 중 */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfileStore.DataDir);
            File.AppendAllText(Path.Combine(ProfileStore.DataDir, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        SetStatus($"오류: {e.Exception.Message}");
        e.Handled = true;
    }
}
