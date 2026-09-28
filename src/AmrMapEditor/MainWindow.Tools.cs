using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;

namespace AmrMapEditor;

public partial class MainWindow
{
    private readonly List<PointD> _polyPoints = new();   // 폴리곤 선택 중인 꼭짓점 (픽셀 중심)

    // ───────────── 도구 선택 ─────────────

    private RadioButton ToolButton(EditTool tool) => tool switch
    {
        EditTool.Brush => ToolBrush,
        EditTool.Eraser => ToolEraser,
        EditTool.Line => ToolLine,
        EditTool.Rect => ToolRect,
        EditTool.Fill => ToolFill,
        EditTool.Picker => ToolPicker,
        EditTool.Select => ToolSelect,
        EditTool.Polygon => ToolPolygon,
        EditTool.BlobPick => ToolBlobPick,
        EditTool.Wall => ToolWall,
        EditTool.Pillar => ToolPillar,
        EditTool.CandidatePick => ToolPick,
        _ => ToolRestore,
    };

    private void SelectTool(EditTool tool) => ToolButton(tool).IsChecked = true;

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse(tag, out EditTool tool))
        {
            CancelDrag();
            CancelPolygon();
            CancelAlign(true);
            _tool = tool;
            MapViewer.Preview = null;
            SetStatus(ToolHint(tool));
        }
    }

    private static string ToolHint(EditTool tool) => tool switch
    {
        EditTool.Brush => "브러시: 선택한 값으로 칠하기",
        EditTool.Eraser => "지우개: Free(1)로 칠하기",
        EditTool.Line => "직선: 드래그, Shift = 0/45/90° 스냅, Esc = 취소",
        EditTool.Rect => "사각형: 드래그 (속 채우기 옵션)",
        EditTool.Fill => "채우기: 클릭한 픽셀과 같은 값으로 연결된 영역",
        EditTool.Picker => "스포이드: 클릭한 픽셀 값을 그리기 값으로",
        EditTool.Select => "영역 선택: 드래그, 클릭 = 해제",
        EditTool.Polygon => "폴리곤 선택: 클릭으로 꼭짓점, 더블클릭/Enter 완료, Backspace 되돌리기, Esc 취소",
        EditTool.BlobPick => "객체 삭제: 클릭한 장애물 덩어리 전체를 Free로",
        EditTool.Wall => "벽 직선화: 벽 하나를 감싸듯 드래그 (정리 탭 › 벽 · 기둥 옵션에서 두께·스냅 설정)",
        EditTool.Pillar => "기둥 정리: 기둥을 클릭하면 사각형으로 정리",
        EditTool.CandidatePick => "후보 선택: 맵에서 후보를 클릭해 체크 / 해제",
        _ => "복원 브러시: 칠한 부분을 기준 맵 값으로 되돌림",
    };

    private static string ToolName(EditTool tool) => tool switch
    {
        EditTool.Brush => "브러시",
        EditTool.Eraser => "지우개",
        EditTool.Restore => "복원 브러시",
        _ => tool.ToString(),
    };

    private static bool IsBrushTool(EditTool t) => t is EditTool.Brush or EditTool.Eraser or EditTool.Restore;

    private static bool IsRectDragTool(EditTool t) => t is EditTool.Rect or EditTool.Select or EditTool.Wall;

    /// <summary>진행 중인 드래그 정리 (브러시는 그린 만큼 확정)</summary>
    private void CancelDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_tracker != null && _tracker.IsActive) CommitEdit();
        MapViewer.Preview = null;
    }

    // ───────────── 마우스 ─────────────

    private void OnMapMouseDown(object? sender, MapMouseEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        if (_alignStep > 0)
        {
            HandleAlignClick(e.X, e.Y);
            return;
        }
        if (_dragging) FinishDrag(e.X, e.Y, e.Modifiers);   // 비정상 종료된 드래그 정리

        int x = e.X, y = e.Y;
        switch (_tool)
        {
            case EditTool.Brush:
            case EditTool.Eraser:
            case EditTool.Restore:
                if (_tool == EditTool.Restore && _reference == null)
                {
                    SetStatus("복원 브러시는 기준 맵이 필요합니다. 업데이트 탭에서 기준 맵을 먼저 여세요.");
                    return;
                }
                BeginEdit(ToolName(_tool));
                _dragging = true;
                _lastX = x;
                _lastY = y;
                Raster.Stamp(x, y, Offsets(), Plot);
                FlushEdit();
                break;

            case EditTool.Line:
            case EditTool.Rect:
            case EditTool.Select:
            case EditTool.Wall:
                _dragging = true;
                _startX = _lastX = x;
                _startY = _lastY = y;
                UpdateDragPreview(x, y, e.Modifiers);
                break;

            case EditTool.Polygon:
                AddPolygonPoint(x, y, e.ClickCount);
                break;

            case EditTool.Fill:
                FloodFillAt(x, y);
                break;

            case EditTool.Picker:
                if (_map.InBounds(x, y))
                {
                    byte v = _map.Get(x, y);
                    SetDrawValue(v);
                    SetStatus($"그리기 값 = {MapValues.Describe(v)}");
                }
                break;

            case EditTool.BlobPick:
                DeleteBlobAt(x, y);
                break;

            case EditTool.Pillar:
                RectifyPillarAt(x, y);
                break;

            case EditTool.CandidatePick:
                ToggleCandidateAt(x, y);
                break;
        }
    }

    private void OnMapMouseMove(object? sender, MapMouseEventArgs e)
    {
        UpdateCursorStatus(e.X, e.Y);
        if (_map == null) return;

        if (_dragging)
        {
            if (IsBrushTool(_tool))
            {
                if (e.X != _lastX || e.Y != _lastY)
                {
                    Raster.StrokeLine(_lastX, _lastY, e.X, e.Y, Offsets(), Plot);
                    _lastX = e.X;
                    _lastY = e.Y;
                    FlushEdit();
                }
                MapViewer.Preview = new BrushPreview(e.X, e.Y, _brushSize, _brushRound);
            }
            else
            {
                UpdateDragPreview(e.X, e.Y, e.Modifiers);
            }
            return;
        }

        if (_polyPoints.Count > 0)
        {
            MapViewer.Preview = new PolygonPreview(_polyPoints.ToArray(), new PointD(e.X + 0.5, e.Y + 0.5));
            return;
        }

        MapViewer.Preview = _alignStep == 0 && (IsBrushTool(_tool) || _tool == EditTool.Line)
            ? new BrushPreview(e.X, e.Y, _brushSize, _brushRound)
            : new BrushPreview(e.X, e.Y, 1, false);
    }

    private void OnMapMouseUp(object? sender, MapMouseEventArgs e)
    {
        if (_dragging) FinishDrag(e.X, e.Y, e.Modifiers);
    }

    private void UpdateDragPreview(int x, int y, ModifierKeys mods)
    {
        _lastX = x;
        _lastY = y;
        switch (_tool)
        {
            case EditTool.Line:
                (int ex, int ey) = LineEnd(x, y, mods);
                MapViewer.Preview = new LinePreview(_startX, _startY, ex, ey, _brushSize);
                double len = Math.Sqrt((ex - _startX) * (double)(ex - _startX) + (ey - _startY) * (double)(ey - _startY));
                SetStatus($"직선 길이 {len:0} px ≈ {len * _meta.Resolution:0.00} m");
                break;
            case EditTool.Rect:
            case EditTool.Select:
            case EditTool.Wall:
                IntRect r = IntRect.FromCorners(_startX, _startY, x, y);
                MapViewer.Preview = new RectPreview(r);
                SetStatus($"{r.Width} × {r.Height} px ≈ {r.Width * _meta.Resolution:0.00} × {r.Height * _meta.Resolution:0.00} m");
                break;
        }
    }

    private void FinishDrag(int x, int y, ModifierKeys mods)
    {
        _dragging = false;
        switch (_tool)
        {
            case EditTool.Brush:
            case EditTool.Eraser:
            case EditTool.Restore:
                CommitEdit();
                break;

            case EditTool.Line:
                (int ex, int ey) = LineEnd(x, y, mods);
                BeginEdit("직선");
                Raster.StrokeLine(_startX, _startY, ex, ey, Offsets(), Plot);
                CommitEdit();
                break;

            case EditTool.Rect:
                IntRect rect = IntRect.FromCorners(_startX, _startY, x, y);
                BeginEdit("사각형");
                if (RectFillCheck.IsChecked == true) Raster.RectFill(rect, Plot);
                else Raster.RectOutline(rect, Offsets(), Plot);
                CommitEdit();
                break;

            case EditTool.Select:
                IntRect sel = IntRect.FromCorners(_startX, _startY, x, y).Intersect(Full);
                SetSelection(sel.Width <= 1 && sel.Height <= 1 ? null : PixelRegion.FromRect(sel));
                break;

            case EditTool.Wall:
                IntRect wr = IntRect.FromCorners(_startX, _startY, x, y).Intersect(Full);
                MapViewer.Preview = null;
                if (wr.Width >= 3 || wr.Height >= 3) StraightenRegion(PixelRegion.FromRect(wr));
                return;
        }
        MapViewer.Preview = null;
    }

    private (int X, int Y) LineEnd(int x, int y, ModifierKeys mods) =>
        (mods & ModifierKeys.Shift) != 0 ? Raster.SnapAngle(_startX, _startY, x, y) : (x, y);

    private (int Dx, int Dy)[] Offsets() => Raster.BrushOffsets(_brushSize, _brushRound);

    /// <summary>현재 도구 기준으로 한 픽셀 칠하기</summary>
    private void Plot(int x, int y)
    {
        if (_tracker == null) return;
        switch (_tool)
        {
            case EditTool.Eraser:
                _tracker.Set(x, y, MapValues.Free);
                break;
            case EditTool.Restore:
                if (_reference != null && _reference.InBounds(x, y)) _tracker.Set(x, y, _reference.Get(x, y));
                break;
            default:
                _tracker.Set(x, y, _drawValue);
                break;
        }
    }

    private void FloodFillAt(int x, int y)
    {
        if (_map == null || _tracker == null || !_map.InBounds(x, y)) return;
        int n;
        BeginEdit("채우기");
        using (new WaitCursor())
        {
            n = Raster.FloodFill(_map, x, y, _drawValue, _tracker);
            CommitEdit();
        }
        SetStatus((n > 0 ? $"채우기: {n:N0} px" : "채우기: 변경 없음 (같은 값이거나 선택 영역 밖)") + BlockedNote());
    }

    // ───────────── 폴리곤 선택 ─────────────

    private void AddPolygonPoint(int x, int y, int clickCount)
    {
        if (clickCount >= 2)
        {
            FinishPolygon();
            return;
        }
        var p = new PointD(x + 0.5, y + 0.5);
        if (_polyPoints.Count > 0 && _polyPoints[^1] == p) return;
        _polyPoints.Add(p);
        MapViewer.Preview = new PolygonPreview(_polyPoints.ToArray(), p);
        SetStatus($"폴리곤 {_polyPoints.Count}점 · 더블클릭 또는 Enter로 완료, Backspace 되돌리기, Esc 취소");
    }

    private void FinishPolygon()
    {
        if (_map == null) return;
        if (_polyPoints.Count < 3)
        {
            SetStatus("꼭짓점이 3개 이상 필요합니다.");
            return;
        }
        PixelRegion? r = PixelRegion.FromPolygon(_polyPoints.ToArray(), _map.Bounds);
        _polyPoints.Clear();
        MapViewer.Preview = null;
        if (r == null)
        {
            SetStatus("폴리곤이 맵 범위 밖입니다.");
            return;
        }
        SetSelection(r);
        SetStatus($"폴리곤 선택: {r.PixelCount:N0} px");
    }

    private void RemoveLastPolygonPoint()
    {
        if (_polyPoints.Count == 0) return;
        _polyPoints.RemoveAt(_polyPoints.Count - 1);
        MapViewer.Preview = _polyPoints.Count > 0 ? new PolygonPreview(_polyPoints.ToArray(), _polyPoints[^1]) : null;
    }

    private void CancelPolygon()
    {
        if (_polyPoints.Count == 0) return;
        _polyPoints.Clear();
        MapViewer.Preview = null;
    }

    // ───────────── 그리기 값 ─────────────

    private void OnValueRadioChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == ValueFree) _drawValue = MapValues.Free;
        else if (sender == ValueObstacle) _drawValue = MapValues.Obstacle;
        else if (sender == ValueUnknown) _drawValue = MapValues.Unknown;
        else if (sender == ValueCustom && MapValues.TryParse(CustomValueBox.Text, out byte v)) _drawValue = v;
        UpdateValueUi();
    }

    private void OnCustomValueChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (MapValues.TryParse(CustomValueBox.Text, out byte v))
        {
            CustomValueBox.ClearValue(Control.BorderBrushProperty);
            if (ValueCustom.IsChecked == true)
            {
                _drawValue = v;
                UpdateValueUi();
            }
        }
        else
        {
            CustomValueBox.BorderBrush = Brushes.Red;
        }
    }

    private void SetDrawValue(byte v)
    {
        switch (v)
        {
            case MapValues.Free: ValueFree.IsChecked = true; break;
            case MapValues.Obstacle: ValueObstacle.IsChecked = true; break;
            case MapValues.Unknown: ValueUnknown.IsChecked = true; break;
            default:
                CustomValueBox.Text = v.ToString();
                ValueCustom.IsChecked = true;
                break;
        }
        _drawValue = v;
        UpdateValueUi();
    }

    private void UpdateValueUi()
    {
        uint c = _lut[_drawValue];
        var swatch = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
        swatch.Freeze();
        string desc = MapValues.Describe(_drawValue);
        ValueSwatch.Background = swatch;
        ValueText.Text = desc;
        RailSwatch.Background = swatch;
        RailSwatchButton.ToolTip = $"그리기 값  {desc}\n1 Free · 2 장애물 · 3 Unknown";
    }

    private void OnRailSwatchClick(object sender, RoutedEventArgs e) => SegEdit.IsChecked = true;

    private void OnBrushSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _brushSize = (int)Math.Round(BrushSizeSlider.Value);
        BrushSizeText.Text = _brushSize.ToString();
    }

    private void OnBrushShapeChanged(object sender, RoutedEventArgs e) =>
        _brushRound = BrushRoundCheck.IsChecked == true;

    // ───────────── 선택 영역 ─────────────

    private void SetSelection(PixelRegion? r)
    {
        _selection = r;
        MapViewer.Selection = r;
        UpdateSelectionUi();
    }

    /// <summary>작업 대상 표시 (선택 영역 / 맵 전체)와 선택 영역이 필요한 버튼 활성화</summary>
    private void UpdateSelectionUi()
    {
        bool has = _selection != null;
        if (_selection is PixelRegion s)
        {
            double res = _meta.Resolution;
            string kind = s.IsRect ? "사각형" : "폴리곤";
            ScopeText.Text = $"{kind}  {s.Bounds.Width} × {s.Bounds.Height} px · {s.Bounds.Width * res:0.##} × {s.Bounds.Height * res:0.##} m";
        }
        else
        {
            ScopeText.Text = "대상  맵 전체";
        }
        ScopeBar.Background = Res(has ? "AccentSoftBrush" : "CardBrush");
        ScopeBar.BorderBrush = has ? Res("AccentSoftBrush") : Res("CardLineBrush");
        ScopeIcon.Stroke = Res(has ? "AccentBrush" : "SecondaryLabelBrush");
        ScopeText.Foreground = Res(has ? "LabelBrush" : "SecondaryLabelBrush");
        ScopeClearButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        FillSelectionButton.IsEnabled = has;
        AddProtectButton.IsEnabled = has;
        AddAreaButton.IsEnabled = has;
        RestoreSelectionButton.IsEnabled = has;
        StraightenButton.IsEnabled = has;
        ClearOutsideButton.IsEnabled = has;
    }

    /// <summary>상태 메시지용 작업 대상 이름</summary>
    private string ScopeName() => _selection != null ? "선택 영역" : "맵 전체";

    private void OnFillSelection(object sender, RoutedEventArgs e)
    {
        if (_map == null || _tracker == null) return;
        if (_selection is not PixelRegion s)
        {
            SetStatus("선택 영역이 없습니다.");
            return;
        }
        EditTracker tracker = _tracker;
        byte value = _drawValue;
        BeginEdit("선택 영역 채우기");
        s.ForEach((x, y) => tracker.Set(x, y, value));
        CommitEdit();
        SetStatus($"선택 영역을 {MapValues.Describe(_drawValue)}(으)로 채움" + BlockedNote());
    }

    private void OnClearSelection(object sender, RoutedEventArgs e) => SetSelection(null);
}
