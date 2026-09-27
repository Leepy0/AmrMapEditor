using System.Windows;
using System.Windows.Threading;

namespace AmrMapEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    // 예외로 프로그램이 종료되어 편집 내용을 잃지 않도록 표시 후 계속 진행
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"예기치 않은 오류가 발생했습니다.\n\n{e.Exception.Message}", "오류",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
