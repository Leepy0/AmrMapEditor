using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;
using AmrMapEditor.Models;

namespace AmrMapEditor;

/// <summary>
/// 맵 회전 · 캔버스 크기 · 원점 변경, 그리고 크기 · 좌표가 바뀌는 작업(회전 · 기울기 보정 · 크기 · 합치기 · 원점)의 실행 취소.
/// 픽셀 단위 실행 취소로는 되돌릴 수 없으므로 작업 전 문서 상태(맵 · 원점 · 보호 영역 · 도면 · 맞출 맵 · 기준 맵 ·
/// 그 시점까지의 실행 취소 기록)를 통째로 보관했다가 Ctrl+Z로 바꿔 끼움
/// </summary>
public partial class MainWindow
{
    private const int MaxFrameSteps = 5;   // 문서 상태는 맵 전체를 들고 있으므로 개수 제한

    private sealed class DocState
    {
        public string Name { get; init; } = "";
        public MapImage Map { get; init; } = null!;
        public double OriginX { get; init; }
        public double OriginY { get; init; }
        public bool MetaChanged { get; init; }
        public string? MetaReason { get; init; }
        public UndoStack Undo { get; init; } = null!;
        public List<NamedRegion> Protect { get; init; } = new();
        public DxfPlacement? Dxf { get; init; }
        public List<PixelRegion> UpdateAreas { get; init; } = new();
        public MapImage? Reference { get; init; }
        public string? ReferenceName { get; init; }
        public MapImage? Second { get; init; }
        public PointD[] SecondHull { get; init; } = Array.Empty<PointD>();
        public PointD SecondHullCenter { get; init; }
        public string? SecondPath { get; init; }
        public MapMeta? SecondMeta { get; init; }
        public MapPose SecondPose { get; init; }
        public double? Axis { get; init; }
    }

    private readonly List<DocState> _frameUndo = new();
    private readonly List<DocState> _frameRedo = new();
    private bool _originPick;   // '맵에서 지정' 대기 중

    private bool CanUndoAny => _undo.CanUndo || _frameUndo.Count > 0;
    private bool CanRedoAny => _undo.CanRedo || _frameRedo.Count > 0;
    private string? UndoNameAny => _undo.UndoName ?? (_frameUndo.Count > 0 ? _frameUndo[^1].Name : null);
    private string? RedoNameAny => _undo.RedoName ?? (_frameRedo.Count > 0 ? _frameRedo[^1].Name : null);

    private static DxfPlacement? CloneDxf(DxfPlacement? d) =>
        d == null ? null : new DxfPlacement { Scale = d.Scale, RotationDeg = d.RotationDeg, OffsetX = d.OffsetX, OffsetY = d.OffsetY };

    private DocState CaptureState(string name) => new()
    {
        Name = name,
        Map = _map!,
        OriginX = _meta.OriginX,
        OriginY = _meta.OriginY,
        MetaChanged = _metaChanged,
        MetaReason = _metaChangeReason,
        Undo = _undo,
        Protect = _protect.ToList(),
        Dxf = CloneDxf(_dxfPlacement),
        UpdateAreas = _updateAreas.ToList(),
        Reference = _reference,
        ReferenceName = _referenceName,
        Second = _second,
        SecondHull = _secondHull,
        SecondHullCenter = _secondHullCenter,
        SecondPath = _secondPath,
        SecondMeta = _secondMeta,
        SecondPose = _secondPose,
        Axis = _axis,
    };

    /// <summary>
    /// 크기 · 좌표가 바뀌는 작업 직전: 지금 상태를 보관하고 새 실행 취소 기록을 시작.
    /// 호출한 쪽은 반드시 _map을 새 이미지로 바꿔야 함 (보관한 이미지를 계속 편집하지 않도록)
    /// </summary>
    private void PushFrame(string name)
    {
        _undo.ClearRedo();
        _frameUndo.Add(CaptureState(name));
        if (_frameUndo.Count > MaxFrameSteps) _frameUndo.RemoveAt(0);
        _frameRedo.Clear();
        _undo = new UndoStack();
    }

    private void ClearFrames()
    {
        _frameUndo.Clear();
        _frameRedo.Clear();
    }

    /// <summary>픽셀 실행 취소 기록이 비었을 때 Ctrl+Z: 직전 문서 상태로</summary>
    private bool UndoFrame()
    {
        if (_frameUndo.Count == 0 || FrameBusy()) return false;
        DocState prev = _frameUndo[^1];
        _frameUndo.RemoveAt(_frameUndo.Count - 1);
        _frameRedo.Add(CaptureState(prev.Name));
        RestoreState(prev);
        _opLog.Add($"{DateTime.Now:HH:mm:ss} 실행 취소: {prev.Name}");
        SetStatus($"실행 취소: {prev.Name} ({_map!.Width} × {_map.Height} px, 원점 {_meta.OriginX:0.###}, {_meta.OriginY:0.###})");
        return true;
    }

    private bool RedoFrame()
    {
        if (_frameRedo.Count == 0 || FrameBusy()) return false;
        DocState next = _frameRedo[^1];
        _frameRedo.RemoveAt(_frameRedo.Count - 1);
        _frameUndo.Add(CaptureState(next.Name));
        RestoreState(next);
        _opLog.Add($"{DateTime.Now:HH:mm:ss} 다시 실행: {next.Name}");
        SetStatus($"다시 실행: {next.Name} ({_map!.Width} × {_map.Height} px)");
        return true;
    }

    private bool FrameBusy()
    {
        if (_busyCts == null) return false;
        SetStatus("진행 중인 작업이 끝난 뒤 다시 시도하세요.");
        return true;
    }

    private void RestoreState(DocState s)
    {
        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        CancelPair();
        CancelOriginPick();
        bool sizeChanged = _map == null || _map.Width != s.Map.Width || _map.Height != s.Map.Height;

        _map = s.Map;
        _meta.OriginX = s.OriginX;
        _meta.OriginY = s.OriginY;
        _metaChanged = s.MetaChanged;
        _metaChangeReason = s.MetaReason;
        _undo = s.Undo;
        _protect.Clear();
        foreach (NamedRegion p in s.Protect) _protect.Add(p);
        _dxfPlacement = _dxf != null ? CloneDxf(s.Dxf) : null;
        if (_dxfPlacement != null) UpdateDxfPlacementUi();

        // 기준 맵 (같은 것이면 그대로 둠)
        if (!ReferenceEquals(_reference, s.Reference))
        {
            if (s.Reference != null) SetReference(s.Reference, s.ReferenceName ?? "기준 맵", "실행 취소로 다시 엶");
            else CloseReference();
        }

        // 맞출 맵
        if (s.Second == null)
        {
            if (_second != null) CloseSecond();
        }
        else
        {
            _second = s.Second;
            _secondHull = s.SecondHull;
            _secondHullCenter = s.SecondHullCenter;
            _secondPath = s.SecondPath;
            _secondMeta = s.SecondMeta;
            _secondPose = s.SecondPose;
            _secondHistory.Clear();
            _secondDiff = null;
            _secondRegions.Clear();
            ClearAlignResult();
        }

        _updateAreas.Clear();
        _updateAreas.AddRange(s.UpdateAreas);
        _axis = s.Axis;
        ReloadDocument(sizeChanged);
    }

    /// <summary>맵 이미지가 바뀐 뒤 화면 · 상태 전체 갱신</summary>
    private void ReloadDocument(bool sizeChanged)
    {
        _tracker = new EditTracker(_map!);
        _selection = null;
        ResetCandidates();
        _diffRegions.Clear();
        _focusMarker = null;
        UpdateAxisText();

        if (sizeChanged || !MapViewer.HasImage) MapViewer.CreateImage(_map!.Width, _map.Height);
        RedrawBase(Full);
        ApplyProtect();
        UpdateAreasChanged();
        RebuildOverlay();
        RebuildDxfGeometry();
        RefreshMarkers();
        if (sizeChanged) MapViewer.FitToView();
        SetDirty(true);
        UpdateInfo();
        UpdateSelectionUi();
        UpdateUndoButtons();
        UpdateDiffStats();
        if (_second != null)
        {
            RefreshSecondUi();
            RefreshSecondLayer();
            ApplySecondPose();
        }
    }

    // ───────────── 회전 ─────────────

    private void OnRotateLeft(object sender, RoutedEventArgs e) => RotateDocument(90, "맵 회전 (반시계 90°)");

    private void OnRotateRight(object sender, RoutedEventArgs e) => RotateDocument(-90, "맵 회전 (시계 90°)");

    private void OnRotate180(object sender, RoutedEventArgs e) => RotateDocument(180, "맵 회전 (180°)");

    private void OnRotateAngle(object sender, RoutedEventArgs e)
    {
        if (!ReadDouble(RotateAngleBox, -180, 180, "회전 각도", out double deg)) return;
        if (Math.Abs(deg) < 1e-9)
        {
            SetStatus("회전 각도를 입력하세요. + = 반시계, − = 시계 방향");
            return;
        }
        RotateDocument(deg, $"맵 회전 ({(deg > 0 ? "반시계" : "시계")} {Math.Abs(deg):0.##}°)");
    }

    /// <summary>
    /// 맵을 화면 기준 반시계로 ccwDeg만큼 회전 (캔버스 확장, 빈 곳은 Unknown, 90° 단위는 손실 없음).
    /// 월드 원점 (0, 0)이 맵에서 같은 지점에 남도록 origin을 다시 계산하고, 보호 영역 · 도면 · 맞출 맵도 같이 돌림.
    /// 실행 취소(Ctrl+Z)로 되돌릴 수 있음. 성공하면 true
    /// </summary>
    private bool RotateDocument(double ccwDeg, string name)
    {
        if (_map == null) return false;
        if (_reference != null)
        {
            ShowError("기준 맵을 연 상태에서는 회전할 수 없습니다",
                "업데이트 보정은 기존 좌표를 유지해야 합니다. 기준 맵을 닫은 뒤 다시 시도하세요.");
            return false;
        }
        if (FrameBusy()) return false;
        ccwDeg = MapPose.NormalizeDeg(ccwDeg);
        if (Math.Abs(ccwDeg) < 1e-9) return false;

        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        CancelPair();
        CancelOriginPick();
        PushFrame(name);

        int oldW = _map.Width, oldH = _map.Height;
        double res = _meta.Resolution;
        DeskewResult r;
        using (new WaitCursor()) r = WallCleanup.Deskew(_map, ccwDeg, _meta);

        // 이전 픽셀 좌표 p → 새 좌표 q = dc + R(p - sc) (화면 기준 반시계)
        double rad = ccwDeg * Math.PI / 180, cs = Math.Cos(rad), sn = Math.Sin(rad);
        double scx = oldW / 2.0, scy = oldH / 2.0, dcx = r.Image.Width / 2.0, dcy = r.Image.Height / 2.0;
        PointD Move(PointD p)
        {
            double vx = p.X - scx, vy = p.Y - scy;
            return new PointD(dcx + vx * cs + vy * sn, dcy - vx * sn + vy * cs);
        }

        // 월드 (0, 0) 지점이 회전 뒤에도 맵에서 같은 곳에 오도록 origin 계산
        PointD o = Move(new PointD(-_meta.OriginX / res, oldH + _meta.OriginY / res));
        double ox = -o.X * res, oy = -(r.Image.Height - o.Y) * res;

        // 보호 영역 (사각형도 폴리곤으로)
        var newBounds = new IntRect(0, 0, r.Image.Width, r.Image.Height);
        var rotated = new List<NamedRegion>();
        foreach (NamedRegion p in _protect)
        {
            PixelRegion? pr = PixelRegion.FromPolygon(p.Region.Outline.Select(Move).ToList(), newBounds);
            if (pr != null) rotated.Add(new NamedRegion(p.Name, pr));
        }
        _protect.Clear();
        foreach (NamedRegion p in rotated) _protect.Add(p);

        // 도면 배치
        if (_dxfPlacement != null)
        {
            PointD d = Move(new PointD(_dxfPlacement.OffsetX, _dxfPlacement.OffsetY));
            _dxfPlacement.RotationDeg -= ccwDeg;
            _dxfPlacement.OffsetX = d.X;
            _dxfPlacement.OffsetY = d.Y;
            UpdateDxfPlacementUi();
        }

        // 맞출 맵은 현재 맵과 같이 돈 자세로 (q' = Move(R(θ)p + T) = R(θ - a)p + Move(T))
        if (_second != null)
        {
            PointD t = Move(new PointD(_secondPose.Tx, _secondPose.Ty));
            _secondPose = new MapPose(MapPose.NormalizeDeg(_secondPose.AngleDeg - ccwDeg), t.X, t.Y);
            _secondHistory.Clear();
            _secondDiff = null;
            _secondRegions.Clear();
            ClearAlignResult();
        }

        _map = r.Image;
        _meta.OriginX = ox;
        _meta.OriginY = oy;
        MarkMetaChanged(name);
        _opLog.Add($"{DateTime.Now:HH:mm:ss} {name} ({oldW}×{oldH} → {_map.Width}×{_map.Height})");
        _axis = _axis is double a ? WallCleanup.NormalizeAxis(a - ccwDeg) : null;
        _updateAreas.Clear();
        ReloadDocument(sizeChanged: true);

        bool lossless = Math.Abs(Math.IEEERemainder(ccwDeg, 90)) < 1e-9;
        SetStatus($"{name}: {oldW} × {oldH} → {_map.Width} × {_map.Height} px · 월드 (0, 0)은 같은 지점 유지" +
                  (lossless ? "" : " · 90° 단위가 아니라 벽에 계단이 생길 수 있음 (벽 직선화로 정리)") +
                  " · 좌표계가 바뀌어 스테이션 · 경로는 다시 티칭", undo: true);
        return true;
    }

    // ───────────── 원점 ─────────────

    private void OnApplyOrigin(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (!ReadDouble(OriginXBox, -1e6, 1e6, "원점 X", out double x)) return;
        if (!ReadDouble(OriginYBox, -1e6, 1e6, "원점 Y", out double y)) return;
        SetOrigin(x, y, "원점 변경");
    }

    private void OnPickOrigin(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        CancelPair();
        _originPick = true;
        SetStatus("월드 (0, 0)으로 쓸 지점을 맵에서 클릭하세요 · 스테이션 · 충전기 같은 기준점 · Esc 취소");
    }

    private void CancelOriginPick()
    {
        if (!_originPick) return;
        _originPick = false;
        SetStatus("원점 지정을 취소했습니다.");
    }

    /// <summary>클릭한 픽셀 중심이 월드 (0, 0)이 되도록 origin 변경</summary>
    private void HandleOriginPick(int x, int y)
    {
        _originPick = false;
        if (_map == null || !_map.InBounds(x, y)) return;
        (double wx, double wy) = _meta.PixelToWorld(x, y, _map.Height);
        if (SetOrigin(_meta.OriginX - wx, _meta.OriginY - wy, "원점 지정"))
            SetStatus($"원점 지정: 픽셀 ({x}, {y})이 월드 (0, 0) · origin ({_meta.OriginX:0.###}, {_meta.OriginY:0.###}) m · 저장할 때 yaml에 반영", undo: true);
    }

    /// <summary>yaml origin(왼쪽 아래 픽셀의 월드 좌표) 변경. 픽셀은 그대로, 실행 취소 가능</summary>
    private bool SetOrigin(double x, double y, string name)
    {
        if (_map == null || FrameBusy()) return false;
        if (Math.Abs(x - _meta.OriginX) < 1e-9 && Math.Abs(y - _meta.OriginY) < 1e-9)
        {
            SetStatus("원점이 지금과 같습니다.");
            RefreshOriginUi();
            return false;
        }
        double oldX = _meta.OriginX, oldY = _meta.OriginY;
        PushFrame(name);
        _map = _map.Clone();   // 보관한 상태와 픽셀을 공유하지 않도록
        _meta.OriginX = x;
        _meta.OriginY = y;
        MarkMetaChanged("원점 변경");
        _opLog.Add($"{DateTime.Now:HH:mm:ss} {name}: ({oldX:0.###}, {oldY:0.###}) → ({x:0.###}, {y:0.###})");
        ReloadDocument(sizeChanged: false);
        SetStatus($"{name}: origin ({oldX:0.###}, {oldY:0.###}) → ({x:0.###}, {y:0.###}) m · 저장할 때 yaml에 반영 · 스테이션 좌표가 바뀝니다", undo: true);
        return true;
    }

    private void OnOriginShowToggle(object sender, RoutedEventArgs e) => RefreshOriginUi();

    /// <summary>원점 입력칸 · 지도 위 원점 표시 갱신</summary>
    private void RefreshOriginUi()
    {
        bool open = _map != null;
        OriginXBox.Text = open ? _meta.OriginX.ToString("0.###", CultureInfo.InvariantCulture) : "";
        OriginYBox.Text = open ? _meta.OriginY.ToString("0.###", CultureInfo.InvariantCulture) : "";
        OriginXBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        OriginYBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        ResizeWBox.Text = open ? _map!.Width.ToString(CultureInfo.InvariantCulture) : "";
        ResizeHBox.Text = open ? _map!.Height.ToString(CultureInfo.InvariantCulture) : "";
        ResizeWBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        ResizeHBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        ResizeCurrentText.Text = open ? $"지금 {_map!.Width} × {_map.Height} px" : "";
        UpdateOriginMark();
    }

    // ───────────── 캔버스 크기 ─────────────

    private const int MaxCanvasSide = 20000;

    /// <summary>선택한 기준 위치 (0 · 1 · 2, 0 · 1 · 2). 기본 왼쪽 아래</summary>
    private (int X, int Y) ResizeAnchor()
    {
        foreach (System.Windows.Controls.RadioButton rb in new[]
                 { AnchorTL, AnchorT, AnchorTR, AnchorL, AnchorC, AnchorR, AnchorBL, AnchorB, AnchorBR })
            if (rb.IsChecked == true && rb.Tag is string t && t.Length == 2)
                return (t[0] - '0', t[1] - '0');
        return (0, 2);
    }

    private void OnResizeFit(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        // 선택 영역이 있으면 그 범위로 자르기 (기준 위치 무시)
        if (_selection != null)
        {
            IntRect b = _selection.Bounds.Intersect(_map.Bounds);
            if (b.IsEmpty) return;
            if (b.Width == _map.Width && b.Height == _map.Height)
            {
                SetStatus("선택 범위가 맵 전체와 같습니다.");
                return;
            }
            ResizeCanvas(b.Width, b.Height, -b.X, -b.Y, "맵 크기 변경 (선택으로 자르기)");
            return;
        }
        SetStatus("자를 범위를 사각형(M)이나 폴리곤(P)으로 먼저 선택하세요.");
    }

    private void OnResizeApply(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (!ReadInt(ResizeWBox, 1, MaxCanvasSide, "폭", out int w)) return;
        if (!ReadInt(ResizeHBox, 1, MaxCanvasSide, "높이", out int h)) return;
        if (w == _map.Width && h == _map.Height)
        {
            SetStatus("크기가 지금과 같습니다.");
            return;
        }
        (int ax, int ay) = ResizeAnchor();
        (int ox, int oy) = MapCanvas.AnchorOffset(_map.Width, _map.Height, w, h, ax, ay);
        ResizeCanvas(w, h, ox, oy, "맵 크기 변경");
    }

    /// <summary>
    /// 캔버스 크기 변경 (픽셀 크기는 그대로). 기존 픽셀 (0, 0)을 (offX, offY)에 놓음.
    /// 내용의 월드 좌표가 그대로 남도록 origin을 맞추고, 보호 영역 · 도면 · 맞출 맵도 같이 옮김. 실행 취소 가능
    /// </summary>
    private bool ResizeCanvas(int newW, int newH, int offX, int offY, string name)
    {
        if (_map == null) return false;
        if (_reference != null)
        {
            ShowError("기준 맵을 연 상태에서는 크기를 바꿀 수 없습니다",
                "업데이트 보정은 기존 좌표를 유지해야 합니다. 기준 맵을 닫은 뒤 다시 시도하세요.");
            return false;
        }
        if ((long)newW * newH > 400_000_000L)
        {
            ShowError("맵이 너무 큽니다", $"{newW} × {newH} px는 다룰 수 없습니다. 4억 픽셀 이하로 정하세요.");
            return false;
        }
        if (FrameBusy()) return false;

        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        CancelPair();
        CancelOriginPick();

        CanvasResult r;
        using (new WaitCursor()) r = MapCanvas.Resize(_map, newW, newH, offX, offY);
        PushFrame(name);
        int oldW = _map.Width, oldH = _map.Height;
        double res = _meta.Resolution;

        // 내용의 월드 좌표 유지: 왼쪽 아래 기준 origin 보정
        double ox = _meta.OriginX - offX * res;
        double oy = _meta.OriginY - (newH - (oldH + offY)) * res;

        var bounds = new IntRect(0, 0, newW, newH);
        var moved = new List<NamedRegion>();
        foreach (NamedRegion p in _protect)
        {
            PixelRegion? pr = ShiftRegion(p.Region, offX, offY, bounds);
            if (pr != null) moved.Add(new NamedRegion(p.Name, pr));
        }
        int lostProtect = _protect.Count - moved.Count;
        _protect.Clear();
        foreach (NamedRegion p in moved) _protect.Add(p);
        if (_dxfPlacement != null)
        {
            _dxfPlacement.OffsetX += offX;
            _dxfPlacement.OffsetY += offY;
            UpdateDxfPlacementUi();
        }
        if (_second != null)
        {
            _secondPose = _secondPose.Translate(offX, offY);
            _secondHistory.Clear();
            _secondDiff = null;
            _secondRegions.Clear();
            ClearAlignResult();
        }
        var areas = new List<PixelRegion>();
        foreach (PixelRegion a in _updateAreas)
            if (ShiftRegion(a, offX, offY, bounds) is PixelRegion sa) areas.Add(sa);
        _updateAreas.Clear();
        _updateAreas.AddRange(areas);

        _map = r.Image;
        _meta.OriginX = ox;
        _meta.OriginY = oy;
        MarkMetaChanged("크기 변경");
        _opLog.Add($"{DateTime.Now:HH:mm:ss} {name}: {oldW}×{oldH} → {newW}×{newH} (기존 (0, 0) → ({offX}, {offY}))");
        ReloadDocument(sizeChanged: true);

        string lost = r.LostKnown > 0 ? $" · ⚠ 잘린 곳에 그려진 픽셀 {r.LostKnown:N0}개" : "";
        string lostP = lostProtect > 0 ? $" · 보호 영역 {lostProtect}개가 범위 밖이라 빠짐" : "";
        SetStatus($"{name}: {oldW} × {oldH} → {newW} × {newH} px · 내용의 월드 좌표는 그대로 (origin {ox:0.###}, {oy:0.###}){lost}{lostP}", undo: true);
        return true;
    }

    private void UpdateOriginMark()
    {
        double res = _meta.Resolution;
        MapViewer.OriginMark = _map != null && res > 0 && OriginShowCheck.IsChecked == true
            ? new PointD(-_meta.OriginX / res, _map.Height + _meta.OriginY / res)
            : null;
    }
}
