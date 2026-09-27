using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AmrMapEditor.Core;
using AmrMapEditor.Models;
using Microsoft.Win32;

namespace AmrMapEditor;

/// <summary>DXF 도면 오버레이와 2점 정렬 (&lt;맵&gt;.dxf.json)</summary>
public partial class MainWindow
{
    private DxfDrawing? _dxf;
    private string? _dxfPath;
    private DxfPlacement? _dxfPlacement;
    private readonly ObservableCollection<LayerItem> _dxfLayers = new();

    // 2점 정렬 상태: 1 도면 A → 2 맵 A → 3 도면 B → 4 맵 B
    private int _alignStep;
    private PointD _alignDxf1, _alignDxf2, _alignMap1, _alignMap2;
    private readonly List<PointD> _alignMarks = new();

    private void OnOpenDxf(object sender, RoutedEventArgs e)
    {
        if (_map == null)
        {
            SetStatus("먼저 맵을 여세요.");
            return;
        }
        var dlg = new OpenFileDialog { Filter = "DXF 도면 (*.dxf)|*.dxf|모든 파일 (*.*)|*.*", Title = "도면 열기" };
        if (dlg.ShowDialog(this) != true) return;
        if (LoadDxf(dlg.FileName, null))
        {
            SaveDxfLink();
            SetStatus("도면을 맵 중앙에 배치했습니다. '2점 정렬'로 위치를 맞추세요.");
        }
    }

    private void OnCloseDxf(object sender, RoutedEventArgs e)
    {
        if (_dxf == null) return;
        CloseDxf();
        // 명시적으로 닫으면 맵과의 연결도 해제
        try
        {
            if (_path != null && File.Exists(DxfLink.PathFor(_path))) File.Delete(DxfLink.PathFor(_path));
        }
        catch (Exception)
        {
            // 연결 파일 삭제 실패는 무시
        }
    }

    private void CloseDxf()
    {
        CancelAlign(true);
        _dxf = null;
        _dxfPath = null;
        _dxfPlacement = null;
        _dxfLayers.Clear();
        MapViewer.DxfGeometry = null;
        DxfFileText.Text = "도면 없음";
        DxfUnitText.Text = "단위: -";
    }

    /// <summary>맵에 연결된 도면이 있으면 불러옴</summary>
    private void LoadDxfLink()
    {
        if (_path == null) return;
        DxfLink? link = DxfLink.Load(_path);
        if (link == null) return;
        if (!File.Exists(link.DxfPath))
        {
            SetStatus($"연결된 도면을 찾을 수 없습니다: {link.DxfPath}");
            return;
        }
        LoadDxf(link.DxfPath, link);
    }

    private bool LoadDxf(string path, DxfLink? link)
    {
        DxfDrawing d;
        try
        {
            using (new WaitCursor()) d = DxfReader.Read(path);
        }
        catch (Exception ex)
        {
            ShowError($"도면을 읽을 수 없습니다.\n{path}\n\n{ex.Message}");
            return false;
        }
        if (d.Polylines.Count == 0)
        {
            ShowError("도면에 표시할 선이 없습니다. (LINE, POLYLINE, CIRCLE, ARC, INSERT만 지원)");
            return false;
        }

        CancelAlign(true);
        _dxf = d;
        _dxfPath = path;

        var hidden = new HashSet<string>(link?.HiddenLayers ?? new List<string>());
        var counts = d.Polylines.GroupBy(p => p.Layer).ToDictionary(g => g.Key, g => g.Count());
        _bulk = true;
        _dxfLayers.Clear();
        foreach (string layer in d.Layers)
        {
            var item = new LayerItem(layer, counts[layer], !hidden.Contains(layer));
            item.PropertyChanged += (_, _) =>
            {
                if (_bulk) return;
                RebuildDxfGeometry();
                SaveDxfLink();
            };
            _dxfLayers.Add(item);
        }
        _bulk = false;

        _dxfPlacement = link != null
            ? new DxfPlacement { Scale = link.Scale, RotationDeg = link.RotationDeg, OffsetX = link.OffsetX, OffsetY = link.OffsetY }
            : DxfPlacement.CenterOn(d, IsLayerVisible, DxfUnitScale(), _map!.Bounds);

        DxfFileText.Text = $"{Path.GetFileName(path)}  (선 {d.Polylines.Count:N0}개, 레이어 {_dxfLayers.Count}개)";
        DxfUnitText.Text = $"단위: {d.UnitName} · 1단위 = {DxfUnitScale():0.#####} px";
        UpdateDxfPlacementUi();
        RebuildDxfGeometry();
        return true;
    }

    /// <summary>도면 1단위당 픽셀 (도면 단위 m 환산 ÷ 해상도)</summary>
    private double DxfUnitScale() => _dxf != null && _meta.Resolution > 0 ? _dxf.UnitToMeter / _meta.Resolution : 1;

    private bool IsLayerVisible(string layer)
    {
        foreach (LayerItem l in _dxfLayers)
            if (l.Name == layer) return l.IsVisible;
        return true;
    }

    private void RebuildDxfGeometry()
    {
        if (_dxf == null || _dxfPlacement == null)
        {
            MapViewer.DxfGeometry = null;
            return;
        }
        var hidden = new HashSet<string>(_dxfLayers.Where(l => !l.IsVisible).Select(l => l.Name));
        var g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            foreach (DxfPolyline pl in _dxf.Polylines)
            {
                if (hidden.Contains(pl.Layer) || pl.Points.Count < 2) continue;
                PointD p0 = _dxfPlacement.ToPixel(pl.Points[0]);
                ctx.BeginFigure(new Point(p0.X, p0.Y), false, pl.Closed);
                var pts = new List<Point>(pl.Points.Count - 1);
                for (int k = 1; k < pl.Points.Count; k++)
                {
                    PointD p = _dxfPlacement.ToPixel(pl.Points[k]);
                    pts.Add(new Point(p.X, p.Y));
                }
                ctx.PolyLineTo(pts, true, false);
            }
        }
        g.Freeze();
        MapViewer.DxfGeometry = g;
    }

    /// <summary>도면 연결 저장. 기울기 보정 후(좌표 변경)에는 맵 저장 시에만 저장</summary>
    private void SaveDxfLink(bool force = false)
    {
        if (_path == null || _dxfPath == null || _dxfPlacement == null || (_metaChanged && !force)) return;
        try
        {
            new DxfLink
            {
                DxfPath = _dxfPath,
                Scale = _dxfPlacement.Scale,
                RotationDeg = _dxfPlacement.RotationDeg,
                OffsetX = _dxfPlacement.OffsetX,
                OffsetY = _dxfPlacement.OffsetY,
                HiddenLayers = _dxfLayers.Where(l => !l.IsVisible).Select(l => l.Name).ToList(),
            }.Save(_path);
        }
        catch (Exception ex)
        {
            SetStatus($"도면 연결 저장 실패: {ex.Message}");
        }
    }

    private void OnDxfShowToggle(object sender, RoutedEventArgs e) => MapViewer.ShowDxf = DxfShowCheck.IsChecked == true;

    private void OnDxfLayersAll(object sender, RoutedEventArgs e) => SetAllLayers(true);

    private void OnDxfLayersNone(object sender, RoutedEventArgs e) => SetAllLayers(false);

    private void SetAllLayers(bool visible)
    {
        _bulk = true;
        foreach (LayerItem l in _dxfLayers) l.IsVisible = visible;
        _bulk = false;
        RebuildDxfGeometry();
        SaveDxfLink();
    }

    // ───────────── 배치 값 ─────────────

    private void UpdateDxfPlacementUi()
    {
        if (_dxfPlacement == null) return;
        DxfOffsetXBox.Text = _dxfPlacement.OffsetX.ToString("0.##", CultureInfo.InvariantCulture);
        DxfOffsetYBox.Text = _dxfPlacement.OffsetY.ToString("0.##", CultureInfo.InvariantCulture);
        DxfRotationBox.Text = _dxfPlacement.RotationDeg.ToString("0.###", CultureInfo.InvariantCulture);
        DxfScaleBox.Text = _dxfPlacement.Scale.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private void OnDxfApply(object sender, RoutedEventArgs e)
    {
        if (_dxfPlacement == null) return;
        if (!ReadDouble(DxfOffsetXBox, -1e7, 1e7, "이동 X", out double ox)) return;
        if (!ReadDouble(DxfOffsetYBox, -1e7, 1e7, "이동 Y", out double oy)) return;
        if (!ReadDouble(DxfRotationBox, -360, 360, "회전", out double rot)) return;
        if (!ReadDouble(DxfScaleBox, 1e-9, 1e6, "축척", out double scale)) return;
        _dxfPlacement.OffsetX = ox;
        _dxfPlacement.OffsetY = oy;
        _dxfPlacement.RotationDeg = rot;
        _dxfPlacement.Scale = scale;
        RebuildDxfGeometry();
        SaveDxfLink();
        SetStatus("도면 배치 적용");
    }

    private void OnDxfFit(object sender, RoutedEventArgs e)
    {
        if (_dxf == null || _map == null) return;
        _dxfPlacement = DxfPlacement.CenterOn(_dxf, IsLayerVisible, DxfUnitScale(), _map.Bounds);
        UpdateDxfPlacementUi();
        RebuildDxfGeometry();
        SaveDxfLink();
    }

    // ───────────── 2점 정렬 ─────────────

    private void OnDxfAlignStart(object sender, RoutedEventArgs e)
    {
        if (_dxf == null || _dxfPlacement == null)
        {
            SetStatus("도면을 먼저 여세요.");
            return;
        }
        CancelDrag();
        CancelPolygon();
        _alignStep = 1;
        _alignMarks.Clear();
        RefreshMarkers();
        UpdateAlignPrompt();
    }

    private void UpdateAlignPrompt()
    {
        string msg = _alignStep switch
        {
            1 => "① 도면에서 기준점 A 클릭 (가까운 꼭짓점에 붙음)",
            2 => "② 맵에서 A와 같은 위치 클릭",
            3 => "③ 도면에서 기준점 B 클릭 (A와 멀리 떨어진 점)",
            4 => "④ 맵에서 B와 같은 위치 클릭",
            _ => "-",
        };
        DxfAlignText.Text = _alignStep > 0 ? msg + "  · Esc 취소" : msg;
        if (_alignStep > 0) SetStatus(msg);
    }

    private void HandleAlignClick(int x, int y)
    {
        var click = new PointD(x + 0.5, y + 0.5);
        switch (_alignStep)
        {
            case 1:
            case 3:
                PointD d = SnapDxfVertex(click, out PointD shown);
                if (_alignStep == 1) _alignDxf1 = d;
                else _alignDxf2 = d;
                _alignMarks.Add(shown);
                break;
            case 2:
                _alignMap1 = click;
                _alignMarks.Add(click);
                break;
            case 4:
                _alignMap2 = click;
                FinishAlign();
                return;
        }
        _alignStep++;
        RefreshMarkers();
        UpdateAlignPrompt();
    }

    /// <summary>클릭 위치 근처(화면 12px)의 도면 꼭짓점. 없으면 클릭 위치를 도면 좌표로 역변환</summary>
    private PointD SnapDxfVertex(PointD click, out PointD shown)
    {
        DxfPlacement pl = _dxfPlacement!;
        double tol = 12 / MapViewer.Zoom, best = tol * tol;
        PointD? found = null;
        shown = click;
        var hidden = new HashSet<string>(_dxfLayers.Where(l => !l.IsVisible).Select(l => l.Name));
        foreach (DxfPolyline line in _dxf!.Polylines)
        {
            if (hidden.Contains(line.Layer)) continue;
            foreach (PointD p in line.Points)
            {
                PointD q = pl.ToPixel(p);
                double d2 = (q.X - click.X) * (q.X - click.X) + (q.Y - click.Y) * (q.Y - click.Y);
                if (d2 < best)
                {
                    best = d2;
                    found = p;
                    shown = q;
                }
            }
        }
        return found ?? PixelToDxf(click);
    }

    /// <summary>DxfPlacement.ToPixel의 역변환</summary>
    private PointD PixelToDxf(PointD q)
    {
        DxfPlacement pl = _dxfPlacement!;
        double x = q.X - pl.OffsetX, y = q.Y - pl.OffsetY;
        double r = pl.RotationDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double xr = x * c + y * s, yr = -x * s + y * c;
        return new PointD(xr / pl.Scale, -yr / pl.Scale);
    }

    private void FinishAlign()
    {
        bool lockScale = DxfScaleLockCheck.IsChecked == true;
        DxfPlacement? p = DxfPlacement.FromTwoPoints(_alignDxf1, _alignDxf2, _alignMap1, _alignMap2,
            lockScale ? DxfUnitScale() : null);
        CancelAlign(false);
        if (p == null)
        {
            DxfAlignText.Text = "두 점이 너무 가깝습니다. 다시 시도하세요.";
            return;
        }

        _dxfPlacement = p;
        UpdateDxfPlacementUi();
        RebuildDxfGeometry();
        SaveDxfLink();

        string msg = $"정렬 완료 · 회전 {p.RotationDeg:0.00}°, 축척 {p.Scale:0.#####} px/단위";
        if (lockScale)
        {
            // 축척 고정이면 두 점이 정확히 맞지 않을 수 있음 → 오차 표시
            PointD a = p.ToPixel(_alignDxf1), b = p.ToPixel(_alignDxf2);
            double err = Math.Max(Dist(a, _alignMap1), Dist(b, _alignMap2));
            msg += $", 두 점 오차 {err:0.0} px ({err * _meta.Resolution:0.00} m)";
        }
        DxfAlignText.Text = msg;
        SetStatus(msg);
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void CancelAlign(bool resetText)
    {
        if (_alignStep == 0 && _alignMarks.Count == 0) return;
        _alignStep = 0;
        _alignMarks.Clear();
        RefreshMarkers();
        if (resetText) DxfAlignText.Text = "-";
    }
}
