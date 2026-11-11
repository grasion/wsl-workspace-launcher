using System;
using Velopack;
using WorkspaceLauncher.Core;

namespace WorkspaceLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 설치/업데이트/제거 훅은 WPF 창을 만들기 전에 처리해야 한다.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // 제거 시 Windows 시작 프로그램 등록도 정리
                try { SystemIntegration.SetStartWithWindows(false); } catch { }
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
