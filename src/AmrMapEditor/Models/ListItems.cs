using System.ComponentModel;
using AmrMapEditor.Core;

namespace AmrMapEditor.Models;

/// <summary>체크 가능한 덩어리 후보 (노이즈 / 이중 벽 / 벽 끊김)</summary>
public sealed class BlobItem : INotifyPropertyChanged
{
    private bool _isChecked;

    public BlobItem(Blob blob, string title, string detail, bool isChecked, byte threshold = 0)
    {
        Blob = blob;
        Title = title;
        Detail = detail;
        _isChecked = isChecked;
        Threshold = threshold;
    }

    public Blob Blob { get; }

    /// <summary>검출 당시 판정값 (삭제 시 동일 기준 적용)</summary>
    public byte Threshold { get; }

    public string Title { get; }
    public string Detail { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static BlobItem Noise(int index, Blob b, byte threshold, double res) =>
        new(b, $"{index}", $"{b.Area:N0} px · {b.Bounds.Width}×{b.Bounds.Height} · {b.MaxSide * res:0.##} m", true, threshold);

    public static BlobItem Duplicate(int index, Blob b, double res) =>
        new(b, $"{index}", $"{b.Area:N0} px · 길이 {b.MaxSide * res:0.##} m", true);

    public static BlobItem Gap(int index, Blob b, double res) =>
        new(b, $"{index}", $"틈 {b.MaxSide * res:0.##} m · ({b.Bounds.X}, {b.Bounds.Y})", false);
}

/// <summary>기준 맵 대비 변경 영역 목록 항목</summary>
public sealed class RegionItem
{
    public RegionItem(int index, Blob region)
    {
        Bounds = region.Bounds;
        Title = $"{index}";
        Detail = $"{region.Area:N0} px · {region.Bounds.Width}×{region.Bounds.Height} · ({region.Bounds.X}, {region.Bounds.Y})";
    }

    public IntRect Bounds { get; }
    public string Title { get; }
    public string Detail { get; }
}

/// <summary>도면 레이어 표시 여부</summary>
public sealed class LayerItem : INotifyPropertyChanged
{
    private bool _isVisible = true;

    public LayerItem(string name, int count, bool visible)
    {
        Name = name;
        CountText = count.ToString("N0");
        _isVisible = visible;
    }

    public string Name { get; }
    public string CountText { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
