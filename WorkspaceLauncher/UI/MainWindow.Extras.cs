using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkspaceLauncher.UI;

/// <summary>후원(커피) 팝업과 버전/업데이트 표시</summary>
public partial class MainWindow
{
    private void InitExtras()
    {
        VersionText.Text = $"v{AppRef.Updates.CurrentVersion}";
        AppRef.UpdateReady += () => Dispatcher.BeginInvoke(ShowUpdateReady);
    }

    // =============================================================== 업데이트

    private void ShowUpdateReady()
    {
        UpdateButton.Content = $"⬆ v{AppRef.Updates.ReadyVersion} 지금 적용";
        UpdateButton.Foreground = (Brush)FindResource("AccentBrush");
        UpdateButton.FontWeight = FontWeights.SemiBold;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (AppRef.Updates.Ready != null)
        {
            ConfirmAndApplyUpdate();
            return;
        }

        UpdateButton.IsEnabled = false;
        var prev = UpdateButton.Content;
        UpdateButton.Content = "확인 중...";
        try
        {
            var result = await AppRef.CheckForUpdateNowAsync();
            if (result == null)
            {
                StatusText.Text = "자동 업데이트는 설치 버전(WSL-win-Setup.exe 로 설치)에서만 동작합니다.";
                UpdateButton.Content = prev;
            }
            else if (result == "")
            {
                StatusText.Text = $"최신 버전입니다 (v{AppRef.Updates.CurrentVersion}).";
                UpdateButton.Content = prev;
            }
            else
            {
                ConfirmAndApplyUpdate();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"업데이트 확인 실패: {ex.Message}";
            UpdateButton.Content = prev;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void ConfirmAndApplyUpdate()
    {
        var r = MessageBox.Show(this,
            $"새 버전 v{AppRef.Updates.ReadyVersion} 이(가) 준비되었습니다.\n(현재 v{AppRef.Updates.CurrentVersion})\n\n지금 재시작해 적용할까요?\n'아니요'를 누르면 프로그램을 종료할 때 자동으로 적용됩니다.",
            "WSL 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) AppRef.ApplyUpdateNow();
    }

    // =============================================================== 커피 후원

    private void CoffeeBanner_Click(object sender, MouseButtonEventArgs e)
    {
        try { CreateCoffeePopup().ShowDialog(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"후원 QR 코드를 표시할 수 없습니다.\n{ex.Message}",
                "커피 사주기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private Window CreateCoffeePopup()
    {
        var popup = new Window
        {
            Title = "개발자에게 커피 사주기", Width = 380, SizeToContent = SizeToContent.Height,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = FontFamily,
            ShowInTaskbar = false, Icon = Icon,
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "☕ 개발자에게 커피 사주기", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(168, 91, 19)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18),
        });

        var qr = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/coffee_qr.png")),
            Width = 260, Height = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
        };
        // 작은 QR 이미지를 확대해도 흐려지지 않게 (픽셀 그대로 확대)
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(qr);

        panel.Children.Add(new TextBlock
        {
            Text = "QR 코드를 스캔하여 개발자에게\n커피 한 잔의 응원을 보내주세요.",
            TextAlignment = TextAlignment.Center, FontSize = 13, Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 16, 0, 18),
        });
        var close = new Button
        {
            Content = "닫기", MinWidth = 90, Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Center, IsCancel = true,
        };
        close.Click += (_, _) => popup.Close();
        panel.Children.Add(close);
        popup.Content = panel;
        return popup;
    }
}
