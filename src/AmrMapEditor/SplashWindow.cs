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

    // 진행 표시: 단계(코드가 알려 줌)와 시간(지난번 걸린 시간 대비 경과) 중 큰 쪽
    private static readonly DateTime ProcessStart = StartTime();
    private static double _expectedMs;          // 지난번 시작에 걸린 시간 (0 = 모름 → 왕복 막대)
    private static double _stepFraction;        // 단계 기준 진행률 0 ~ 1
    private static string _stage = "불러오는 중…";
    private static TextBlock? _stageText, _etaText;
    private static Rectangle? _bar;
    private static Border? _track;
    private static TranslateTransform? _sweep;
    private static bool _determinate;

    /// <summary>프로그램 시작(더블클릭)부터 지금까지 ms. 압축 해제 · 런타임 준비 시간도 포함</summary>
    public static double ElapsedMs => (DateTime.Now - ProcessStart).TotalMilliseconds;

    private static DateTime StartTime()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime; }
        catch (Exception) { return DateTime.Now; }
    }

    /// <param name="expectedMs">지난번 시작에 걸린 시간 (설정에 저장). 0이면 끝을 모르는 왕복 막대</param>
    public static void Show(bool dark, double expectedMs)
    {
        if (_dispatcher != null) return;
        _expectedMs = expectedMs;
        var t = new Thread(() =>
        {
            try
            {
                _window = Build(dark);
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
                _window.Closed += (_, _) => timer.Stop();
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

    /// <summary>지금 하는 일과 단계 기준 진행률(0 ~ 1). 어느 스레드에서든 호출 가능</summary>
    public static void Report(string stage, double fraction)
    {
        _stage = stage;
        _stepFraction = Math.Clamp(fraction, 0, 1);
        _dispatcher?.BeginInvoke(Refresh);
    }

    /// <summary>시작 화면 스레드에서: 글 · 막대 갱신</summary>
    private static void Refresh()
    {
        if (_window == null || _stageText == null || _etaText == null || _bar == null || _track == null) return;
        _stageText.Text = _stage;
        double elapsed = ElapsedMs;
        double byTime = _expectedMs > 0 ? Math.Min(0.95, elapsed / _expectedMs) : 0;
        double p = Math.Max(_stepFraction, byTime);
        if (_expectedMs > 0)
        {
            double remain = Math.Max(0, _expectedMs - elapsed) / 1000;
            _etaText.Text = remain >= 0.5 ? $"약 {Math.Ceiling(remain):0}초 남음" : "곧 완료";
        }
        else
        {
            _etaText.Text = "";
        }
        if (p <= 0 && _expectedMs <= 0) return;   // 아는 게 없으면 왕복 막대 유지

        if (!_determinate)
        {
            // 왕복 막대 → 채워지는 막대
            _determinate = true;
            _sweep?.BeginAnimation(TranslateTransform.XProperty, null);
            if (_sweep != null) _sweep.X = 0;
        }
        double target = Math.Max(8, _track.ActualWidth * p);
        if (Math.Abs(target - _bar.Width) < 0.5) return;
        _bar.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
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
            _stepFraction = 1;
            _stage = "완료";
            Refresh();
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

        // 진행 막대: 지난번 걸린 시간을 알면 채워지는 막대, 모르면 왕복하는 막대. 아래에 지금 하는 일 · 남은 시간
        var trackBorder = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = track, ClipToBounds = true };
        var bar = new Rectangle { Width = 72, Height = 4, RadiusX = 2, RadiusY = 2, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left };
        var move = new TranslateTransform();
        bar.RenderTransform = move;
        trackBorder.Child = bar;
        _track = trackBorder;
        _bar = bar;
        _sweep = move;
        trackBorder.SizeChanged += (_, e) =>
        {
            if (_determinate) { Refresh(); return; }
            var anim = new DoubleAnimation(-bar.Width, e.NewSize.Width, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            move.BeginAnimation(TranslateTransform.XProperty, anim);
        };
        var status = new StackPanel();
        status.Children.Add(trackBorder);
        var line2 = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _stageText = new TextBlock { Text = _stage, FontSize = 12, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis };
        _etaText = new TextBlock { FontSize = 12, Foreground = secondary, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(_etaText, 1);
        line2.Children.Add(_stageText);
        line2.Children.Add(_etaText);
        status.Children.Add(line2);
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
