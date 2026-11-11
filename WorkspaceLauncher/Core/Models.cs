using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WorkspaceLauncher.Core;

/// <summary>실행 타겟 유형</summary>
public enum TargetKind
{
    App,        // 실행 파일(.exe, .lnk, shell:AppsFolder\...)
    Folder,     // 탐색기 폴더 창
    File,       // 문서 파일(연결 프로그램으로 열기)
    Url,        // 브라우저 URL
    WindowOnly  // 실행하지 않고 이미 떠 있는 창만 배치
}

public enum WindowShowState { Normal, Maximized, Minimized }

public enum MonitorChangeAction { None, Suggest, AutoApply }

public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public override string ToString() => $"{X},{Y}  {W}×{H}";
}

/// <summary>프로필 안의 창 하나</summary>
public class LayoutItem
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public TargetKind Kind { get; set; } = TargetKind.App;

    /// <summary>exe 경로 / 폴더 경로 / 파일 경로 / URL</summary>
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Url 유형일 때 사용할 브라우저 실행 파일 (비우면 기본 브라우저)</summary>
    public string BrowserPath { get; set; } = "";

    /// <summary>창 매칭에 쓰는 프로세스 이름 (확장자 제외, 예: chrome)</summary>
    public string ProcessName { get; set; } = "";
    public string WindowClass { get; set; } = "";
    public string CapturedTitle { get; set; } = "";
    /// <summary>지정하면 창 제목에 이 문자열이 포함된 창만 매칭</summary>
    public string TitleMatch { get; set; } = "";

    public bool LaunchIfMissing { get; set; } = true;
    public int DelayMs { get; set; }

    // 배치 정보 — 화면 절대 좌표(물리 픽셀), 눈에 보이는 프레임 기준
    public int MonitorIndex { get; set; } = 1;
    public string MonitorDevice { get; set; } = "";
    public int MonX { get; set; }
    public int MonY { get; set; }
    public int MonW { get; set; }
    public int MonH { get; set; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public WindowShowState State { get; set; } = WindowShowState.Normal;

    [JsonIgnore] public RectI Rect => new(X, Y, Width, Height);
    [JsonIgnore] public RectI SavedMonitor => new(MonX, MonY, MonW, MonH);

    public LayoutItem Clone() => (LayoutItem)MemberwiseClone();
}

public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 프로필";
    public string Hotkey { get; set; } = "";
    public string MonitorSignature { get; set; } = "";
    public string MonitorSummary { get; set; } = "";
    /// <summary>이 모니터 구성이 감지되면 자동 전환/추천 대상</summary>
    public bool AutoSwitch { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<LayoutItem> Items { get; set; } = new();

    [JsonIgnore] public bool MatchesCurrentSetup { get; set; }
    [JsonIgnore] public string ListSubtitle =>
        $"{Items.Count}개 창" + (string.IsNullOrEmpty(Hotkey) ? "" : $" · {Hotkey}");
}

public class AppSettings
{
    public bool StartWithWindows { get; set; }
    /// <summary>Windows 시작 시 자동 적용할 프로필 Id (빈 값이면 적용 안 함)</summary>
    public string StartupProfileId { get; set; } = "";
    /// <summary>Windows 시작 후 레이아웃 적용 전 대기 시간 — 다른 시작 프로그램이 뜰 시간을 줌</summary>
    public int StartupDelaySec { get; set; } = 5;
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public MonitorChangeAction OnMonitorChange { get; set; } = MonitorChangeAction.Suggest;
    public int LaunchTimeoutSec { get; set; } = 20;
    public bool TrayHintShown { get; set; }
}

public class AppData
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();
}
