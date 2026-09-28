using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;
using AmrMapEditor.Models;
using Microsoft.Win32;

namespace AmrMapEditor;

/// <summary>업데이트 보정: 기준 맵 비교, 업데이트 영역, 영역 밖 복원, 어긋남, 이중 벽</summary>
public partial class MainWindow
{
    private const int MaxDiffRegions = 1000;

    private MapImage? _reference;           // 현재 맵 좌표로 정렬된 기준 맵
    private string? _referenceName;
    private MapOverlayMode _overlayMode = MapOverlayMode.Diff;
    private readonly List<PixelRegion> _updateAreas = new();
    private bool[]? _updateMask;
    private OffsetResult? _lastOffset;
    private bool _dupDetected;

    // ───────────── 1. 기준 맵 ─────────────

    private void OnOpenReference(object sender, RoutedEventArgs e)
    {
        if (_map == null)
        {
            SetStatus("먼저 편집할(업데이트된) 맵을 여세요.");
            return;
        }

        var dlg = new OpenFileDialog { Filter = PgmFilter, Title = "기준 맵 열기 (업데이트 전 맵)" };
        if (dlg.ShowDialog(this) != true) return;

        MapImage r;
        try
        {
            r = PgmIO.Read(dlg.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"기준 맵을 열 수 없습니다.\n\n{ex.Message}");
            return;
        }

        // 좌표 정렬: 두 맵 모두 yaml이 있으면 origin 기준, 없으면 크기가 같을 때만 그대로 비교
        bool sameSize = r.Width == _map.Width && r.Height == _map.Height;
        MapMeta? refMeta = MapMeta.TryLoadForImage(dlg.FileName);
        int dx = 0, dy = 0;
        string note = "";
        if (_meta.SourcePath != null && refMeta?.SourcePath != null)
        {
            var off = MapAlign.OffsetFromMeta(_meta, _map.Height, refMeta, r.Height);
            if (off == null)
            {
                ShowError($"해상도가 달라 비교할 수 없습니다.\n현재 {_meta.Resolution} m/px, 기준 {refMeta.Resolution} m/px");
                return;
            }
            (dx, dy) = off.Value;
            if (dx != 0 || dy != 0 || !sameSize) note = $"yaml origin 기준 정렬 (이동 {dx}, {dy} px)";
        }
        else if (!sameSize)
        {
            dy = r.Height - _map.Height;   // origin(왼쪽 아래) 동일 가정
            if (!Confirm($"맵 크기가 다릅니다.\n현재 맵: {_map.Width} × {_map.Height}\n기준 맵: {r.Width} × {r.Height}\n\n" +
                         "yaml이 없어 왼쪽 아래(origin)가 같다고 보고 맞춥니다. 계속할까요?", "기준 맵 정렬"))
                return;
            note = "왼쪽 아래 기준 정렬 (yaml 없음)";
        }

        _reference = dx == 0 && dy == 0 && sameSize ? r : MapAlign.Shift(r, _map.Width, _map.Height, dx, dy);
        _referenceName = Path.GetFileName(dlg.FileName);
        RefFileText.Text = _referenceName + (note.Length > 0 ? $" · {note}" : "");
        RefFileText.ToolTip = dlg.FileName + (note.Length > 0 ? $"\n{note}" : "");
        _diffRegions.Clear();
        _dupCandidates.Clear();
        _dupDetected = false;
        _lastOffset = null;
        OffsetText.Text = "";
        OffsetText.ToolTip = null;

        if (_overlayMode == MapOverlayMode.None)
            OverlayDiff.IsChecked = true;   // 변경점 표시로 전환 (핸들러에서 오버레이 갱신)
        else
            RebuildOverlay();

        RefreshMarkers();
        UpdateDiffStats();
    }

    private void OnCloseReference(object sender, RoutedEventArgs e) => CloseReference();

    private void CloseReference()
    {
        _reference = null;
        _referenceName = null;
        _lastOffset = null;
        _dupDetected = false;
        RefFileText.Text = "";
        RefFileText.ToolTip = null;
        OffsetText.Text = "";
        OffsetText.ToolTip = null;
        _diffRegions.Clear();
        _dupCandidates.Clear();
        _focusMarker = null;
        RebuildOverlay();
        RefreshMarkers();
        RefreshUpdateGuide();
        if (_tool == EditTool.Restore) SelectTool(EditTool.Brush);
    }

    private void OnOverlayModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out MapOverlayMode mode))
        {
            _overlayMode = mode;
            RebuildOverlay();
        }
    }

    /// <summary>오버레이 레이어 전체 재생성</summary>
    private void RebuildOverlay()
    {
        bool on = _map != null && _reference != null && _overlayMode != MapOverlayMode.None;
        MapViewer.SetOverlayEnabled(on);
        if (on) RedrawOverlay(Full);
    }

    private void RedrawOverlay(IntRect r)
    {
        MapImage? map = _map, reference = _reference;
        if (map == null || reference == null || _overlayMode == MapOverlayMode.None) return;
        byte thr = _occThreshold;
        MapOverlayMode mode = _overlayMode;
        MapViewer.UpdateOverlay(r, (chunk, buf) => MapDiff.Fill(map, reference, thr, mode, chunk, buf));
    }

    private void UpdateDiffStats()
    {
        if (_map == null || _reference == null)
        {
            RefreshUpdateGuide();
            return;
        }
        DiffStats st = MapDiff.Count(_map, _reference, _occThreshold);
        DiffAddedText.Text = $"{st.Added:N0} px";
        DiffRemovedText.Text = $"{st.Removed:N0} px";
        DiffOtherText.Text = $"{st.Other:N0} px";
        RefreshUpdateGuide();
    }

    // ───────────── 2. 업데이트 영역 ─────────────

    private void OnAddUpdateArea(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (_selection == null)
        {
            SetStatus("업데이트한 범위를 사각형(M)이나 폴리곤(P)으로 먼저 선택하세요.");
            return;
        }
        _updateAreas.Add(_selection);
        SetSelection(null);
        UpdateAreasChanged();
        SetStatus($"업데이트 영역 {_updateAreas.Count}개");
    }

    private void OnRemoveLastUpdateArea(object sender, RoutedEventArgs e)
    {
        if (_updateAreas.Count == 0) return;
        _updateAreas.RemoveAt(_updateAreas.Count - 1);
        UpdateAreasChanged();
    }

    private void OnClearUpdateAreas(object sender, RoutedEventArgs e)
    {
        _updateAreas.Clear();
        UpdateAreasChanged();
    }

    private void UpdateAreasChanged()
    {
        _updateMask = _map != null && _updateAreas.Count > 0
            ? PixelRegion.BuildMask(_updateAreas, _map.Width, _map.Height)
            : null;
        long px = _updateMask?.LongCount(b => b) ?? 0;
        UpdateAreaText.Text = _updateAreas.Count == 0
            ? "지정 안 함 · 3~5단계는 맵 전체가 대상"
            : $"{_updateAreas.Count}개 · {px:N0} px · {px * _meta.Resolution * _meta.Resolution:0.#} m²";
        RefreshRegions();
        RefreshUpdateGuide();
    }

    // ───────────── 3. 영역 밖 변경 복원 ─────────────

    private void OnRevertOutside(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        if (_reference == null)
        {
            SetStatus("기준 맵을 먼저 여세요.");
            return;
        }
        if (_updateMask == null)
        {
            SetStatus("업데이트 영역을 먼저 지정하세요 (2단계).");
            return;
        }

        int n;
        BeginEdit("영역 밖 변경 복원", useClip: false);
        using (new WaitCursor())
        {
            n = UpdateCorrection.RevertOutside(_reference, _updateMask, _tracker);
            CommitEdit();
        }
        SetStatus((n == 0 ? "영역 밖 변경 없음" : $"영역 밖 변경 {n:N0} px를 기준 맵 값으로 되돌림") + BlockedNote());
    }

    // ───────────── 4. 어긋남 / 재정렬 ─────────────

    private void OnEstimateOffset(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (_reference == null)
        {
            SetStatus("기준 맵을 먼저 여세요.");
            return;
        }
        if (!ReadInt(OffsetRadiusBox, 1, 100, "탐색 범위", out int radius)) return;
        if (!ReadDouble(OffsetAngleBox, 0, 5, "회전 범위", out double angle)) return;

        OffsetResult? r;
        using (new WaitCursor())
            r = UpdateCorrection.EstimateOffset(_map, _reference, _occThreshold, _updateMask, radius, angle);

        if (r == null)
        {
            OffsetText.Text = "새로 생긴 장애물이 너무 적어 추정할 수 없습니다.";
            OffsetText.ToolTip = null;
            return;
        }
        _lastOffset = r;
        double res = _meta.Resolution;

        // 화면에는 결론만, 세부 수치는 툴팁으로
        string shift = $"x {r.Dx:+0;-0;0}, y {r.Dy:+0;-0;0} px" +
                       (Math.Abs(r.AngleDeg) > 1e-9 ? $", 회전 {r.AngleDeg:+0.0;-0.0}°" : "");
        string verdict;
        if (r.Dx == 0 && r.Dy == 0 && Math.Abs(r.AngleDeg) < 1e-9)
            verdict = "어긋남 없음 · 새 장애물은 실제 변경일 가능성이 큽니다.";
        else if (r.InlierRatio < 0.3)
            verdict = "겹침이 낮아 신뢰도가 낮습니다. 실제 변경이 많을 수 있습니다.";
        else if (Math.Abs(r.AngleDeg) >= 0.3)
            verdict = "회전 어긋남 · 이중 벽 정리(5)나 재업데이트를 권장합니다.";
        else
            verdict = "일정한 이동 · 옮겨서 합치거나 이중 벽을 정리하세요.";
        OffsetText.Text = $"{shift} · 겹침 {r.InlierRatio:P0}\n{verdict}";

        var sb = new StringBuilder();
        sb.AppendLine($"업데이트분을 {shift} 옮기면 기존 벽과 겹칩니다.");
        sb.AppendLine($"월드 x {r.Dx * res:+0.00;-0.00;0} m, y {-r.Dy * res:+0.00;-0.00;0} m");
        sb.Append($"기존 벽까지 평균 거리 {r.MeanBefore:0.0} → {r.MeanAfter:0.0} px · 대상 {r.PointCount:N0} px");
        OffsetText.ToolTip = sb.ToString();

        RealignDxBox.Text = r.Dx.ToString();
        RealignDyBox.Text = r.Dy.ToString();
        RefreshUpdateGuide();
    }

    private void OnRealign(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        if (_reference == null)
        {
            SetStatus("기준 맵을 먼저 여세요.");
            return;
        }
        if (!ReadInt(RealignDxBox, -200, 200, "이동 dx", out int dx)) return;
        if (!ReadInt(RealignDyBox, -200, 200, "이동 dy", out int dy)) return;
        if (dx == 0 && dy == 0)
        {
            SetStatus("이동량이 0입니다. 어긋남 추정을 먼저 실행하거나 값을 입력하세요.");
            return;
        }

        int moved;
        BeginEdit($"업데이트분 이동 ({dx}, {dy})", useClip: false);
        using (new WaitCursor())
        {
            moved = UpdateCorrection.Realign(_reference, _updateMask, dx, dy, _occThreshold, KeepRefCheck.IsChecked == true, _tracker);
            CommitEdit();
        }
        _dupCandidates.Clear();
        _dupDetected = false;
        RefreshMarkers();
        SetStatus($"업데이트분 {moved:N0} px를 ({dx}, {dy}) 이동해 합성" + BlockedNote());
    }

    // ───────────── 5. 이중 벽 ─────────────

    private void OnDetectDuplicates(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (_reference == null)
        {
            SetStatus("기준 맵을 먼저 여세요.");
            return;
        }
        if (!ReadDouble(DupDistBox, 1, 100, "기존 벽과 거리", out double dist)) return;
        if (!ReadInt(DupMinAreaBox, 1, 100000, "최소 면적", out int minArea)) return;

        List<Blob> blobs;
        using (new WaitCursor())
            blobs = UpdateCorrection.FindDuplicateWalls(_map, _reference, _occThreshold, _updateMask, dist, minArea);
        int excluded = blobs.RemoveAll(TouchesProtect);

        ResetCandidates();
        _bulk = true;
        int index = 1;
        foreach (Blob b in blobs.OrderByDescending(x => x.Area))
            _dupCandidates.Add(Track(BlobItem.Duplicate(index++, b, _meta.Resolution)));
        _bulk = false;
        _dupDetected = true;
        RefreshMarkers();
        RefreshUpdateGuide();
        AfterDetect(blobs.Count);
        SetStatus($"이중 벽 후보 {blobs.Count:N0}개" + (excluded > 0 ? $" (보호 영역 {excluded}개 제외)" : ""));
    }

    private void OnDupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DupList.SelectedItem is BlobItem item) FocusOn(item.Blob.Bounds);
    }

    private void OnToggleAllDup(object sender, RoutedEventArgs e) => ToggleAll(_dupCandidates);

    private void OnRestoreDuplicates(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null || _reference == null) return;
        List<BlobItem> targets = _dupCandidates.Where(c => c.IsChecked).ToList();
        if (targets.Count == 0)
        {
            SetStatus("체크된 후보가 없습니다.");
            return;
        }
        if (!ReadInt(DupExpandBox, 0, 10, "주변 정리", out int expand)) return;

        long n = 0;
        BeginEdit($"이중 벽 복원 {targets.Count}개", useClip: false);
        using (new WaitCursor())
        {
            foreach (BlobItem t in targets)
                n += UpdateCorrection.RestoreFromReference(t.Blob, _reference, _tracker, _occThreshold, expand);
            CommitEdit();
        }

        RemoveItems(_dupCandidates, targets);
        SetStatus($"이중 벽 {targets.Count:N0}개를 기준 맵 값으로 복원 ({n:N0} px)" + BlockedNote());
    }

    // ───────────── 6. 변경 영역 ─────────────

    private void OnRestoreSelection(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        MapImage? reference = _reference;
        if (reference == null)
        {
            SetStatus("기준 맵이 없습니다.");
            return;
        }
        if (_selection is not PixelRegion s)
        {
            SetStatus("선택 영역이 없습니다. 영역 선택(M)이나 폴리곤(P)으로 복원할 범위를 지정하세요.");
            return;
        }

        EditTracker tracker = _tracker;
        BeginEdit("선택 영역 복원");
        s.ForEach((x, y) => tracker.Set(x, y, reference.Get(x, y)));
        int n = CommitEdit();
        SetStatus($"선택 영역을 기준 맵 값으로 복원 ({n:N0} px)" + BlockedNote());
    }

    private void OnFindDiffRegions(object sender, RoutedEventArgs e)
    {
        if (_map == null || _reference == null)
        {
            SetStatus("기준 맵을 먼저 여세요.");
            return;
        }

        List<Blob> regions;
        using (new WaitCursor())
        {
            MapImage mask = MapDiff.CreateMask(_map, _reference);
            regions = BlobDetector.Detect(mask, MapValues.Obstacle, int.MaxValue, int.MaxValue, null, collectPixels: false);
        }

        _diffRegions.Clear();
        int index = 1;
        foreach (Blob b in regions.OrderByDescending(x => x.Area).Take(MaxDiffRegions))
            _diffRegions.Add(new RegionItem(index++, b));
        DiffRegionList.Visibility = _diffRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SetStatus(regions.Count == 0
            ? "변경 영역 없음"
            : $"변경 영역 {regions.Count:N0}개" + (regions.Count > MaxDiffRegions ? $" (면적 큰 순 {MaxDiffRegions}개 표시)" : ""));
    }

    private void OnDiffRegionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DiffRegionList.SelectedItem is RegionItem item) FocusOn(item.Bounds);
    }

    private void OnGoCleanupTab(object sender, RoutedEventArgs e) => SegClean.IsChecked = true;

    // ───────────── 진행 상황 ─────────────

    private void RefreshUpdateGuide()
    {
        bool hasRef = _map != null && _reference != null;
        RefEmptyPanel.Visibility = hasRef ? Visibility.Collapsed : Visibility.Visible;
        RefStepsPanel.Visibility = hasRef ? Visibility.Visible : Visibility.Collapsed;
        DiffRegionList.Visibility = _diffRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 기울기 보정은 좌표계를 바꾸므로 기준 맵 비교 중에는 막음
        DeskewButton.IsEnabled = _reference == null;
        if (!hasRef)
        {
            RevertOutsideButton.IsEnabled = false;
            return;
        }

        long outside = _updateMask != null ? UpdateCorrection.CountOutside(_map!, _reference!, _updateMask) : -1;
        OutsideText.Text = outside < 0 ? "업데이트 영역을 먼저 지정하세요."
            : outside == 0 ? "영역 밖 변경 없음" : $"영역 밖 변경 {outside:N0} px";
        RevertOutsideButton.IsEnabled = outside > 0;

        SetStep(Step2Badge, Step2Text, 2, _updateAreas.Count > 0);
        SetStep(Step3Badge, Step3Text, 3, outside == 0);
        SetStep(Step4Badge, Step4Text, 4, _lastOffset != null);
        SetStep(Step5Badge, Step5Text, 5, _dupDetected && _dupCandidates.Count == 0);
    }

    /// <summary>단계 번호 원: 완료되면 초록 체크</summary>
    private void SetStep(Border badge, TextBlock text, int step, bool done)
    {
        badge.Background = Res(done ? "GreenBrush" : "SegmentTrackBrush");
        text.Text = done ? "✓" : step.ToString();
        text.Foreground = done ? Brushes.White : Res("SecondaryLabelBrush");
    }
}
