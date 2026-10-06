using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace AmrMapEditor;

public enum DialogKind { Info, Warning, Error }

/// <summary>대화상자 버튼. Primary = 강조색, Cancel = Esc · 닫기(✕)와 같은 결과</summary>
public sealed record DialogButton(string Text, bool Primary = false, bool Cancel = false);

/// <summary>
/// 공용 대화상자: 동사형 버튼, 기본 버튼 표시(테두리 강조 + 포커스), Esc = 취소.
/// 무엇(제목) · 설명 · 항목(이름-값) · 자세히(접힘) 구성
/// </summary>
public sealed class MessageDialog : Window
{
    private int _result = -1;
    private readonly int _cancelIndex;

    private MessageDialog(DialogKind kind, string title, string message, IReadOnlyList<(string Key, string Value)>? details,
                          string? more, IReadOnlyList<DialogButton> buttons, int defaultIndex)
    {
        Title = "AMR Map Editor";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        FontSize = 14;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "LabelBrush");
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);

        _cancelIndex = -1;
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i].Cancel) _cancelIndex = i;

        // ── 본문: 아이콘 | 제목 · 설명 · 항목 · 자세히 ──
        var body = new Grid { Margin = new Thickness(24, 24, 24, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Path
        {
            Data = (Geometry)Application.Current.FindResource(kind switch
            {
                DialogKind.Error => "I.Error",
                DialogKind.Warning => "I.Warning",
                _ => "I.Info",
            }),
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0),
        };
        icon.SetResourceReference(Shape.StrokeProperty, kind switch
        {
            DialogKind.Error => "DangerTextBrush",
            DialogKind.Warning => "WarningTextBrush",
            _ => "AccentTextBrush",
        });
        body.Children.Add(icon);

        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);
        body.Children.Add(stack);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(message))
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 20 });

        if (details is { Count: > 0 })
        {
            var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < details.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new TextBlock { Text = details[r].Key, FontSize = 12, Margin = new Thickness(0, r == 0 ? 2 : 10, 12, 0), TextWrapping = TextWrapping.Wrap };
                key.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
                var val = new TextBlock { Text = details[r].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, r == 0 ? 0 : 8, 0, 0), LineHeight = 20 };
                Grid.SetRow(key, r);
                Grid.SetRow(val, r);
                Grid.SetColumn(val, 1);
                grid.Children.Add(key);
                grid.Children.Add(val);
            }
            stack.Children.Add(grid);
        }

        if (!string.IsNullOrEmpty(more))
        {
            var box = new TextBox
            {
                Text = more,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Height = double.NaN,
                MaxHeight = 200,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(8),
            };
            box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            var expander = new Expander { Header = "자세히", Content = box, Margin = new Thickness(0, 12, 0, 0) };
            expander.SetResourceReference(StyleProperty, "Disclosure");
            stack.Children.Add(expander);
        }

        // ── 버튼 줄: 왼쪽 Esc 안내, 오른쪽 버튼 ──
        var footer = new DockPanel { Margin = new Thickness(24), LastChildFill = false };
        if (_cancelIndex >= 0)
        {
            var hint = new TextBlock { Text = "Esc  " + buttons[_cancelIndex].Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            DockPanel.SetDock(hint, Dock.Left);
            footer.Children.Add(hint);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(row, Dock.Right);
        footer.Children.Add(row);

        Button? focus = null;
        for (int i = 0; i < buttons.Count; i++)
        {
            DialogButton b = buttons[i];
            var btn = new Button
            {
                Content = b.Text,
                MinWidth = 88,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsCancel = b.Cancel,
                IsDefault = i == defaultIndex,
            };
            if (b.Primary) btn.SetResourceReference(StyleProperty, "PrimaryButton");
            if (i == defaultIndex)
            {
                // 기본 버튼: Enter로 실행되는 버튼을 테두리로 표시
                btn.BorderThickness = new Thickness(2);
                btn.SetResourceReference(BorderBrushProperty, "AccentTextBrush");
                focus = btn;
            }
            int index = i;
            btn.Click += (_, _) =>
            {
                _result = index;
                Close();
            };
            row.Children.Add(btn);
        }

        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(footer);
        Content = root;

        Loaded += (_, _) => focus?.Focus();
        Closing += (_, _) =>
        {
            if (_result < 0) _result = _cancelIndex;
        };
    }

    /// <summary>대화상자를 띄우고 누른 버튼 번호 반환 (Esc · ✕ = 취소 버튼 번호, 없으면 -1)</summary>
    public static int Show(Window? owner, DialogKind kind, string title, string message,
                           IReadOnlyList<(string Key, string Value)>? details, string? more,
                           IReadOnlyList<DialogButton> buttons, int defaultIndex)
    {
        var d = new MessageDialog(kind, title, message, details, more, buttons, defaultIndex);
        if (owner != null && owner.IsVisible)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        d.ShowDialog();
        return d._result;
    }
}

/// <summary>예외를 사용자 문구(원인 · 해결)로 바꾸고 상세 내용은 로그 파일에 남김</summary>
public static class ErrorReport
{
    public static string LogPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmrMapEditor", "error.log");

    /// <summary>로그 파일에 추가하고 경로 반환 (기록 실패는 무시)</summary>
    public static string Write(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch (Exception)
        {
            // 로그 기록 실패는 무시
        }
        return LogPath;
    }

    /// <summary>원인 · 해결 문구</summary>
    public static (string Cause, string Fix) Describe(Exception ex)
    {
        const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021), DiskFull = unchecked((int)0x80070070);
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                ("파일이나 폴더를 찾을 수 없습니다.", "경로가 바뀌었거나 삭제되지 않았는지 확인하세요."),
            UnauthorizedAccessException =>
                ("파일 · 폴더에 접근할 권한이 없습니다.", "읽기 전용인지, 다른 사용자 폴더인지 확인하거나 다른 위치에 저장하세요."),
            IOException io when io.HResult == SharingViolation || io.HResult == LockViolation =>
                ("다른 프로그램이 파일을 사용 중입니다.", "GIMP 등 파일을 연 프로그램을 닫은 뒤 다시 시도하세요."),
            IOException io when io.HResult == DiskFull =>
                ("디스크 공간이 부족합니다.", "공간을 확보하거나 다른 드라이브에 저장하세요."),
            InvalidDataException =>
                (ex.Message, "8bit PGM(P5/P2) · ASCII DXF인지 확인하세요. 다른 프로그램에서 다시 저장하면 열리는 경우가 많습니다."),
            OutOfMemoryException =>
                ("메모리가 부족합니다.", "다른 프로그램을 닫거나 맵을 나눠 작업하세요."),
            IOException =>
                ($"파일을 읽거나 쓰는 중 문제가 생겼습니다. ({ex.Message})", "네트워크 드라이브라면 연결을 확인하고 다시 시도하세요."),
            _ =>
                ($"예상하지 못한 오류입니다. ({ex.Message})", "작업을 저장한 뒤 다시 시도하세요. 반복되면 '자세히'의 로그 파일을 전달해 주세요."),
        };
    }
}
