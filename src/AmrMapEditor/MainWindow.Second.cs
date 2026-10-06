using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;
using AmrMapEditor.Models;
using Microsoft.Win32;

namespace AmrMapEditor;

public enum SecondView { Overlay, Diff, Hidden }

/// <summary>
/// 맞추기: 다른 맵(맞출 맵)을 현재 맵 위에 겹쳐 위치를 맞춘 뒤 비교 · 현재 맵에 반영 · 합쳐서 큰 맵 만들기.
/// 현재 맵 좌표 · origin은 항상 유지하고 맞출 맵만 움직임
/// </summary>
public partial class MainWindow
{
    private const int MaxSecondRegions = 500;

    private MapImage? _second;
    private PointD[] _secondHull = Array.Empty<PointD>();   // 맞출 맵에서 Unknown이 아닌 부분의 볼록 껍질 (외곽선 · 합친 크기 · 반영 범위)
    private PointD _secondHullCenter;
    private string? _secondPath;
    private MapMeta? _secondMeta;
    private MapPose _secondPose;
    private readonly List<MapPose> _secondHistory = new();   // 위치 되돌리기
    private bool _secondKeyBurst;                             // 연속 키 이동은 되돌리기 한 번으로
    private AlignDiff? _secondDiff;
    private AlignResult? _secondAlign;
    private int _secondAltIndex;
    private SecondView _secondView = SecondView.Overlay;
    private readonly ObservableCollection<RegionItem> _secondRegions = new();
    private DispatcherTimer? _secondDiffTimer;
    private bool _secondBusy;   // 자동 정렬 계산 중: 맞출 맵 위치 변경 · 닫기 막음 (현재 맵 편집은 복사본으로 계산하므로 가능)

    // 끌기 (이동 / Shift = 회전)
    private bool _secondDragging, _secondRotating;
    private PointD _secondDragStart, _secondPivot;
    private MapPose _secondDragPose;

    // 2점 맞추기: 1 맞출 맵 A → 2 현재 맵 A → 3 맞출 맵 B → 4 현재 맵 B
    private int _pairStep;
    private PointD _pairM1, _pairB1, _pairM2;
    private readonly List<PointD> _pairMarks = new();

    // ───────────── 열기 / 닫기 ─────────────

    private void OnOpenSecond(object sender, RoutedEventArgs e)
    {
        if (_map == null)
        {
            SetStatus("먼저 현재 맵을 여세요.");
            return;
        }
        if (SecondBusyBlocked()) return;
        var dlg = new OpenFileDialog { Filter = PgmFilter, Title = "맞출 맵 열기 (나눠 그린 맵 · 새로 그린 일부 맵)" };
        if (dlg.ShowDialog(this) == true) OpenSecond(dlg.FileName);
    }

    private void OpenSecond(string path)
    {
        if (_map == null) return;
        MapImage m;
        while (true)
        {
            try
            {
                m = PgmIO.Read(path);
                break;
            }
            catch (Exception ex)
            {
                if (!ShowFailure("맞출 맵을 열 수 없습니다", path, ex, canRetry: true)) return;
            }
        }
        OpenSecond(path, m);
    }

    private void OpenSecond(string path, MapImage m)
    {
        if (_map == null) return;
        MapMeta? meta = MapMeta.TryLoadForImage(path);
        if (meta?.SourcePath != null && Math.Abs(meta.Resolution - _meta.Resolution) > _meta.Resolution * 1e-6)
        {
            ShowError("해상도가 달라 맞출 수 없습니다",
                $"현재 맵 {_meta.Resolution} m/px, 맞출 맵 {meta.Resolution} m/px입니다. 같은 해상도로 저장한 맵만 맞출 수 있습니다.");
            return;
        }

        CloseSecond();
        _secondView = SecondView.Overlay;
        SecondShowOverlay.IsChecked = true;   // 맞출 맵 지정 전이라 핸들러는 레이어를 그리지 않음
        _second = m;
        _secondHull = MapRegistration.KnownHull(m);
        IntRect hb = MapRegistration.BoundsOf(_secondHull);
        _secondHullCenter = new PointD(hb.X + hb.Width / 2.0, hb.Y + hb.Height / 2.0);
        _secondPath = path;
        _secondMeta = meta?.SourcePath != null ? meta : null;

        // 초기 위치: 두 yaml origin으로 겹치면 그 위치, 아니면 화면 중앙
        string note;
        MapPose? byMeta = YamlPose();
        if (byMeta is MapPose p && !SecondBounds(p).Intersect(_map.Bounds).IsEmpty)
        {
            _secondPose = p;
            note = "yaml origin 기준으로 배치";
        }
        else
        {
            _secondPose = CenterPose(MapPose.Identity);
            note = byMeta != null ? "yaml 위치가 현재 맵과 겹치지 않아 화면 중앙에 배치" : "화면 중앙에 배치";
        }

        SegSecond.IsChecked = true;
        RefreshSecondUi();
        RefreshSecondLayer();
        ApplySecondPose();
        ShowBoth();
        SelectTool(EditTool.SecondMove);

        string big = (long)m.Width * m.Height > 2L * _map.Width * _map.Height
            ? "  · 맞출 맵이 더 큽니다. 기존 맵 갱신이 목적이면 큰 맵을 현재 맵으로 여세요."
            : "";
        SetStatus($"맞출 맵: {Path.GetFileName(path)} · {note}. 끌어서 대략 겹친 뒤 자동 정렬하세요.{big}");
    }

    private void OnCloseSecond(object sender, RoutedEventArgs e)
    {
        if (SecondBusyBlocked()) return;
        string? name = _secondPath != null ? Path.GetFileName(_secondPath) : null;
        CloseSecond();
        if (name != null) SetStatus($"맞출 맵 닫음: {name}");
    }

    private void CloseSecond()
    {
        if (_secondBusy) _busyCts?.Cancel();   // 닫히는 맵의 자동 정렬은 결과를 쓸 곳이 없음
        CancelPair();
        _secondDragging = false;
        _secondDiffTimer?.Stop();
        _second = null;
        _secondPath = null;
        _secondMeta = null;
        _secondDiff = null;
        _secondAlign = null;
        _secondHistory.Clear();
        _secondKeyBurst = false;
        _secondRegions.Clear();
        SetSecondAlignText("");
        SetPairText("");
        MapViewer.SetSecondLayer(null);
        MapViewer.SetSecondPose(null, null);
        if (_tool == EditTool.SecondMove) SelectTool(EditTool.Brush);
        RefreshSecondUi();
        RefreshStatusChips();
    }

    /// <summary>현재 맵과 맞출 맵이 모두 보이게</summary>
    private void ShowBoth()
    {
        if (_map == null || _second == null) return;
        MapViewer.CenterOn(MapRegistration.MergedBounds(_map, _secondHull, _secondPose), 0);
    }

    // ───────────── 표시 ─────────────

    private void RefreshSecondUi()
    {
        bool has = _second != null;
        SecondEmptyPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        SecondPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ToolSecond.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SecondUndoPoseButton.IsEnabled = _secondHistory.Count > 0;
        SecondYamlButton.IsEnabled = has && _secondMeta != null && _meta.SourcePath != null;
        SecondDiffList.Visibility = _secondRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_secondRegions.Count > 0 || !has) SecondDiffEmpty.Visibility = Visibility.Collapsed;
        UpdateSecondStats();
        RefreshStatusChips();
        if (_second == null)
        {
            SecondFileText.Text = "";
            SecondFileText.ToolTip = null;
            SecondMetaText.Text = "";
            return;
        }
        SecondFileText.Text = Path.GetFileName(_secondPath);
        SecondFileText.ToolTip = _secondPath;
        SecondMetaText.Text = $"{_second.Width} × {_second.Height} px · " +
                              (_secondMeta != null ? $"origin ({_secondMeta.OriginX:0.###}, {_secondMeta.OriginY:0.###})" : "yaml 없음");
    }

    /// <summary>맞출 맵 색 레이어 다시 그리기 (변경점 표시면 비교도 다시)</summary>
    private void RefreshSecondLayer()
    {
        if (_second == null || _map == null || _secondView == SecondView.Hidden)
        {
            MapViewer.SetSecondLayer(null);
            return;
        }
        if (_secondView == SecondView.Diff && _secondDiff == null) ComputeSecondDiff();

        int w = _second.Width, h = _second.Height;
        var buf = new uint[w * h];
        MapRegistration.FillLayer(_second, _occThreshold, _secondView == SecondView.Diff ? _secondDiff : null, buf);
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
        bmp.Freeze();
        MapViewer.SetSecondLayer(bmp);
    }

    /// <summary>자세를 화면 레이어 · 외곽선 · 표시 문구에 반영</summary>
    private void ApplySecondPose()
    {
        if (_second == null || _map == null)
        {
            MapViewer.SetSecondPose(null, null);
            return;
        }
        MapPose p = _secondPose;
        double r = p.AngleDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        PointD[] outline = MapRegistration.Transform(_secondHull, p);
        MapViewer.SetSecondPose(new Matrix(c, s, -s, c, p.Tx, p.Ty), outline);

        SecondPoseText.Text = $"회전 {p.AngleDeg:0.00}° · 위치 ({p.Tx:0.#}, {p.Ty:0.#}) px";

        IntRect fp = MapRegistration.BoundsOf(outline);
        IntRect mb = MapRegistration.MergedBounds(_map, _secondHull, p);
        int w = mb.Width, h = mb.Height;
        long inside = fp.Intersect(_map.Bounds).Area;
        double outside = fp.Area > 0 ? 1 - (double)inside / fp.Area : 0;
        MergeSizeText.Text = w == _map.Width && h == _map.Height
            ? $"결과 크기 {w} × {h} px (현재 맵 크기 그대로)"
            : $"결과 크기 {w} × {h} px (현재 {_map.Width} × {_map.Height})";
        SecondOutsideText.Text = outside > 0.02
            ? $"맞출 맵의 약 {outside:P0}가 현재 맵 밖에 있어 반영되지 않습니다. 그 부분까지 쓰려면 합치기를 쓰세요."
            : "";
        SecondOutsideText.Visibility = outside > 0.02 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSecondViewChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out SecondView view))
        {
            _secondView = view;
            RefreshSecondLayer();
        }
    }

    private void OnSecondOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        ApplySecondOpacity();
    }

    /// <summary>2점 맞추기에서 현재 맵을 찍는 단계는 레이어를 흐리게</summary>
    private void ApplySecondOpacity() =>
        MapViewer.SecondOpacity = SecondOpacitySlider.Value * (_pairStep is 2 or 4 ? 0.3 : 1);

    // ───────────── 자세 변경 ─────────────

    private void SetSecondPose(MapPose pose, bool fromKey = false, bool keepAlign = false)
    {
        if (_second == null || pose == _secondPose) return;
        if (!fromKey || !_secondKeyBurst) PushSecondHistory(_secondPose);
        _secondKeyBurst = fromKey;
        _secondPose = pose;
        if (!keepAlign) ClearAlignResult();
        ApplySecondPose();
        InvalidateSecondDiff(true);
    }

    private void PushSecondHistory(MapPose prev)
    {
        _secondHistory.Add(prev);
        if (_secondHistory.Count > 200) _secondHistory.RemoveAt(0);
        SecondUndoPoseButton.IsEnabled = true;
    }

    private void OnSecondUndoPose(object sender, RoutedEventArgs e)
    {
        if (_second == null || _secondHistory.Count == 0) return;
        CancelPair();
        _secondPose = _secondHistory[^1];
        _secondHistory.RemoveAt(_secondHistory.Count - 1);
        _secondKeyBurst = false;
        ClearAlignResult();
        ApplySecondPose();
        InvalidateSecondDiff(true);
        SecondUndoPoseButton.IsEnabled = _secondHistory.Count > 0;
        SetStatus("맞출 맵 위치를 되돌렸습니다.");
    }

    /// <summary>맞출 맵(알려진 부분) 중심 (현재 맵 좌표)</summary>
    private PointD SecondCenter() => _secondPose.ToBase(_secondHullCenter);

    /// <summary>맞출 맵(알려진 부분)이 차지하는 현재 맵 좌표 범위</summary>
    private IntRect SecondBounds(MapPose pose) => MapRegistration.BoundsOf(MapRegistration.Transform(_secondHull, pose));

    /// <summary>회전 중심: 맞출 맵 중 화면에 보이는 부분의 가운데 (보던 곳이 제자리에 남도록)</summary>
    private PointD SecondPivot()
    {
        IntRect fp = SecondBounds(_secondPose);
        Point a = MapViewer.ScreenToImage(new Point(0, 0));
        Point b = MapViewer.ScreenToImage(new Point(MapViewer.ActualWidth, MapViewer.ActualHeight));
        double x0 = Math.Max(fp.X, a.X), y0 = Math.Max(fp.Y, a.Y), x1 = Math.Min(fp.Right, b.X), y1 = Math.Min(fp.Bottom, b.Y);
        return x1 > x0 && y1 > y0 ? new PointD((x0 + x1) / 2, (y0 + y1) / 2) : SecondCenter();
    }

    /// <summary>각도는 유지하고 맞출 맵(알려진 부분) 중심을 화면 중앙으로</summary>
    private MapPose CenterPose(MapPose pose)
    {
        PointD v = MapViewer.ViewCenterImage;
        PointD c = pose.ToBase(_secondHullCenter);
        return pose.Translate(Math.Round(v.X - c.X), Math.Round(v.Y - c.Y));
    }

    /// <summary>두 yaml origin 기준 자세 (둘 다 yaml이 있을 때만)</summary>
    private MapPose? YamlPose() =>
        _map != null && _second != null && _secondMeta != null && _meta.SourcePath != null
            ? MapRegistration.FromMeta(_meta, _map.Height, _secondMeta, _second.Height)
            : null;

    private void OnSecondRotateLeft(object sender, RoutedEventArgs e) => RotateSecond(-90);

    private void OnSecondRotateRight(object sender, RoutedEventArgs e) => RotateSecond(90);

    private void RotateSecond(double deg)
    {
        if (_second == null) return;
        CancelPair();
        SetSecondPose(_secondPose.RotateAround(SecondPivot(), deg));
        SetStatus($"맞출 맵 {(deg < 0 ? "왼쪽" : "오른쪽")}으로 {Math.Abs(deg):0}° 회전");
    }

    private void OnSecondAxisSnap(object sender, RoutedEventArgs e)
    {
        if (_map == null || _second == null) return;
        CancelPair();
        double? d;
        using (new WaitCursor()) d = MapRegistration.AxisSnapDelta(_map, _second, _occThreshold, _secondPose.AngleDeg);
        if (d is not double delta)
        {
            SetStatus("장애물이 부족해 벽 방향을 계산할 수 없습니다.");
            return;
        }
        if (Math.Abs(delta) < 0.01)
        {
            SetStatus("두 맵의 벽 방향이 이미 평행합니다. 90° 단위로 다르면 회전 버튼을 쓰세요.");
            return;
        }
        SetSecondPose(_secondPose.RotateAround(SecondPivot(), delta));
        SetStatus($"벽 방향 맞춤: {delta:+0.00;-0.00}° 회전. 방향이 90° 단위로 다르면 회전 버튼으로 돌리세요.");
    }

    private void OnSecondCenter(object sender, RoutedEventArgs e)
    {
        if (_second == null) return;
        CancelPair();
        SetSecondPose(CenterPose(_secondPose));
        SetStatus("맞출 맵을 화면 중앙으로 옮겼습니다.");
    }

    private void OnSecondYaml(object sender, RoutedEventArgs e)
    {
        if (_second == null) return;
        if (YamlPose() is not MapPose p)
        {
            SetStatus("두 맵 모두 yaml이 있어야 합니다.");
            return;
        }
        CancelPair();
        SetSecondPose(p);
        ShowBoth();
        SetStatus("yaml origin 기준 위치로 옮겼습니다. 같은 좌표계로 그린 맵일 때만 맞습니다.");
    }

    // ───────────── 끌기 · 키보드 ─────────────

    /// <summary>맞출 맵 이동 도구: 드래그 = 이동, Shift+드래그 = 회전 (Ctrl을 같이 누르면 1° 단위)</summary>
    private void BeginSecondDrag(MapMouseEventArgs e)
    {
        if (_second == null)
        {
            SetStatus("맞추기 탭에서 맞출 맵을 먼저 여세요.");
            return;
        }
        if (SecondBusyBlocked()) return;
        _secondDragging = true;
        _secondRotating = (e.Modifiers & ModifierKeys.Shift) != 0;
        _secondDragStart = new PointD(e.ImageX, e.ImageY);
        _secondDragPose = _secondPose;
        _secondPivot = SecondPivot();
    }

    private void UpdateSecondDrag(MapMouseEventArgs e)
    {
        if (_second == null) return;
        if (_secondRotating)
        {
            double a0 = Math.Atan2(_secondDragStart.Y - _secondPivot.Y, _secondDragStart.X - _secondPivot.X);
            double a1 = Math.Atan2(e.ImageY - _secondPivot.Y, e.ImageX - _secondPivot.X);
            double deg = MapPose.NormalizeDeg((a1 - a0) * 180 / Math.PI);
            if ((e.Modifiers & ModifierKeys.Control) != 0) deg = Math.Round(deg);
            _secondPose = _secondDragPose.RotateAround(_secondPivot, deg);
            SetStatus($"회전 {deg:+0.00;-0.00;0}° (맞출 맵 {_secondPose.AngleDeg:0.00}°)");
        }
        else
        {
            double dx = Math.Round(e.ImageX - _secondDragStart.X), dy = Math.Round(e.ImageY - _secondDragStart.Y);
            _secondPose = _secondDragPose.Translate(dx, dy);
            SetStatus($"이동 ({dx:+0;-0;0}, {dy:+0;-0;0}) px ≈ ({dx * _meta.Resolution:0.00}, {-dy * _meta.Resolution:0.00}) m");
        }
        ApplySecondPose();
    }

    private void EndSecondDrag()
    {
        if (!_secondDragging) return;
        _secondDragging = false;
        if (_secondPose == _secondDragPose) return;
        PushSecondHistory(_secondDragPose);
        _secondKeyBurst = false;
        ClearAlignResult();
        InvalidateSecondDiff(true);
    }

    /// <summary>방향키 1px (Shift 10px), 쉼표 · 마침표 0.1° (Shift 1°). 처리했으면 true</summary>
    private bool HandleSecondKey(Key key, bool shift)
    {
        if (_second == null) return false;
        if (_secondBusy && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.OemComma or Key.OemPeriod)
        {
            SecondBusyBlocked();
            return true;
        }
        double step = shift ? 10 : 1, rot = shift ? 1 : 0.1;
        MapPose p = _secondPose;
        switch (key)
        {
            case Key.Left: p = p.Translate(-step, 0); break;
            case Key.Right: p = p.Translate(step, 0); break;
            case Key.Up: p = p.Translate(0, -step); break;
            case Key.Down: p = p.Translate(0, step); break;
            case Key.OemComma: p = p.RotateAround(SecondPivot(), -rot); break;
            case Key.OemPeriod: p = p.RotateAround(SecondPivot(), rot); break;
            default: return false;
        }
        CancelPair();
        SetSecondPose(p, fromKey: true);
        return true;
    }

    // ───────────── 2점 맞추기 ─────────────

    private void OnSecondPairStart(object sender, RoutedEventArgs e)
    {
        if (_second == null) return;
        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        _pairStep = 1;
        _pairMarks.Clear();
        RefreshMarkers();
        UpdatePairPrompt();
    }

    private void UpdatePairPrompt()
    {
        string msg = _pairStep switch
        {
            1 => "① 맞출 맵(자홍)에서 점 A 클릭 · 벽 모서리처럼 뚜렷한 곳",
            2 => "② 현재 맵에서 A와 같은 위치 클릭",
            3 => "③ 맞출 맵에서 점 B 클릭 (A와 멀리 떨어진 곳)",
            4 => "④ 현재 맵에서 B와 같은 위치 클릭",
            _ => "",
        };
        SetPairText(_pairStep > 0 ? msg + "  · Esc 취소" : "");
        if (_pairStep > 0) SetStatus(msg);
        ApplySecondOpacity();
    }

    private void HandlePairClick(int x, int y)
    {
        if (_second == null)
        {
            CancelPair();
            return;
        }
        var q = new PointD(x + 0.5, y + 0.5);
        switch (_pairStep)
        {
            case 1:
                _pairM1 = _secondPose.ToMoving(q);
                break;
            case 2:
                _pairB1 = q;
                break;
            case 3:
                _pairM2 = _secondPose.ToMoving(q);
                break;
            case 4:
                FinishPair(q);
                return;
        }
        _pairMarks.Add(q);
        _pairStep++;
        RefreshMarkers();
        UpdatePairPrompt();
    }

    private void FinishPair(PointD b2)
    {
        PointD m1 = _pairM1, m2 = _pairM2, b1 = _pairB1;
        CancelPair(false);
        if (Dist(m1, m2) < 5 || Dist(b1, b2) < 5)
        {
            SetPairText("두 점이 너무 가깝습니다. 멀리 떨어진 두 점으로 다시 하세요.");
            return;
        }
        MapPose p = MapRegistration.FromTwoPoints(m1, m2, b1, b2, out double ratio);
        SetSecondPose(p);
        string msg = $"2점 맞춤 완료 · 회전 {p.AngleDeg:0.00}°";
        if (Math.Abs(ratio - 1) > 0.03)
            msg += $" · ⚠ 두 점 거리 비 {ratio:0.00} (같은 지점을 찍었는지 확인)";
        msg += " · 자동 정렬로 마무리하세요.";
        SetPairText(msg);
        SetStatus(msg);
    }

    private void CancelPair(bool resetText = true)
    {
        if (_pairStep == 0 && _pairMarks.Count == 0) return;
        _pairStep = 0;
        _pairMarks.Clear();
        RefreshMarkers();
        ApplySecondOpacity();
        if (resetText) SetPairText("");
    }

    private void SetPairText(string text)
    {
        SecondPairText.Text = text;
        SecondPairText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────── 자동 정렬 ─────────────

    /// <summary>
    /// 자동 정렬은 큰 맵에서 수 초 걸리므로 백그라운드로 계산하고 단계 · 진행률 · 취소를 보여줌.
    /// 현재 맵은 복사본으로 계산하므로 그동안 화면 이동 · 편집은 계속 가능
    /// </summary>
    private async void OnSecondAutoAlign(object sender, RoutedEventArgs e)
    {
        if (_map == null || _second == null) return;
        if (_busyCts != null)
        {
            SetStatus("진행 중인 작업이 끝난 뒤 다시 시도하세요.");
            return;
        }
        if (!ReadInt(SecondRadiusBox, 2, 300, "이동 탐색 범위", out int radius)) return;
        if (!ReadDouble(SecondAngleBox, 0, 30, "회전 탐색 범위", out double angle)) return;
        CancelPair();
        ClearAlignResult();

        MapImage second = _second, snapshot = _map.Clone();
        MapPose start = _secondPose;
        byte thr = _occThreshold;
        CancellationToken ct = BeginBusy("자동 정렬 중", lockEditing: false, cancellable: true, showProgress: true);
        SetSecondBusy(true);
        var progress = new Progress<AlignProgress>(p =>
        {
            if (!_secondBusy) return;
            SecondProgressBar.Value = p.Percent;
            SecondProgressPercent.Text = $"{p.Percent:0} %";
            SecondProgressText.Text = p.Stage switch
            {
                1 => "1/3 벽 점 추출",
                2 => "2/3 대략 위치 찾기",
                _ => "3/3 세밀하게 맞추기",
            };
            ReportBusy(p.Percent);
        });

        AlignResult? r;
        try
        {
            r = await Task.Run(() => MapRegistration.Refine(snapshot, second, thr, start, radius, angle, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            SetStatus("자동 정렬을 취소했습니다. 맞출 맵 위치는 그대로입니다.");
            return;
        }
        catch (Exception ex)
        {
            ShowFailure("자동 정렬 중 문제가 생겼습니다", null, ex, canRetry: false);
            return;
        }
        finally
        {
            SetSecondBusy(false);
            EndBusy();
        }
        if (_second != second || _map == null) return;   // 계산 중 현재 맵을 닫거나 다른 맵을 엶

        if (r == null)
        {
            ClearAlignResult();
            SetSecondAlignText("겹치는 부분의 벽이 부족합니다. 대략 더 겹쳐 놓거나 탐색 범위를 넓혀 보세요.");
            SetStatus("자동 정렬 실패: 겹치는 벽 부족");
            return;
        }

        SetSecondPose(r.Pose, keepAlign: true);
        _secondAlign = r;
        _secondAltIndex = 0;
        ShowAlignResult();
        SetStatus($"자동 정렬 완료 · 벽 일치 {r.MatchBefore:P0} → {r.MatchAfter:P0}");
    }

    private void OnSecondNextCandidate(object sender, RoutedEventArgs e)
    {
        if (_secondAlign is not AlignResult r || r.Alternatives.Count == 0) return;
        int n = 1 + r.Alternatives.Count;
        _secondAltIndex = (_secondAltIndex + 1) % n;
        SetSecondPose(_secondAltIndex == 0 ? r.Pose : r.Alternatives[_secondAltIndex - 1], keepAlign: true);
        ShowAlignResult();
        SetStatus($"정렬 후보 {_secondAltIndex + 1}/{n}");
    }

    private void ShowAlignResult()
    {
        if (_secondAlign is not AlignResult r || _second == null) return;
        PointD center = _secondHullCenter;
        double move = Dist(r.Start.ToBase(center), _secondPose.ToBase(center));
        double turn = MapPose.NormalizeDeg(_secondPose.AngleDeg - r.Start.AngleDeg);
        int n = 1 + r.Alternatives.Count;

        var sb = new StringBuilder();
        sb.Append($"보정 이동 {move:0.0} px, 회전 {turn:+0.00;-0.00;0}°");
        if (_secondAltIndex == 0)
        {
            sb.Append($" · 벽 일치 {r.MatchBefore:P0} → {r.MatchAfter:P0}\n");
            sb.Append(r.MatchAfter >= 0.8 ? "잘 맞습니다."
                : r.MatchAfter >= 0.5 ? "부분적으로 맞습니다. 바뀐 곳이 많거나 위치가 다를 수 있으니 확인하세요."
                : "신뢰도가 낮습니다. 대략 위치를 다시 잡고 실행하세요.");
        }
        else
        {
            sb.Append($"\n후보 {_secondAltIndex + 1}/{n} 표시 중 (1번이 가장 잘 맞는 위치)");
        }
        if (n > 1 && _secondAltIndex == 0)
            sb.Append($"\n점수가 비슷한 위치가 {n}곳 있습니다 (랙 열처럼 반복되는 구조). 다른 후보도 확인하세요.");
        SetSecondAlignText(sb.ToString());
        SecondAlignText.ToolTip = $"겹친 벽 점 {r.OverlapPoints:N0} / {r.Points:N0} ({r.OverlapRatio:P0})";

        SecondAltButton.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
        SecondAltText.Text = $"다른 후보 ({_secondAltIndex + 1}/{n})";
    }

    private void ClearAlignResult()
    {
        if (_secondAlign == null && SecondAlignText.Visibility != Visibility.Visible) return;
        _secondAlign = null;
        _secondAltIndex = 0;
        SetSecondAlignText("");
        SecondAltButton.Visibility = Visibility.Collapsed;
    }

    private void SetSecondAlignText(string text)
    {
        SecondAlignText.Text = text;
        SecondAlignText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length == 0) SecondAlignText.ToolTip = null;
    }

    /// <summary>자동 정렬 중: 정렬 버튼 대신 진행 표시, 위치를 바꾸는 카드는 비활성</summary>
    private void SetSecondBusy(bool busy)
    {
        _secondBusy = busy;
        SecondFileCard.IsEnabled = SecondCard1.IsEnabled = SecondCard3.IsEnabled = SecondCard4.IsEnabled = !busy;
        SecondAlignButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        SecondProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SecondProgressBar.Value = 0;
        SecondProgressPercent.Text = "0 %";
        SecondProgressText.Text = "준비";
    }

    private bool SecondBusyBlocked()
    {
        if (!_secondBusy) return false;
        SetStatus("자동 정렬 중입니다. 끝난 뒤 다시 시도하세요.  Esc = 취소");
        return true;
    }

    // ───────────── 비교 ─────────────

    private void ComputeSecondDiff()
    {
        if (_map == null || _second == null) return;
        using (new WaitCursor()) _secondDiff = MapRegistration.Compare(_map, _second, _secondPose, _occThreshold);
        UpdateSecondStats();
    }

    /// <summary>자세 · 현재 맵 · 임계값이 바뀌면 비교 결과를 버리고 변경점 표시 중이면 잠시 뒤 다시 계산</summary>
    private void InvalidateSecondDiff(bool poseChanged)
    {
        if (_second == null) return;
        if (poseChanged)
        {
            _secondRegions.Clear();
            SecondDiffList.Visibility = Visibility.Collapsed;
            SecondDiffEmpty.Visibility = Visibility.Collapsed;
        }
        if (_secondDiff == null && _secondView != SecondView.Diff) return;
        _secondDiff = null;
        UpdateSecondStats();
        if (_secondView != SecondView.Diff) return;

        if (_secondDiffTimer == null)
        {
            _secondDiffTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _secondDiffTimer.Tick += (_, _) =>
            {
                _secondDiffTimer!.Stop();
                if (_second == null || _secondDragging || _secondView != SecondView.Diff) return;
                RefreshSecondLayer();
            };
        }
        _secondDiffTimer.Stop();
        _secondDiffTimer.Start();
    }

    private void UpdateSecondStats()
    {
        if (_secondDiff is not AlignDiff d)
        {
            SecondStatsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SecondStatsPanel.Visibility = Visibility.Visible;
        SecondAddedText.Text = $"{d.AddedCount:N0} px";
        SecondRemovedText.Text = $"{d.RemovedCount:N0} px";
        SecondNewAreaText.Text = $"{d.NewAreaCount:N0} px";
    }

    private void OnSecondFindDiff(object sender, RoutedEventArgs e)
    {
        if (_map == null || _second == null) return;
        if (_secondDiff == null) ComputeSecondDiff();
        if (_secondDiff is not AlignDiff diff) return;

        List<(IntRect Bounds, int Area, bool Added)> regions;
        using (new WaitCursor()) regions = MapRegistration.DiffRegions(diff, _second, _secondPose, 4);
        _secondRegions.Clear();
        int index = 1;
        foreach ((IntRect b, int area, bool added) in regions.Take(MaxSecondRegions))
            _secondRegions.Add(new RegionItem(index++, b, $"{(added ? "추가" : "삭제")} · {area:N0} px · {b.Width}×{b.Height}"));
        SecondDiffList.Visibility = _secondRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (regions.Count == 0)
            ShowEmpty(SecondDiffEmpty, SecondDiffEmptyText,
                diff.NewAreaCount > 0
                    ? $"겹친 곳의 벽은 모두 같습니다. 현재 맵에 없던 곳 {diff.NewAreaCount:N0} px는 반영 · 합치기로 가져올 수 있습니다."
                    : "겹친 곳의 벽이 모두 같습니다.");
        else
            SecondDiffEmpty.Visibility = Visibility.Collapsed;

        if (_secondView != SecondView.Diff) SecondShowDiff.IsChecked = true;   // 핸들러에서 레이어 갱신
        int addedCount = regions.Count(x => x.Added);
        SetStatus(regions.Count == 0
            ? "추가 · 삭제된 곳 없음"
            : $"추가 {addedCount:N0}곳 · 삭제 {regions.Count - addedCount:N0}곳" +
              (regions.Count > MaxSecondRegions ? $" (면적 큰 순 {MaxSecondRegions}개 표시)" : ""));
    }

    private void OnSecondDiffSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SecondDiffList.SelectedItem is RegionItem item) FocusOn(item.Bounds);
    }

    // ───────────── 현재 맵에 반영 ─────────────

    /// <summary>
    /// 맞출 맵이 알고 있는 곳(Unknown 제외)을 현재 맵에 덮어씀. 선택 영역이 있으면 그 안만.
    /// 반영 전 맵을 기준 맵으로 두고 반영 범위를 업데이트 영역으로 지정 → 업데이트 탭 단계로 이어짐
    /// </summary>
    private void OnSecondApply(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null || _second == null) return;
        PixelRegion? region = _selection;
        PixelRegion? area = region ?? PixelRegion.FromPolygon(MapRegistration.Transform(_secondHull, _secondPose), _map.Bounds);
        if (area == null)
        {
            SetStatus("맞출 맵이 현재 맵 범위 밖에 있습니다. 현재 맵을 넓히려면 합치기를 쓰세요.");
            return;
        }

        if (SecondBusyBlocked()) return;

        // 확인창 없이 실행: Ctrl+Z 한 번으로 픽셀 · 기준 맵 · 업데이트 영역까지 함께 되돌림
        CancelDrag();
        CancelPolygon();
        CancelPair();

        MapImage? newReference = null;
        string? newReferenceName = null;
        if (_reference == null)
        {
            newReference = _map.Clone();
            newReferenceName = $"반영 전 · {Path.GetFileName(_path)}";
            SetReference(newReference, newReferenceName, "맞출 맵 반영 전 상태");
        }

        int n;
        _lastCommit = null;
        BeginEdit($"맞출 맵 반영 ({Path.GetFileName(_secondPath)})", useClip: false);
        using (new WaitCursor())
        {
            n = MapRegistration.ApplyInto(_tracker, _second, _secondPose, region);
            CommitEdit();
        }
        if (_lastCommit == null)
        {
            // 바뀐 픽셀이 없음: 방금 만든 기준 맵도 정리
            if (newReference != null) CloseReference();
            SetStatus("맞출 맵이 현재 맵과 같아 반영할 내용이 없습니다." + BlockedNote());
            return;
        }
        _applyUndo = new ApplyUndo(_lastCommit, newReference, newReferenceName, area);
        if (region != null) SetSelection(null);
        _updateAreas.Add(area);
        UpdateAreasChanged();

        // 업데이트 탭 변경점이 보이도록 레이어는 숨김
        SecondShowHidden.IsChecked = true;
        SegUpdate.IsChecked = true;
        string scope = region != null ? "선택 영역" : "맞출 맵 전체";
        SetStatus($"맞출 맵 반영 · {scope} {n:N0} px" +
                  (newReference != null ? " · 반영 전 맵을 기준 맵으로 열고 반영 범위를 업데이트 영역으로 지정" : " · 반영 범위를 업데이트 영역에 추가") +
                  BlockedNote(), undo: true);
    }

    // ───────────── 합치기 ─────────────

    private MergeRule SelectedMergeRule() =>
        MergeMovingFirst.IsChecked == true ? MergeRule.MovingFirst
        : MergeUnion.IsChecked == true ? MergeRule.ObstacleUnion
        : MergeRegion.IsChecked == true ? MergeRule.RegionMovingFirst
        : MergeRule.BaseFirst;

    private static string MergeRuleName(MergeRule rule) => rule switch
    {
        MergeRule.MovingFirst => "새 맵 우선",
        MergeRule.ObstacleUnion => "장애물 합침",
        MergeRule.RegionMovingFirst => "선택 영역만 새 맵 우선",
        _ => "현재 맵 우선",
    };

    private void OnMergeRuleChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        MergeRuleText.Text = SelectedMergeRule() switch
        {
            MergeRule.MovingFirst => "겹친 곳은 맞출 맵 값으로. 맞출 맵이 더 최신일 때.",
            MergeRule.ObstacleUnion => "어느 한쪽이라도 장애물이면 장애물. 나머지는 현재 맵 우선. 겹친 곳이 조금 어긋나면 이중 벽이 생길 수 있습니다.",
            MergeRule.RegionMovingFirst => "선택 영역(M · P) 안은 맞출 맵, 밖은 현재 맵 우선. 겹친 경계를 직접 정할 때.",
            _ => "현재 맵이 Unknown인 곳만 맞출 맵으로 채웁니다. 겹친 곳에 이중 벽이 생기지 않습니다.",
        };
    }

    private void OnSecondMerge(object sender, RoutedEventArgs e)
    {
        if (_map == null || _second == null || SecondBusyBlocked()) return;
        MergeRule rule = SelectedMergeRule();
        if (rule == MergeRule.RegionMovingFirst && _selection == null)
        {
            SetStatus("맞출 맵을 우선할 범위를 사각형(M)이나 폴리곤(P)으로 먼저 선택하세요.");
            return;
        }

        IntRect mb = MapRegistration.MergedBounds(_map, _secondHull, _secondPose);
        int nw = mb.Width, nh = mb.Height;
        if ((long)nw * nh > 400_000_000L)
        {
            ShowError("결과 맵이 너무 큽니다",
                $"합치면 {nw} × {nh} px가 됩니다. 맞출 맵이 엉뚱한 곳에 놓였는지 위치를 확인하세요.");
            return;
        }

        // 실행 취소 이력이 초기화되므로 확인
        string yaml = _meta.SourcePath != null
            ? "새 크기에 맞게 갱신 · 기존 영역의 월드 좌표는 그대로"
            : "yaml이 없어 새 origin은 직접 반영해야 합니다";
        if (!Confirm("두 맵을 합칠까요?",
                     "실행 취소할 수 없고, 열린 기준 맵과 맞출 맵은 닫힙니다. 원본을 남기려면 저장할 때 다른 이름으로 저장(Ctrl+Shift+S)하세요.",
                     "합치기",
                     new[]
                     {
                         ("크기", $"{_map.Width} × {_map.Height} → {nw} × {nh}"),
                         ("겹친 곳", MergeRuleName(rule)),
                         ("yaml origin", yaml),
                         ("같이 옮김", "보호 영역 · 도면 배치"),
                     }))
            return;

        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        CancelPair();

        MergeResult res;
        using (new WaitCursor())
            res = MapRegistration.Merge(_map, _second, _secondPose, rule, _occThreshold,
                rule == MergeRule.RegionMovingFirst ? _selection : null);
        (double ox, double oy) = MapRegistration.MergedOrigin(_meta, _map.Height, res);
        int dx = res.OffsetX, dy = res.OffsetY;

        // 보호 영역 · 도면 배치 이동 (현재 맵 픽셀 (0, 0) → (dx, dy))
        var moved = new List<NamedRegion>();
        foreach (NamedRegion p in _protect)
        {
            PixelRegion? r = ShiftRegion(p.Region, dx, dy, res.Image.Bounds);
            if (r != null) moved.Add(new NamedRegion(p.Name, r));
        }
        _protect.Clear();
        foreach (NamedRegion p in moved) _protect.Add(p);
        if (_dxfPlacement != null)
        {
            _dxfPlacement.OffsetX += dx;
            _dxfPlacement.OffsetY += dy;
            UpdateDxfPlacementUi();
        }

        string secondName = Path.GetFileName(_secondPath) ?? "";
        int oldW = _map.Width, oldH = _map.Height;
        CloseReference();
        CloseSecond();

        _map = res.Image;
        _meta.OriginX = ox;
        _meta.OriginY = oy;
        MarkMetaChanged("맵 합치기");
        _tracker = new EditTracker(_map);
        _undo.Clear();
        _opLog.Add($"{DateTime.Now:HH:mm:ss} 맵 합치기: {secondName} ({oldW}×{oldH} → {_map.Width}×{_map.Height}, " +
                   $"{MergeRuleName(rule)}, 맞출 맵에서 {res.FromMoving:N0} px)");
        _axis = null;
        UpdateAxisText();

        _selection = null;
        ResetCandidates();
        _diffRegions.Clear();
        _updateAreas.Clear();
        _focusMarker = null;

        MapViewer.CreateImage(_map.Width, _map.Height);
        RedrawBase(Full);
        ApplyProtect();
        UpdateAreasChanged();
        RebuildDxfGeometry();
        RefreshMarkers();
        MapViewer.FitToView();
        SetDirty(true);
        UpdateInfo();
        UpdateSelectionUi();
        UpdateUndoButtons();
        UpdateDiffStats();
        SetStatus($"합치기 완료: {_map.Width} × {_map.Height} px (맞출 맵에서 {res.FromMoving:N0} px) · " +
                  "원본을 남기려면 다른 이름으로 저장(Ctrl+Shift+S)하세요.");
    }

    /// <summary>영역을 정수 픽셀만큼 옮김 (사각형은 사각형 그대로)</summary>
    private static PixelRegion? ShiftRegion(PixelRegion r, int dx, int dy, IntRect bounds)
    {
        if (r.IsRect)
        {
            IntRect b = new IntRect(r.Bounds.X + dx, r.Bounds.Y + dy, r.Bounds.Width, r.Bounds.Height).Intersect(bounds);
            return b.IsEmpty ? null : PixelRegion.FromRect(b);
        }
        return PixelRegion.FromPolygon(r.Outline.Select(p => new PointD(p.X + dx, p.Y + dy)).ToList(), bounds);
    }
}
