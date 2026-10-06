using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace AmrMapEditor;

/// <summary>
/// 라이트 / 다크 색 토큰. XAML은 모든 색을 DynamicResource로 참조하므로
/// Application 리소스의 브러시를 바꾸면 화면 전체가 즉시 바뀜. (맵 표시 색은 테마와 무관)
/// </summary>
public static class Theme
{
    // 키: (라이트, 다크). App.xaml의 기본값(라이트)과 키가 같아야 함
    private static readonly Dictionary<string, (string Light, string Dark)> Tokens = new()
    {
        ["AccentBrush"] = ("#0062CC", "#0062CC"),            // 채움 (흰 글자 5.8:1)
        ["AccentTextBrush"] = ("#0062CC", "#5EA8FF"),        // 배경 위 글자 · 아이콘 · 포커스 링
        ["AccentSoftBrush"] = ("#E3EEFF", "#1D3557"),
        ["OnAccentBrush"] = ("#FFFFFF", "#FFFFFF"),
        ["LabelBrush"] = ("#1D1D1F", "#F5F5F7"),
        ["SecondaryLabelBrush"] = ("#636366", "#AEAEB2"),
        ["TertiaryLabelBrush"] = ("#86868B", "#8E8E93"),     // 아이콘 전용 (3:1)
        ["SeparatorBrush"] = ("#E5E5EA", "#38383A"),
        ["ControlBorderBrush"] = ("#D1D1D6", "#48484A"),     // 버튼 (글자로 구분)
        ["InputBorderBrush"] = ("#8E8E93", "#8E8E93"),       // 입력칸 · 체크 · 스위치 (3:1)
        ["ControlBrush"] = ("#FFFFFF", "#3A3A3C"),
        ["ChromeBrush"] = ("#F7F7F9", "#242426"),
        ["ChromeLineBrush"] = ("#DCDCE0", "#3A3A3C"),
        ["InspectorBrush"] = ("#F2F2F7", "#1C1C1E"),
        ["CardBrush"] = ("#FFFFFF", "#2C2C2E"),
        ["SubtleBrush"] = ("#F2F2F7", "#242426"),
        ["SegmentTrackBrush"] = ("#E6E6EB", "#3A3A3C"),
        ["SegmentSelectedBrush"] = ("#FFFFFF", "#636366"),
        ["HoverBrush"] = ("#0F000000", "#1AFFFFFF"),
        ["PressedBrush"] = ("#1F000000", "#2EFFFFFF"),
        ["ShadeBrush"] = ("#000000", "#FFFFFF"),
        ["CanvasBrush"] = ("#2C2C2E", "#2C2C2E"),
        ["CanvasLabelBrush"] = ("#F2F2F7", "#F2F2F7"),
        ["CanvasSecondaryBrush"] = ("#AEAEB2", "#AEAEB2"),
        ["TooltipBrush"] = ("#FBFBFD", "#3A3A3C"),
        ["SuccessBrush"] = ("#248A3D", "#248A3D"),
        ["WarningTextBrush"] = ("#A65300", "#FFB340"),
        ["WarningSoftBrush"] = ("#FFF4E5", "#3D2E12"),
        ["DangerTextBrush"] = ("#D70015", "#FF6961"),
        ["SwitchOffBrush"] = ("#E3E3E8", "#48484A"),
        ["SwitchOffHoverBrush"] = ("#D8D8DE", "#545456"),
        ["SliderTrackBrush"] = ("#DCDCE1", "#48484A"),
        ["KnobBrush"] = ("#FFFFFF", "#F5F5F7"),
        ["ScrollThumbBrush"] = ("#33000000", "#40FFFFFF"),
        ["ScrollThumbHoverBrush"] = ("#59000000", "#66FFFFFF"),
        ["ScrollThumbDragBrush"] = ("#73000000", "#80FFFFFF"),
        ["SwatchBorderBrush"] = ("#33000000", "#59FFFFFF"),
        ["SecondBrush"] = ("#A21CAF", "#E879F9"),
        ["SecondTileBrush"] = ("#FAE8FF", "#3B1740"),
        ["SecondLayerBrush"] = ("#D946EF", "#D946EF"),
        ["DxfBrush"] = ("#C2410C", "#FB923C"),
        ["DxfTileBrush"] = ("#FFF1E6", "#3D2414"),
    };

    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    /// <summary>테마 적용 (모든 창의 제목 표시줄도 함께)</summary>
    public static void Apply(bool dark)
    {
        IsDark = dark;
        ResourceDictionary res = Application.Current.Resources;
        foreach ((string key, (string light, string darkHex)) in Tokens)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : light));
            brush.Freeze();
            res[key] = brush;
        }
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Windows 앱 모드가 다크인지 (레지스트리 AppsUseLightTheme = 0)</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>제목 표시줄을 테마에 맞춤 (Windows 10 20H1 이상, 실패해도 무시)</summary>
    public static void ApplyTitleBar(Window w)
    {
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = IsDark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception)
        {
            // 지원하지 않는 OS
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
