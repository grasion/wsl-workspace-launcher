using System.Collections.Generic;
using System.Linq;
using System.Windows;
using WorkspaceLauncher.Core;

namespace WorkspaceLauncher.UI;

public partial class CaptureDialog : Window
{
    private List<CaptureCandidate> _candidates = new();

    public string ProfileName => NameBox.Text.Trim();
    public List<LayoutItem> SelectedItems => _candidates.Where(c => c.Selected).Select(c => c.Item).ToList();

    public CaptureDialog(string defaultName, bool isRecapture)
    {
        InitializeComponent();
        NameBox.Text = defaultName;
        if (isRecapture)
        {
            Title = "현재 화면으로 다시 캡처";
            HeaderText.Text = "현재 화면으로 다시 캡처";
        }
        Load();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Load()
    {
        _candidates = LayoutCapture.CaptureCurrent();
        Grid.ItemsSource = _candidates;
        UpdateCount();
    }

    private void UpdateCount()
    {
        int sel = _candidates.Count(c => c.Selected);
        CountText.Text = $"창 {_candidates.Count}개 중 {sel}개 선택 · {MonitorService.GetSummary(MonitorService.GetMonitors())}";
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateCount();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var c in _candidates) c.Selected = value;
        Grid.Items.Refresh();
        UpdateCount();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "프로필 이름을 입력하세요.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }
        if (!_candidates.Any(c => c.Selected))
        {
            MessageBox.Show(this, "저장할 창을 하나 이상 선택하세요.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
