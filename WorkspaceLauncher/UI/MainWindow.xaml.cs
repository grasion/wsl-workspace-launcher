using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WorkspaceLauncher.Core;

namespace WorkspaceLauncher.UI;

public partial class MainWindow : Window
{
    private static App AppRef => App.Current;
    private AppData Data => AppRef.Store.Data;

    private Profile? _current;
    private bool _loading;
    private readonly DispatcherTimer _saveTimer;
    private string _hotkeyBefore = "";

    private static readonly Color[] Palette =
    {
        Color.FromRgb(37, 99, 235), Color.FromRgb(16, 185, 129), Color.FromRgb(245, 158, 11),
        Color.FromRgb(139, 92, 246), Color.FromRgb(236, 72, 153), Color.FromRgb(14, 165, 233),
        Color.FromRgb(239, 68, 68), Color.FromRgb(132, 204, 22),
    };

    public MainWindow()
    {
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        AppRef.StatusChanged += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
        AppRef.ProfilesChanged += () => Dispatcher.BeginInvoke(RefreshProfiles);
        AppRef.MonitorsChanged += () => Dispatcher.BeginInvoke(() => { RefreshMonitorText(); RefreshProfiles(); });

        LoadSettings();
        InitExtras();
        RefreshMonitorText();
        RefreshProfiles();
        if (Data.Profiles.Count > 0) ProfilesList.SelectedIndex = 0;
    }

    // =============================================================== 공통

    private void RefreshMonitorText()
    {
        var mons = MonitorService.GetMonitors();
        MonitorText.Text = MonitorService.GetSummary(mons);
        DrawPreview();
    }

    private void RefreshProfiles()
    {
        var sig = AppRef.CurrentSignature();
        foreach (var p in Data.Profiles) p.MatchesCurrentSetup = p.MonitorSignature == sig;

        var keep = _current;
        _loading = true;
        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = Data.Profiles;
        RefreshStartupProfileCombo();
        _loading = false;
        if (keep != null && Data.Profiles.Contains(keep)) ProfilesList.SelectedItem = keep;
        else if (Data.Profiles.Count > 0) ProfilesList.SelectedIndex = 0;
        else ShowProfile(null);
    }

    private void ShowProfile(Profile? p)
    {
        _current = p;
        EmptyPanel.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) return;

        _loading = true;
        ProfileNameBox.Text = p.Name;
        HotkeyBox.Text = string.IsNullOrEmpty(p.Hotkey) ? "" : p.Hotkey;
        AutoSwitchBox.IsChecked = p.AutoSwitch;
        bool match = p.MonitorSignature == AppRef.CurrentSignature();
        ProfileMonitorText.Text = (match ? "✓ " : "⚠ 다른 구성에서 저장됨 · ") + p.MonitorSummary;
        ProfileMonitorText.Foreground = (Brush)FindResource(match ? "SuccessBrush" : "WarnBrush");
        ItemsGrid.ItemsSource = null;
        ItemsGrid.ItemsSource = p.Items;
        _loading = false;
        DrawPreview();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        if (_current != null) _current.UpdatedAt = DateTime.Now;
        _saveTimer.Stop();
        _saveTimer.Start();
        DrawPreview();
    }

    private void SaveNow()
    {
        try { AppRef.Store.Save(); }
        catch (Exception ex) { StatusText.Text = $"저장 실패: {ex.Message}"; }
    }

    private LayoutItem? SelectedItem => ItemsGrid.SelectedItem as LayoutItem;

    // =============================================================== 프로필 목록

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ShowProfile(ProfilesList.SelectedItem as Profile);
    }

    private void ProfilesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfilesList.SelectedItem is Profile p) _ = AppRef.ApplyProfileAsync(p);
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        var p = AppRef.CaptureNewProfile(this);
        if (p != null) ProfilesList.SelectedItem = p;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        CommitGrid();
        _ = AppRef.ApplyProfileAsync(_current);
    }

    private void Recapture_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        CommitGrid();
        if (AppRef.RecaptureProfile(_current, this)) ShowProfile(_current);
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            var path = SystemIntegration.CreateDesktopShortcut(_current);
            StatusText.Text = $"바탕화면 바로가기 생성: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"바로가기를 만들지 못했습니다.\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        CommitGrid();
        var copy = new Profile
        {
            Name = AppRef.UniqueName(_current.Name + " 복사본"),
            MonitorSignature = _current.MonitorSignature,
            MonitorSummary = _current.MonitorSummary,
            Items = _current.Items.Select(i => i.Clone()).ToList(),
        };
        Data.Profiles.Insert(Data.Profiles.IndexOf(_current) + 1, copy);
        AppRef.SaveAndNotify();
        ProfilesList.SelectedItem = copy;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"'{_current.Name}' 프로필을 삭제할까요?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int idx = Data.Profiles.IndexOf(_current);
        if (Data.Settings.StartupProfileId == _current.Id) Data.Settings.StartupProfileId = "";
        Data.Profiles.Remove(_current);
        _current = null;
        AppRef.SaveAndNotify();
        if (Data.Profiles.Count > 0) ProfilesList.SelectedIndex = Math.Clamp(idx, 0, Data.Profiles.Count - 1);
    }

    // =============================================================== 프로필 속성

    private void ProfileName_LostFocus(object sender, RoutedEventArgs e) => CommitName();

    private void ProfileName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitName(); Keyboard.ClearFocus(); }
    }

    private void CommitName()
    {
        if (_current == null || _loading) return;
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || name == _current.Name) { ProfileNameBox.Text = _current.Name; return; }
        if (Data.Profiles.Any(p => p != _current && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "같은 이름의 프로필이 이미 있습니다.";
            ProfileNameBox.Text = _current.Name;
            return;
        }
        _current.Name = name;
        AppRef.SaveAndNotify();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _hotkeyBefore = HotkeyBox.Text;
        HotkeyBox.Text = "키 조합을 누르세요...";
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_current != null) HotkeyBox.Text = _current.Hotkey;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_current == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { HotkeyBox.Text = _hotkeyBefore; Keyboard.ClearFocus(); return; }
        if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetHotkey("");
            return;
        }
        var text = HotkeyManager.Format(Keyboard.Modifiers, key);
        if (text == null) return; // 수식키만 누른 상태
        if (!HotkeyManager.TryParse(text, out _, out _)) { StatusText.Text = $"지원하지 않는 키: {text}"; return; }
        var dup = Data.Profiles.FirstOrDefault(p => p != _current && p.Hotkey.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (dup != null) { StatusText.Text = $"'{dup.Name}' 프로필이 이미 {text} 를 사용 중입니다."; return; }
        SetHotkey(text);
    }

    private void SetHotkey(string text)
    {
        if (_current == null) return;
        _current.Hotkey = text;
        HotkeyBox.Text = text;
        AppRef.SaveAndNotify();
        var failed = AppRef.RegisterHotkeys();
        StatusText.Text = string.IsNullOrEmpty(text) ? "단축키를 지웠습니다."
            : failed.Count == 0 ? $"단축키 {text} 등록됨" : $"{text} 등록 실패 — 다른 프로그램이 사용 중일 수 있습니다.";
        Keyboard.ClearFocus();
    }

    private void AutoSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.AutoSwitch = AutoSwitchBox.IsChecked == true;
        AppRef.SaveAndNotify();
    }

    // =============================================================== 항목 그리드

    private void CommitGrid()
    {
        ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void ItemsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            Dispatcher.BeginInvoke(MarkDirty, DispatcherPriority.Background);
    }

    private void ItemCheck_Click(object sender, RoutedEventArgs e) => MarkDirty();

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawPreview();

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var mons = MonitorService.GetMonitors();
        var m = mons.FirstOrDefault(x => x.IsPrimary) ?? mons[0];
        var item = new LayoutItem
        {
            Name = "새 항목",
            Kind = TargetKind.App,
            MonitorIndex = m.Index,
            MonitorDevice = m.DeviceName,
            MonX = m.Bounds.X, MonY = m.Bounds.Y, MonW = m.Bounds.W, MonH = m.Bounds.H,
            X = m.WorkArea.X + 80, Y = m.WorkArea.Y + 60,
            Width = Math.Min(1200, m.WorkArea.W - 160), Height = Math.Min(800, m.WorkArea.H - 120),
        };
        _current.Items.Add(item);
        ItemsGrid.Items.Refresh();
        ItemsGrid.SelectedItem = item;
        ItemsGrid.ScrollIntoView(item);
        MarkDirty();
        StatusText.Text = "새 항목을 추가했습니다. 유형과 대상(경로/URL)을 지정하세요.";
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || SelectedItem is not { } item) return;
        _current.Items.Remove(item);
        ItemsGrid.Items.Refresh();
        MarkDirty();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveItem(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveItem(1);

    private void MoveItem(int delta)
    {
        if (_current == null || SelectedItem is not { } item) return;
        int i = _current.Items.IndexOf(item), j = i + delta;
        if (j < 0 || j >= _current.Items.Count) return;
        _current.Items.RemoveAt(i);
        _current.Items.Insert(j, item);
        ItemsGrid.Items.Refresh();
        ItemsGrid.SelectedItem = item;
        MarkDirty();
    }

    private void ApplySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || SelectedItem is not { } item) { StatusText.Text = "적용할 항목을 선택하세요."; return; }
        CommitGrid();
        var temp = new Profile { Name = item.Name, Items = { item.Clone() } };
        temp.Items[0].Enabled = true;
        _ = AppRef.ApplyProfileAsync(temp);
    }

    /// <summary>선택 항목과 맞는 열린 창을 찾아 그 창의 현재 위치로 갱신</summary>
    private void RefreshPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || SelectedItem is not { } item) { StatusText.Text = "갱신할 항목을 선택하세요."; return; }
        CommitGrid();
        var windows = WindowEnumerator.GetTopLevelWindows();
        var paths = item.Kind == TargetKind.Folder ? WindowEnumerator.GetExplorerFolderPaths()
                                                   : new System.Collections.Generic.Dictionary<IntPtr, string>();
        var match = LayoutRestorer.Assign(new[] { item }, windows, new(), paths,
            new System.Collections.Generic.Dictionary<LayoutItem, int>(), new System.Collections.Generic.HashSet<IntPtr>()).FirstOrDefault();
        if (match.w == null) { StatusText.Text = $"'{item.Name}' 과 일치하는 열린 창이 없습니다."; return; }

        var mons = MonitorService.GetMonitors();
        var fresh = LayoutCapture.FromWindow(match.w, mons, mons.FirstOrDefault(m => m.IsPrimary), paths);
        if (fresh == null) return;
        item.MonitorIndex = fresh.MonitorIndex; item.MonitorDevice = fresh.MonitorDevice;
        item.MonX = fresh.MonX; item.MonY = fresh.MonY; item.MonW = fresh.MonW; item.MonH = fresh.MonH;
        item.X = fresh.X; item.Y = fresh.Y; item.Width = fresh.Width; item.Height = fresh.Height;
        item.State = fresh.State; item.CapturedTitle = fresh.CapturedTitle;
        ItemsGrid.Items.Refresh();
        MarkDirty();
        StatusText.Text = $"'{item.Name}' 위치 갱신: 모니터 {item.MonitorIndex} · {item.Rect}";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LayoutItem item) return;
        CommitGrid();
        switch (item.Kind)
        {
            case TargetKind.Folder:
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "폴더 선택" };
                if (Directory.Exists(item.Target)) dlg.InitialDirectory = item.Target;
                if (dlg.ShowDialog(this) == true)
                {
                    item.Target = dlg.FolderName;
                    if (string.IsNullOrWhiteSpace(item.Name) || item.Name == "새 항목")
                        item.Name = "📁 " + System.IO.Path.GetFileName(dlg.FolderName.TrimEnd('\\'));
                }
                break;
            }
            case TargetKind.Url:
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "브라우저 실행 파일 선택 (URL 은 '대상' 칸에 직접 입력)", Filter = "실행 파일|*.exe" };
                if (dlg.ShowDialog(this) == true)
                {
                    item.BrowserPath = dlg.FileName;
                    item.ProcessName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                }
                break;
            }
            default:
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = item.Kind == TargetKind.File ? "파일 선택" : "프로그램 선택",
                    Filter = item.Kind == TargetKind.File ? "모든 파일|*.*" : "프로그램|*.exe;*.lnk;*.bat;*.cmd|모든 파일|*.*",
                    DereferenceLinks = false,
                };
                if (dlg.ShowDialog(this) == true)
                {
                    item.Target = dlg.FileName;
                    if (item.Kind == TargetKind.WindowOnly) item.Kind = TargetKind.App;
                    if (item.Kind == TargetKind.App && dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        item.ProcessName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                    if (string.IsNullOrWhiteSpace(item.Name) || item.Name == "새 항목")
                        item.Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                }
                break;
            }
        }
        ItemsGrid.Items.Refresh();
        MarkDirty();
    }

    // =============================================================== 미리보기

    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

    private void DrawPreview()
    {
        if (PreviewCanvas == null) return;
        PreviewCanvas.Children.Clear();
        double cw = PreviewCanvas.ActualWidth, ch = PreviewCanvas.ActualHeight;
        if (cw < 20 || ch < 20) return;

        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0) return;
        var union = MonitorService.Union(monitors);
        double scale = Math.Min((cw - 8) / union.W, (ch - 8) / union.H);
        double ox = (cw - union.W * scale) / 2, oy = (ch - union.H * scale) / 2;
        Rect Map(RectI r) => new(ox + (r.X - union.X) * scale, oy + (r.Y - union.Y) * scale,
                                 Math.Max(2, r.W * scale), Math.Max(2, r.H * scale));

        foreach (var m in monitors)
        {
            var r = Map(m.Bounds);
            var border = new Border
            {
                Width = r.Width, Height = r.Height,
                Background = new SolidColorBrush(Color.FromRgb(238, 242, 247)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(196, 205, 218)),
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = $"{m.Index}{(m.IsPrimary ? " · 주" : "")}   {m.Bounds.W}×{m.Bounds.H}",
                    FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(120, 130, 145)),
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 4),
                },
            };
            Canvas.SetLeft(border, r.X); Canvas.SetTop(border, r.Y);
            PreviewCanvas.Children.Add(border);
        }

        if (_current == null) return;
        var selected = SelectedItem;
        // 큰 창을 아래에, 작은 창을 위에 그려 겹쳐도 모두 보이게
        var entries = _current.Items
            .Select((item, i) => (item, color: Palette[i % Palette.Length]))
            .Where(x => x.item.Enabled && x.item.State != WindowShowState.Minimized)
            .Select(x =>
            {
                var (rect, mon) = MonitorService.MapToCurrent(x.item, monitors);
                if (x.item.State == WindowShowState.Maximized) rect = mon.WorkArea;
                return (x.item, x.color, rect, mon);
            })
            .OrderByDescending(x => (long)x.rect.W * x.rect.H)
            .ToList();
        int z = 10;
        foreach (var (item, color, rect, mon) in entries)
        {
            var r = Map(rect);
            bool isSel = ReferenceEquals(item, selected);
            var box = new Border
            {
                Width = r.Width, Height = r.Height,
                Background = new SolidColorBrush(Color.FromArgb(isSel ? (byte)200 : (byte)120, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(isSel ? Color.FromRgb(15, 23, 42) : color),
                BorderThickness = new Thickness(isSel ? 2 : 1), CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand, ToolTip = $"{item.Name}\n{KindNames.Get(item.Kind)} · 모니터 {mon.Index} · {rect}",
                Child = new TextBlock
                {
                    Text = item.Name, FontSize = 10.5, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 2, 4, 0),
                },
                Tag = item,
            };
            box.MouseLeftButtonDown += (s, _) => { ItemsGrid.SelectedItem = ((Border)s).Tag; ItemsGrid.ScrollIntoView(ItemsGrid.SelectedItem); };
            Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y);
            Panel.SetZIndex(box, isSel ? 1000 : z++);
            PreviewCanvas.Children.Add(box);
        }
    }

    // =============================================================== 설정

    private void LoadSettings()
    {
        _loading = true;
        var s = Data.Settings;
        StartWithWindowsBox.IsChecked = s.StartWithWindows;
        StartMinimizedBox.IsChecked = s.StartMinimized;
        CloseToTrayBox.IsChecked = s.CloseToTray;
        MonitorActionCombo.SelectedItem = MonitorActionCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.OnMonitorChange.ToString());
        TimeoutBox.Text = s.LaunchTimeoutSec.ToString();
        StartupDelayBox.Text = s.StartupDelaySec.ToString();
        StartupProfilePanel.Visibility = s.StartWithWindows ? Visibility.Visible : Visibility.Collapsed;
        RefreshStartupProfileCombo();
        _loading = false;
    }

    /// <summary>"시작 시 적용할 레이아웃" 목록 — 맨 앞은 '적용 안 함'</summary>
    private void RefreshStartupProfileCombo()
    {
        var items = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
        {
            new("", "(적용 안 함 — 트레이에서만 실행)"),
        };
        items.AddRange(Data.Profiles.Select(p => new System.Collections.Generic.KeyValuePair<string, string>(p.Id, p.Name)));

        var id = Data.Settings.StartupProfileId;
        if (!string.IsNullOrEmpty(id) && Data.Profiles.All(p => p.Id != id)) id = Data.Settings.StartupProfileId = "";

        bool wasLoading = _loading;
        _loading = true;
        StartupProfileCombo.ItemsSource = items;
        StartupProfileCombo.SelectedValue = id;
        _loading = wasLoading;
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var s = Data.Settings;
        bool startup = StartWithWindowsBox.IsChecked == true;
        if (startup != s.StartWithWindows)
        {
            try { SystemIntegration.SetStartWithWindows(startup); s.StartWithWindows = startup; }
            catch (Exception ex) { StatusText.Text = $"시작 프로그램 등록 실패: {ex.Message}"; StartWithWindowsBox.IsChecked = s.StartWithWindows; }
        }
        StartupProfilePanel.Visibility = s.StartWithWindows ? Visibility.Visible : Visibility.Collapsed;
        if (StartupProfileCombo.SelectedValue is string pid) s.StartupProfileId = pid;
        if (int.TryParse(StartupDelayBox.Text, out int d)) s.StartupDelaySec = Math.Clamp(d, 0, 300);
        StartupDelayBox.Text = s.StartupDelaySec.ToString();
        s.StartMinimized = StartMinimizedBox.IsChecked == true;
        s.CloseToTray = CloseToTrayBox.IsChecked == true;
        if (MonitorActionCombo.SelectedItem is ComboBoxItem ci && Enum.TryParse<MonitorChangeAction>((string)ci.Tag, out var act))
            s.OnMonitorChange = act;
        if (int.TryParse(TimeoutBox.Text, out int t)) s.LaunchTimeoutSec = Math.Clamp(t, 3, 120);
        TimeoutBox.Text = s.LaunchTimeoutSec.ToString();
        SaveNow();
        if (sender == StartupProfileCombo || sender == StartWithWindowsBox)
        {
            var name = Data.Profiles.FirstOrDefault(p => p.Id == s.StartupProfileId)?.Name;
            StatusText.Text = !s.StartWithWindows ? "Windows 시작 시 실행 해제"
                : name == null ? "Windows 시작 시 트레이에서 실행 (레이아웃 적용 안 함)"
                : $"Windows 시작 {s.StartupDelaySec}초 후 '{name}' 레이아웃을 자동 적용합니다.";
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ProfileStore.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProfileStore.DataDir}\"") { UseShellExecute = true });
    }

    // =============================================================== 닫기

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitGrid();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
        if (!AppRef.IsExiting)
        {
            if (Data.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                AppRef.NotifyHiddenToTray();
            }
            else
            {
                Dispatcher.BeginInvoke(AppRef.ExitApp);
            }
        }
        base.OnClosing(e);
    }
}
