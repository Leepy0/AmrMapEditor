using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;
using AmrMapEditor.Models;

namespace AmrMapEditor;

/// <summary>맵 정리: 노이즈, 벽 직선화, 기둥, 벽 끊김, 확률값, 외곽, 기울기 보정</summary>
public partial class MainWindow
{
    private const int BlobPickWarnArea = 500;      // 객체 삭제 시 확인 (벽 오삭제 방지)
    private const int PillarWarnArea = 20000;      // 기둥 정리 시 확인

    private double? _axis;   // 맵 주축 각도 (캐시)

    // ───────────── 공통 ─────────────

    private void OnUnitParamChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateUnitLabels();
    }

    /// <summary>px 입력 옆에 m 환산값 표시</summary>
    private void UpdateUnitLabels()
    {
        double res = _meta.Resolution;
        NoiseAreaUnitText.Text = int.TryParse(NoiseMaxAreaBox.Text, out int a) && a > 0 ? $"px ≈ {a * res * res:0.####} m²" : "px";
        NoiseSideUnitText.Text = int.TryParse(NoiseMaxSideBox.Text, out int s) && s > 0 ? $"px ≈ {s * res:0.###} m" : "px";
        DupDistUnitText.Text = double.TryParse(DupDistBox.Text, out double d) && d > 0 ? $"px ≈ {d * res:0.###} m" : "px";
        GapMaxUnitText.Text = int.TryParse(GapMaxBox.Text, out int g) && g > 0 ? $"px ≈ {g * res:0.###} m" : "px";
    }

    private BlobItem Track(BlobItem item)
    {
        item.PropertyChanged += (_, _) =>
        {
            if (!_bulk) RefreshMarkers();
        };
        return item;
    }

    /// <summary>모두 체크돼 있으면 모두 해제, 아니면 모두 체크</summary>
    private void ToggleAll(ICollection<BlobItem> items) => SetAllChecked(items, !items.All(i => i.IsChecked));

    private void SetAllChecked(IEnumerable<BlobItem> items, bool value)
    {
        _bulk = true;
        foreach (BlobItem c in items) c.IsChecked = value;
        _bulk = false;
        RefreshMarkers();
    }

    private void RemoveItems(ObservableCollection<BlobItem> list, List<BlobItem> targets)
    {
        _bulk = true;
        foreach (BlobItem t in targets) list.Remove(t);
        _bulk = false;
        _focusMarker = null;
        RefreshMarkers();
        RefreshUpdateGuide();
    }

    private void FocusOn(IntRect r)
    {
        _focusMarker = new MapMarker(r, MarkerKind.Focus);
        MapViewer.CenterOn(r, 6);
        RefreshMarkers();
    }

    private void RefreshMarkers()
    {
        var list = new List<MapMarker>(_candidates.Count + _dupCandidates.Count + _gapCandidates.Count + 8);
        AddMarkers(list, _candidates, MarkerKind.Candidate, NoiseResults, NoiseAllCheck, CandidateCountText);
        AddMarkers(list, _dupCandidates, MarkerKind.Duplicate, DupResults, DupAllCheck, DupCountText);
        AddMarkers(list, _gapCandidates, MarkerKind.Gap, GapResults, GapAllCheck, GapCountText);
        foreach (PointD m in _alignMarks)
            list.Add(new MapMarker(new IntRect((int)Math.Floor(m.X) - 2, (int)Math.Floor(m.Y) - 2, 5, 5), MarkerKind.Focus));
        if (_focusMarker is MapMarker f) list.Add(f);
        MapViewer.Markers = list;
    }

    /// <summary>후보 마커 추가 + 목록 영역 표시 / 전체 체크 상태 / 개수 갱신</summary>
    private static void AddMarkers(List<MapMarker> list, IEnumerable<BlobItem> items, MarkerKind kind,
        FrameworkElement results, CheckBox allCheck, TextBlock countText)
    {
        int total = 0, chk = 0;
        foreach (BlobItem c in items)
        {
            total++;
            if (c.IsChecked) chk++;
            list.Add(new MapMarker(c.Blob.Bounds, c.IsChecked ? kind : MarkerKind.Excluded));
        }
        results.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        allCheck.IsChecked = chk == 0 ? false : chk == total ? true : null;
        countText.Text = $"{total:N0}개 중 {chk:N0}개 체크";
    }

    private double SnapTol() =>
        double.TryParse(SnapTolBox.Text, out double v) && v >= 0 && v <= 20 ? v : 3;

    // ───────────── 노이즈 ─────────────

    private void OnDetectNoise(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (!ReadInt(NoiseMaxAreaBox, 1, 1_000_000, "최대 면적", out int maxArea)) return;
        if (!ReadInt(NoiseMaxSideBox, 1, 100_000, "최대 크기", out int maxSide)) return;

        PixelRegion? region = _selection;   // 선택 영역이 있으면 그 안에서만
        var sw = Stopwatch.StartNew();
        List<Blob> blobs;
        using (new WaitCursor())
            blobs = BlobDetector.Detect(_map, _occThreshold, maxArea, maxSide, region);
        int excluded = blobs.RemoveAll(TouchesProtect);

        _bulk = true;
        _candidates.Clear();
        int index = 1;
        foreach (Blob b in blobs)
            _candidates.Add(Track(BlobItem.Noise(index++, b, _occThreshold, _meta.Resolution)));
        _bulk = false;
        _focusMarker = null;
        RefreshMarkers();
        SetStatus($"노이즈 후보 {blobs.Count:N0}개 · {ScopeName()} ({sw.ElapsedMilliseconds} ms)" + (excluded > 0 ? $" · 보호 영역 {excluded}개 제외" : ""));
    }

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is BlobItem item) FocusOn(item.Blob.Bounds);
    }

    private void OnToggleAllCandidates(object sender, RoutedEventArgs e) => ToggleAll(_candidates);

    private void OnDeleteCandidates(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        List<BlobItem> targets = _candidates.Where(c => c.IsChecked).ToList();
        if (targets.Count == 0)
        {
            SetStatus("체크된 후보가 없습니다.");
            return;
        }
        if (!ReadInt(NoiseExpandBox, 0, 10, "주변 정리", out int expand)) return;

        long n = 0;
        BeginEdit($"노이즈 제거 {targets.Count}개");
        using (new WaitCursor())
        {
            foreach (BlobItem t in targets) n += BlobDetector.Erase(t.Blob, _tracker, t.Threshold, expand);
            CommitEdit();
        }
        RemoveItems(_candidates, targets);
        SetStatus($"노이즈 {targets.Count:N0}개 삭제 ({n:N0} px)" + BlockedNote());
    }

    /// <summary>객체 삭제 도구: 클릭한 덩어리 전체 삭제</summary>
    private void DeleteBlobAt(int x, int y)
    {
        if (_map == null || _tracker == null || !_map.InBounds(x, y)) return;

        Blob? blob = BlobDetector.ComponentAt(_map, x, y, _occThreshold);
        if (blob == null)
        {
            SetStatus($"장애물 픽셀이 아닙니다 ({MapValues.Describe(_map.Get(x, y))}, 장애물 기준값 {_occThreshold} 이상만 대상)");
            return;
        }
        if (blob.Area > BlobPickWarnArea && !ConfirmLargeBlob(blob, "객체 삭제", "벽체와 연결된 부분일 수 있습니다. 삭제할까요?"))
            return;
        if (!ReadInt(NoiseExpandBox, 0, 10, "주변 정리", out int expand)) return;

        BeginEdit("객체 삭제");
        int n = BlobDetector.Erase(blob, _tracker, _occThreshold, expand);
        CommitEdit();
        SetStatus($"객체 삭제: {blob.Area:N0} px ({blob.Bounds.Width}×{blob.Bounds.Height}), 변경 {n:N0} px" + BlockedNote());
    }

    private bool ConfirmLargeBlob(Blob blob, string title, string question)
    {
        _focusMarker = new MapMarker(blob.Bounds, MarkerKind.Focus);
        RefreshMarkers();
        bool ok = Confirm($"큰 덩어리입니다. ({blob.Area:N0} px, {blob.Bounds.Width}×{blob.Bounds.Height})\n{question}", title);
        _focusMarker = null;
        RefreshMarkers();
        return ok;
    }

    // ───────────── 주축 ─────────────

    private double? GetAxis()
    {
        if (_axis == null && _map != null)
        {
            using (new WaitCursor()) _axis = WallCleanup.EstimateAxis(_map, _occThreshold, null);
            UpdateAxisText();
        }
        return _axis;
    }

    private void UpdateAxisText()
    {
        AxisText.Text = _axis is double a ? $"{a:+0.00;-0.00;0.00}°" : "계산 전";
        AxisText.ToolTip = _axis != null ? "가로축 기준 벽 방향" : "벽 직선화 · 기둥 정리 때 자동으로 계산합니다.";
    }

    private void OnEstimateAxis(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        _axis = null;
        if (GetAxis() == null) AxisText.Text = "장애물이 부족해 계산할 수 없음";
        else SetStatus($"맵 주축 {_axis:0.00}°");
    }

    // ───────────── 벽 직선화 ─────────────

    private void OnStraightenSelection(object sender, RoutedEventArgs e)
    {
        if (_selection == null)
        {
            SetStatus("벽 하나를 감싸는 영역을 먼저 선택하세요. (또는 벽 직선화 도구 W로 드래그)");
            return;
        }
        StraightenRegion(_selection);
    }

    private void StraightenRegion(PixelRegion region)
    {
        if (_map == null || _tracker == null) return;
        if (!ReadInt(WallThicknessBox, 0, 30, "벽 두께", out int thickness)) return;
        double? axis = WallSnapCheck.IsChecked == true ? GetAxis() : null;

        BeginEdit("벽 직선화", useClip: false);   // 지정한 영역 자체가 범위
        StraightenResult? r = WallCleanup.Straighten(region, _occThreshold, thickness, axis, SnapTol(), _tracker);
        CommitEdit();

        if (r == null)
        {
            SetStatus("영역 안에 장애물 픽셀이 부족합니다.");
            return;
        }
        SetStatus($"벽 직선화: 각도 {r.AngleDeg:0.00}°{(r.Snapped ? " (주축 스냅)" : "")}, 두께 {r.Thickness} px, " +
                  $"길이 {r.LengthPx * _meta.Resolution:0.00} m" + BlockedNote());
    }

    // ───────────── 기둥 ─────────────

    private void RectifyPillarAt(int x, int y)
    {
        if (_map == null || _tracker == null || !_map.InBounds(x, y)) return;
        Blob? blob = BlobDetector.ComponentAt(_map, x, y, _occThreshold);
        if (blob == null)
        {
            SetStatus($"장애물 픽셀이 아닙니다 ({MapValues.Describe(_map.Get(x, y))})");
            return;
        }
        if (blob.Area > PillarWarnArea && !ConfirmLargeBlob(blob, "기둥 정리", "벽체와 연결된 덩어리일 수 있습니다. 사각형으로 바꿀까요?"))
            return;

        double? axis = PillarSnapCheck.IsChecked == true ? GetAxis() : null;
        BeginEdit("기둥 정리");
        RectifyResult? r = WallCleanup.Rectify(blob, axis, SnapTol(), PillarFillCheck.IsChecked == true, _tracker);
        CommitEdit();
        if (r == null) return;
        double res = _meta.Resolution;
        SetStatus($"기둥 정리: {r.WidthPx * res:0.00} × {r.HeightPx * res:0.00} m, 각도 {r.AngleDeg:0.0}°" + BlockedNote());
    }

    // ───────────── 벽 끊김 ─────────────

    private void OnDetectGaps(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (!ReadInt(GapMaxBox, 1, 500, "최대 틈", out int maxGap)) return;
        if (!ReadInt(GapMinRunBox, 2, 10000, "양쪽 벽 최소 길이", out int minRun)) return;

        PixelRegion? region = _selection;   // 선택 영역이 있으면 그 안에서만
        List<Blob> gaps;
        using (new WaitCursor())
            gaps = WallCleanup.FindGaps(_map, _occThreshold, maxGap, minRun, region);
        int excluded = gaps.RemoveAll(TouchesProtect);

        _bulk = true;
        _gapCandidates.Clear();
        int index = 1;
        foreach (Blob b in gaps)
            _gapCandidates.Add(Track(BlobItem.Gap(index++, b, _meta.Resolution)));
        _bulk = false;
        _focusMarker = null;
        RefreshMarkers();

        string tilt = _axis is double a && Math.Abs(a) > 0.5 ? $" · 맵이 {a:0.0}° 기울어져 있어 검출이 적을 수 있음" : "";
        SetStatus($"벽 끊김 후보 {gaps.Count:N0}개 · {ScopeName()} (확인 후 체크)" +
                  (excluded > 0 ? $" · 보호 영역 {excluded}개 제외" : "") + tilt);
    }

    private void OnGapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (GapList.SelectedItem is BlobItem item) FocusOn(item.Blob.Bounds);
    }

    private void OnToggleAllGaps(object sender, RoutedEventArgs e) => ToggleAll(_gapCandidates);

    private void OnFillGaps(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        List<BlobItem> targets = _gapCandidates.Where(c => c.IsChecked).ToList();
        if (targets.Count == 0)
        {
            SetStatus("체크된 후보가 없습니다. 연결할 끊김을 확인 후 체크하세요.");
            return;
        }

        BeginEdit($"벽 끊김 연결 {targets.Count}개");
        foreach (BlobItem t in targets)
            if (t.Blob.Pixels != null)
                foreach (int i in t.Blob.Pixels) _tracker.SetIndex(i, MapValues.Obstacle);
        int n = CommitEdit();
        RemoveItems(_gapCandidates, targets);
        SetStatus($"벽 끊김 {targets.Count:N0}개 연결 ({n:N0} px)" + BlockedNote());
    }

    // ───────────── 확률값 / 외곽 ─────────────

    private void OnCleanProbability(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        if (!ReadInt(FreeMaxBox, 1, 253, "Free 기준", out int freeMax)) return;
        if (freeMax >= _occThreshold)
        {
            SetStatus($"Free 기준({freeMax})은 장애물 기준값({_occThreshold})보다 작아야 합니다.");
            return;
        }
        if (_selection == null &&
            !Confirm($"선택 영역이 없어 맵 전체에 적용합니다.\n{freeMax} 이하 → Free, {_occThreshold} 이상 → 장애물\n계속할까요?", "확률값 정리"))
            return;

        (int toObs, int toFree) r;
        BeginEdit("확률값 정리", useClip: false);
        using (new WaitCursor())
        {
            r = WallCleanup.CleanProbability(_selection, _occThreshold, (byte)freeMax, _tracker);
            CommitEdit();
        }
        SetStatus($"확률값 정리: 장애물 {r.toObs:N0} px, Free {r.toFree:N0} px" + BlockedNote());
    }

    private void OnClearOutside(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        if (_selection == null)
        {
            SetStatus("건물 외곽을 폴리곤(P)이나 사각형(M)으로 먼저 선택하세요.");
            return;
        }
        if (!Confirm("선택 영역 밖의 모든 픽셀을 Unknown(0)으로 바꿉니다. (보호 영역 제외)\n계속할까요?", "외곽 정리"))
            return;

        int n;
        BeginEdit("외곽 정리", useClip: false);
        using (new WaitCursor())
        {
            n = WallCleanup.ClearOutside(_selection, _tracker);
            CommitEdit();
        }
        SetStatus($"외곽 정리: {n:N0} px → Unknown" + BlockedNote());
    }

    // ───────────── 기울기 보정 ─────────────

    private void OnDeskew(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (_reference != null)
        {
            ShowError("기준 맵 비교 중에는 사용할 수 없습니다.\n업데이트 보정은 기존 좌표를 유지해야 합니다.");
            return;
        }
        double? axisOpt = GetAxis();
        if (axisOpt is not double axis)
        {
            SetStatus("장애물이 부족해 주축을 계산할 수 없습니다.");
            return;
        }
        if (Math.Abs(axis) < 0.05)
        {
            SetStatus($"이미 수평·수직입니다 ({axis:0.00}°).");
            return;
        }

        double rad = axis * Math.PI / 180, c = Math.Abs(Math.Cos(rad)), s = Math.Abs(Math.Sin(rad));
        int nw = (int)Math.Ceiling(_map.Width * c + _map.Height * s - 1e-9);
        int nh = (int)Math.Ceiling(_map.Width * s + _map.Height * c - 1e-9);
        if (!Confirm($"맵을 {axis:0.00}° 회전해 벽을 수평·수직으로 맞춥니다.\n\n" +
                     $"· 크기: {_map.Width} × {_map.Height} → {nw} × {nh}\n" +
                     "· 좌표계가 바뀌어 스테이션·경로를 다시 티칭해야 합니다.\n" +
                     "· 실행 취소 이력이 초기화됩니다. (저장 전이면 파일을 다시 열어 되돌릴 수 있음)\n" +
                     "· 보호 영역과 도면 배치는 같이 회전합니다.\n\n계속할까요?", "기울기 보정"))
            return;

        CancelDrag();
        CancelPolygon();
        CancelAlign(true);

        int oldW = _map.Width, oldH = _map.Height;
        DeskewResult res;
        using (new WaitCursor()) res = WallCleanup.Deskew(_map, axis, _meta);

        // 이전 픽셀 좌표 p → 새 좌표 q = dc + R(-a)(p - sc)
        double cs = Math.Cos(rad), sn = Math.Sin(rad);
        double scx = oldW / 2.0, scy = oldH / 2.0, dcx = res.Image.Width / 2.0, dcy = res.Image.Height / 2.0;
        PointD Move(PointD p)
        {
            double vx = p.X - scx, vy = p.Y - scy;
            return new PointD(dcx + vx * cs + vy * sn, dcy - vx * sn + vy * cs);
        }

        // 보호 영역 회전 (사각형도 폴리곤으로)
        var newBounds = new IntRect(0, 0, res.Image.Width, res.Image.Height);
        var rotated = new List<NamedRegion>();
        foreach (NamedRegion p in _protect)
        {
            PixelRegion? r = PixelRegion.FromPolygon(p.Region.Outline.Select(Move).ToList(), newBounds);
            if (r != null) rotated.Add(new NamedRegion(p.Name, r));
        }
        _protect.Clear();
        foreach (NamedRegion p in rotated) _protect.Add(p);

        // 도면 배치 회전
        if (_dxfPlacement != null)
        {
            PointD o = Move(new PointD(_dxfPlacement.OffsetX, _dxfPlacement.OffsetY));
            _dxfPlacement.RotationDeg -= axis;
            _dxfPlacement.OffsetX = o.X;
            _dxfPlacement.OffsetY = o.Y;
            UpdateDxfPlacementUi();
        }

        _map = res.Image;
        _meta.OriginX = res.OriginX;
        _meta.OriginY = res.OriginY;
        _metaChanged = true;
        _tracker = new EditTracker(_map);
        _undo.Clear();
        _opLog.Add($"{DateTime.Now:HH:mm:ss} 기울기 보정 {axis:0.00}° ({oldW}×{oldH} → {_map.Width}×{_map.Height})");
        _axis = 0;
        UpdateAxisText();

        _selection = null;
        _candidates.Clear();
        _gapCandidates.Clear();
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
        SetStatus($"기울기 보정 완료: {axis:0.00}° 회전, {_map.Width} × {_map.Height}. 저장 시 yaml origin 갱신");
    }
}
