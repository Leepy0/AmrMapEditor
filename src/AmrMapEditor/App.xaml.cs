using System;
using System.Windows;
using AmrMapEditor.Core;
using System.Windows.Threading;

namespace AmrMapEditor;

public partial class App : Application
{
    private bool _reporting;   // 오류 창을 띄우는 중 또 예외가 나면 기본 메시지 상자로

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // 메인 창은 만드는 데 시간이 걸리므로 먼저 시작 화면을 띄워 실행 중임을 알림
        AppSettings settings = AppSettings.Load();
        bool dark = settings.Values.TryGetValue("Theme", out string? theme) ? theme == "Dark" : Theme.SystemPrefersDark();
        double expected = settings.Values.TryGetValue("StartupMs", out string? ms) &&
                          double.TryParse(ms, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
        Splash.Show(dark, expected);
        try
        {
            Splash.Report("화면 만드는 중", 0.1);
            var main = new MainWindow();
            main.ContentRendered += (_, _) =>
            {
                main.RecordStartup();   // 다음 시작 화면의 남은 시간 계산용
                // 첫 화면이 그려진 뒤 사라짐. (화면 캡처용: 환경 변수로 잠시 더 보여 둘 수 있음)
                if (int.TryParse(Environment.GetEnvironmentVariable("AMRMAPEDITOR_SPLASH_HOLD"), out int hold) && hold > 0)
                {
                    Splash.BringToFront();
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(hold) };
                    t.Tick += (_, _) => { t.Stop(); Splash.Close(); };
                    t.Start();
                }
                else Splash.Close();
            };
            main.Show();
        }
        catch (Exception)
        {
            Splash.Close();
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Splash.Close();
        base.OnExit(e);
    }

    // 예외로 프로그램이 종료되어 편집 내용을 잃지 않도록 표시 후 계속 진행
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        string log = ErrorReport.Write(e.Exception, "처리되지 않은 예외");
        if (_reporting)
        {
            MessageBox.Show($"예상하지 못한 오류가 생겼습니다.\n\n{e.Exception.Message}\n\n로그: {log}", "AMR Map Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _reporting = true;
        try
        {
            MessageDialog.Show(Current.MainWindow, DialogKind.Error, "작업 중 예상하지 못한 오류가 생겼습니다",
                "프로그램은 계속 쓸 수 있습니다. 방금 한 작업이 반영되지 않았을 수 있으니 맵을 확인한 뒤 저장하세요.",
                new[] { ("해결", "같은 작업에서 반복되면 '자세히'의 로그 파일을 전달해 주세요.") },
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n로그: {log}",
                new[] { new DialogButton("닫기", Cancel: true) }, 0);
        }
        finally
        {
            _reporting = false;
        }
    }
}
