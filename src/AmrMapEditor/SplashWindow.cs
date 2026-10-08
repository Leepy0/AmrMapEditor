using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AmrMapEditor;

/// <summary>
/// 시작 화면. 실행 직후 "불러오는 중"을 보여 주어 프로그램이 뜨고 있음을 알린다.
/// 메인 창을 만드는 동안 UI 스레드가 바쁘므로 별도 스레드에서 띄워 진행 표시가 멈추지 않게 한다.
/// (앱 전역 리소스 · 스타일은 다른 스레드 것이라 쓰지 않고 색을 직접 지정)
/// </summary>
public static class Splash
{
    private static Window? _window;
    private static Dispatcher? _dispatcher;
    private static readonly ManualResetEventSlim Shown = new(false);

    public static void Show(bool dark)
    {
        if (_dispatcher != null) return;
        var t = new Thread(() =>
        {
            try
            {
                _window = Build(dark);
                _window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                _window.Show();
                _dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception)
            {
                // 시작 화면은 못 띄워도 프로그램은 정상 실행
                _window = null;
                return;
            }
            finally
            {
                Shown.Set();
            }
            Dispatcher.Run();
        })
        { Name = "Splash", IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Shown.Wait(2000);   // 메인 창 준비 전에 먼저 보이도록 잠깐 기다림
    }

    /// <summary>메인 창이 그려진 뒤 호출. 살짝 사라지며 닫힘</summary>
    public static void Close()
    {
        Dispatcher? d = _dispatcher;
        if (d == null || _window == null) return;
        _dispatcher = null;
        d.BeginInvoke(() =>
        {
            Window w = _window!;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
            fade.Completed += (_, _) => w.Close();
            w.BeginAnimation(UIElement.OpacityProperty, fade);
        });
    }

    private static Window Build(bool dark)
    {
        var bg = Hex(dark ? "#2C2C2E" : "#FFFFFF");
        var line = Hex(dark ? "#48484A" : "#D1D1D6");
        var label = Hex(dark ? "#F5F5F7" : "#1D1D1F");
        var secondary = Hex(dark ? "#AEAEB2" : "#636366");
        var track = Hex(dark ? "#3A3A3C" : "#E6E6EB");
        var accent = Hex("#0062CC");

        var grid = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 아이콘 (exe에 넣은 app.ico)
        var icon = new Image { Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        try
        {
            icon.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch (Exception)
        {
            icon.Width = 0;
            icon.Margin = new Thickness(0);
        }
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock
        {
            Text = "AMR Map Editor", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = label,
        });
        text.Children.Add(new TextBlock
        {
            Text = VersionLabel(), FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 2, 0, 0),
        });
        grid.Children.Add(text);

        // 진행 표시 (끝을 알 수 없으므로 왕복하는 막대) + 상태 글
        var trackBorder = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = track, ClipToBounds = true };
        var bar = new Rectangle { Width = 72, Height = 4, RadiusX = 2, RadiusY = 2, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left };
        var move = new TranslateTransform();
        bar.RenderTransform = move;
        trackBorder.Child = bar;
        trackBorder.SizeChanged += (_, e) =>
        {
            var anim = new DoubleAnimation(-bar.Width, e.NewSize.Width, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            move.BeginAnimation(TranslateTransform.XProperty, anim);
        };
        var status = new StackPanel();
        status.Children.Add(trackBorder);
        status.Children.Add(new TextBlock
        {
            Text = "불러오는 중…", FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 10, 0, 0),
        });
        Grid.SetRow(status, 2);
        Grid.SetColumnSpan(status, 2);
        grid.Children.Add(status);

        // 투명 창 + 둥근 모서리 (사라질 때 Opacity 애니메이션도 이 설정이 있어야 동작)
        var root = new Border
        {
            Background = bg, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Margin = new Thickness(8), Child = grid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25 },
        };
        var w = new Window
        {
            Title = "AMR Map Editor",
            Width = 396, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true, Background = Brushes.Transparent, Content = root,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
            UseLayoutRounding = true, SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        RenderOptions.SetClearTypeHint(w, ClearTypeHint.Enabled);
        return w;
    }

    private static string VersionLabel()
    {
        Assembly asm = typeof(Splash).Assembly;
        string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        string ver = info.Split('+')[0];
        if (string.IsNullOrEmpty(ver)) ver = asm.GetName().Version?.ToString(3) ?? "";
        string channel = "";
        foreach (AssemblyMetadataAttribute m in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (m.Key == "BuildChannel") channel = m.Value ?? "";
        return channel switch
        {
            "dev" => $"v{ver} · 테스트 빌드",
            "local" => $"v{ver} · 로컬 빌드",
            _ => $"v{ver}",
        };
    }

    private static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
