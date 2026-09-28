using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmrMapEditor.Core;

namespace AmrMapEditor.Controls;

public enum MarkerKind { Candidate, Excluded, Focus, Duplicate, Gap }

public readonly record struct MapMarker(IntRect Rect, MarkerKind Kind);

public enum RegionKind { Update, Protect }

public readonly record struct RegionOverlay(PixelRegion Region, RegionKind Kind);

public abstract record CursorPreview;
public sealed record BrushPreview(int X, int Y, int Size, bool Round) : CursorPreview;
public sealed record LinePreview(int X0, int Y0, int X1, int Y1, int Size) : CursorPreview;
public sealed record RectPreview(IntRect Rect) : CursorPreview;
public sealed record PolygonPreview(IReadOnlyList<PointD> Points, PointD Cursor) : CursorPreview;

public sealed class MapMouseEventArgs : EventArgs
{
    public MapMouseEventArgs(int x, int y, ModifierKeys modifiers, int clickCount)
    {
        X = x;
        Y = y;
        Modifiers = modifiers;
        ClickCount = clickCount;
    }

    public int X { get; }
    public int Y { get; }
    public ModifierKeys Modifiers { get; }

    /// <summary>MouseDown에서 더블클릭이면 2</summary>
    public int ClickCount { get; }
}

/// <summary>
/// 맵 비트맵 표시 전용 컨트롤. 줌(휠)/팬(가운데 버튼, Space+드래그)만 직접 처리하고
/// 편집 입력은 픽셀 좌표 이벤트로 넘김.
/// </summary>
public sealed class MapView : FrameworkElement
{
    private const double MinZoom = 0.01;
    private const double MaxZoom = 80;
    private const double GridMinZoom = 12;

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    private static readonly Pen ImageBorderPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), 1));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(90, 120, 130, 145)), 1));
    private static readonly Pen CandidatePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x1E, 0xE0)), 1.5));
    private static readonly Pen ExcludedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), 1));
    private static readonly Pen FocusPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00)), 2.5));
    private static readonly Pen DarkPen = Frozen(new Pen(Brushes.Black, 1));
    private static readonly Pen LightDashPen = Frozen(new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen DuplicatePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E)), 1.5));
    private static readonly Pen GapPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0xD4)), 1.5));
    private static readonly Pen UpdateAreaPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), 1.5));
    private static readonly Brush UpdateAreaFill = Frozen(new SolidColorBrush(Color.FromArgb(22, 0x25, 0x63, 0xEB)));
    private static readonly Pen ProtectPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)), 1.5));
    private static readonly Brush ProtectFill = Frozen(new SolidColorBrush(Color.FromArgb(50, 0x16, 0xA3, 0x4A)));
    private static readonly Brush DxfBrush = Frozen(new SolidColorBrush(Color.FromArgb(220, 0xF9, 0x73, 0x16)));
    private static readonly Brush LinePreviewBrush = Frozen(new SolidColorBrush(Color.FromArgb(120, 255, 140, 0)));
    private static readonly Brush RectPreviewBrush = Frozen(new SolidColorBrush(Color.FromArgb(40, 255, 200, 0)));

    private readonly DrawingVisual _content = new();
    private readonly DrawingVisual _cursor = new();
    private readonly VisualCollection _children;

    private WriteableBitmap? _base;
    private WriteableBitmap? _overlay;
    private double _zoom = 1, _offX, _offY;

    private bool _panning;
    private Point _panStart;
    private double _panOffX, _panOffY;
    private bool _leftCaptured;
    private Point _lastPos;

    private CursorPreview? _preview;
    private IReadOnlyList<MapMarker> _markers = Array.Empty<MapMarker>();
    private PixelRegion? _selection;
    private IReadOnlyList<RegionOverlay> _regions = Array.Empty<RegionOverlay>();
    private Geometry? _dxf;
    private bool _showDxf = true;

    public MapView()
    {
        _children = new VisualCollection(this);
        _children.Add(_content);
        _children.Add(_cursor);

        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Cross;

        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_content, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_content, EdgeMode.Aliased);
        RenderOptions.SetEdgeMode(_cursor, EdgeMode.Aliased);
    }

    public event EventHandler<MapMouseEventArgs>? MapMouseDown;
    public event EventHandler<MapMouseEventArgs>? MapMouseMove;
    public event EventHandler<MapMouseEventArgs>? MapMouseUp;
    public event EventHandler? ViewChanged;

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }
    public bool HasImage => _base != null;
    public double Zoom => _zoom;

    /// <summary>true면 왼쪽 드래그가 팬으로 동작 (Space 누름 상태)</summary>
    public bool PanModifier { get; set; }

    public bool ShowGrid { get; set; } = true;

    public IReadOnlyList<MapMarker> Markers
    {
        get => _markers;
        set
        {
            _markers = value ?? Array.Empty<MapMarker>();
            RenderContent();
        }
    }

    public PixelRegion? Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            RenderContent();
        }
    }

    /// <summary>업데이트 영역 / 보호 영역 표시</summary>
    public IReadOnlyList<RegionOverlay> Regions
    {
        get => _regions;
        set
        {
            _regions = value ?? Array.Empty<RegionOverlay>();
            RenderContent();
        }
    }

    /// <summary>도면 선 (이미지 픽셀 좌표, Freeze된 Geometry)</summary>
    public Geometry? DxfGeometry
    {
        get => _dxf;
        set
        {
            _dxf = value;
            RenderContent();
        }
    }

    public bool ShowDxf
    {
        get => _showDxf;
        set
        {
            _showDxf = value;
            RenderContent();
        }
    }

    public CursorPreview? Preview
    {
        get => _preview;
        set
        {
            if (Equals(_preview, value)) return;
            _preview = value;
            RenderCursor();
        }
    }

    // ───────────── 비트맵 ─────────────

    public void CreateImage(int width, int height)
    {
        _base = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        _overlay = null;
        ImageWidth = width;
        ImageHeight = height;
        _markers = Array.Empty<MapMarker>();
        _regions = Array.Empty<RegionOverlay>();
        _selection = null;
        _preview = null;
    }

    /// <summary>맵 닫기: 비트맵과 표시 요소를 모두 비움</summary>
    public void ClearImage()
    {
        _base = null;
        _overlay = null;
        ImageWidth = 0;
        ImageHeight = 0;
        _markers = Array.Empty<MapMarker>();
        _regions = Array.Empty<RegionOverlay>();
        _selection = null;
        _preview = null;
        _dxf = null;
        RenderContent();
        RenderCursor();
    }

    /// <summary>region 영역을 fill 콜백으로 채워 기본 레이어에 반영</summary>
    public void UpdateBase(IntRect region, Action<IntRect, uint[]> fill)
    {
        if (_base != null) WriteRegion(_base, region, fill);
    }

    public void SetOverlayEnabled(bool enabled)
    {
        if (enabled)
        {
            if (_overlay == null && _base != null)
                _overlay = new WriteableBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Pbgra32, null);
        }
        else
        {
            _overlay = null;
        }
        RenderContent();
    }

    public void UpdateOverlay(IntRect region, Action<IntRect, uint[]> fill)
    {
        if (_overlay != null) WriteRegion(_overlay, region, fill);
    }

    private void WriteRegion(WriteableBitmap bmp, IntRect region, Action<IntRect, uint[]> fill)
    {
        IntRect r = region.Intersect(new IntRect(0, 0, ImageWidth, ImageHeight));
        if (r.IsEmpty) return;

        // 대형 영역은 약 1M 픽셀 단위로 나눠 기록 (임시 버퍼 메모리 제한)
        int rows = Math.Min(Math.Max(1, (1 << 20) / r.Width), r.Height);
        var buffer = new uint[r.Width * rows];
        for (int y = r.Y; y < r.Bottom; y += rows)
        {
            int h = Math.Min(rows, r.Bottom - y);
            var chunk = new IntRect(r.X, y, r.Width, h);
            fill(chunk, buffer);
            bmp.WritePixels(new Int32Rect(chunk.X, chunk.Y, chunk.Width, h), buffer, chunk.Width * 4, 0);
        }
    }

    // ───────────── 좌표 / 뷰 ─────────────

    public Point ScreenToImage(Point p) => new((p.X - _offX) / _zoom, (p.Y - _offY) / _zoom);

    public Point ImageToScreen(double x, double y) => new(x * _zoom + _offX, y * _zoom + _offY);

    private Rect ToScreenRect(IntRect r)
    {
        Point p = ImageToScreen(r.X, r.Y);
        return new Rect(p.X, p.Y, r.Width * _zoom, r.Height * _zoom);
    }

    public void FitToView()
    {
        if (!HasImage || ActualWidth <= 0 || ActualHeight <= 0) return;
        _zoom = Math.Clamp(Math.Min(ActualWidth / ImageWidth, ActualHeight / ImageHeight) * 0.95, MinZoom, MaxZoom);
        _offX = (ActualWidth - ImageWidth * _zoom) / 2;
        _offY = (ActualHeight - ImageHeight * _zoom) / 2;
        OnViewChanged();
    }

    public void ZoomAt(Point screen, double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Point ip = ScreenToImage(screen);
        _zoom = zoom;
        _offX = screen.X - ip.X * _zoom;
        _offY = screen.Y - ip.Y * _zoom;
        OnViewChanged();
    }

    /// <summary>영역이 화면 중앙에 보이도록 이동. 현재 줌이 minZoom보다 작으면 확대</summary>
    public void CenterOn(IntRect r, double minZoom)
    {
        if (!HasImage || r.IsEmpty || ActualWidth <= 0) return;
        double fit = Math.Min(ActualWidth / (r.Width + 20.0), ActualHeight / (r.Height + 20.0));
        _zoom = Math.Clamp(Math.Min(Math.Max(_zoom, minZoom), fit), MinZoom, MaxZoom);
        _offX = ActualWidth / 2 - (r.X + r.Width / 2.0) * _zoom;
        _offY = ActualHeight / 2 - (r.Y + r.Height / 2.0) * _zoom;
        OnViewChanged();
    }

    public void Refresh()
    {
        RenderContent();
        RenderCursor();
    }

    private void OnViewChanged()
    {
        Refresh();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── 렌더링 ─────────────

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Refresh();
    }

    private void RenderContent()
    {
        using DrawingContext dc = _content.RenderOpen();
        double vw = Math.Max(0, ActualWidth), vh = Math.Max(0, ActualHeight);
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, vw, vh));
        if (_base == null) return;

        var imgRect = new Rect(_offX, _offY, ImageWidth * _zoom, ImageHeight * _zoom);
        dc.DrawImage(_base, imgRect);
        if (_overlay != null) dc.DrawImage(_overlay, imgRect);
        dc.DrawRectangle(null, ImageBorderPen, imgRect);

        if (ShowGrid && _zoom >= GridMinZoom) DrawGrid(dc, imgRect, vw, vh);

        foreach (RegionOverlay ro in _regions)
        {
            Geometry g = OutlineGeometry(ro.Region);
            if (ro.Kind == RegionKind.Protect) dc.DrawGeometry(ProtectFill, ProtectPen, g);
            else dc.DrawGeometry(UpdateAreaFill, UpdateAreaPen, g);
        }

        if (_dxf != null && _showDxf)
        {
            // 도면은 이미지 좌표 Geometry → 줌/이동 변환 후 1 화면 픽셀 두께
            dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _offX, _offY));
            dc.DrawGeometry(null, new Pen(DxfBrush, 1.0 / _zoom), _dxf);
            dc.Pop();
        }

        foreach (MapMarker m in _markers)
        {
            Rect rect = ToScreenRect(m.Rect);
            rect.Inflate(3, 3);
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > vw || rect.Top > vh) continue;
            Pen pen = m.Kind switch
            {
                MarkerKind.Candidate => CandidatePen,
                MarkerKind.Excluded => ExcludedPen,
                MarkerKind.Duplicate => DuplicatePen,
                MarkerKind.Gap => GapPen,
                _ => FocusPen,
            };
            dc.DrawRectangle(null, pen, rect);
        }

        if (_selection != null)
        {
            Geometry g = OutlineGeometry(_selection);
            dc.DrawGeometry(null, DarkPen, g);
            dc.DrawGeometry(null, LightDashPen, g);
        }
    }

    /// <summary>영역 외곽선을 화면 좌표 Geometry로</summary>
    private Geometry OutlineGeometry(PixelRegion region) => ScreenPolyline(region.Outline, true, true);

    private StreamGeometry ScreenPolyline(IReadOnlyList<PointD> pts, bool closed, bool filled = false)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            if (pts.Count > 0)
            {
                ctx.BeginFigure(ImageToScreen(pts[0].X, pts[0].Y), filled, closed);
                var rest = new List<Point>(pts.Count - 1);
                for (int k = 1; k < pts.Count; k++) rest.Add(ImageToScreen(pts[k].X, pts[k].Y));
                ctx.PolyLineTo(rest, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    private void DrawGrid(DrawingContext dc, Rect imgRect, double vw, double vh)
    {
        Rect vis = Rect.Intersect(imgRect, new Rect(0, 0, vw, vh));
        if (vis.IsEmpty) return;
        int x0 = (int)Math.Floor((vis.Left - _offX) / _zoom), x1 = (int)Math.Ceiling((vis.Right - _offX) / _zoom);
        int y0 = (int)Math.Floor((vis.Top - _offY) / _zoom), y1 = (int)Math.Ceiling((vis.Bottom - _offY) / _zoom);
        for (int x = x0; x <= x1; x++)
        {
            double sx = x * _zoom + _offX;
            dc.DrawLine(GridPen, new Point(sx, vis.Top), new Point(sx, vis.Bottom));
        }
        for (int y = y0; y <= y1; y++)
        {
            double sy = y * _zoom + _offY;
            dc.DrawLine(GridPen, new Point(vis.Left, sy), new Point(vis.Right, sy));
        }
    }

    private void RenderCursor()
    {
        using DrawingContext dc = _cursor.RenderOpen();
        if (_base == null || _preview == null) return;

        switch (_preview)
        {
            case BrushPreview b:
            {
                int o = (b.Size - 1) / 2;
                Rect rect = ToScreenRect(new IntRect(b.X - o, b.Y - o, b.Size, b.Size));
                if (b.Round && b.Size > 2)
                {
                    var c = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                    dc.DrawEllipse(null, DarkPen, c, rect.Width / 2, rect.Height / 2);
                    dc.DrawEllipse(null, LightDashPen, c, rect.Width / 2, rect.Height / 2);
                }
                else
                {
                    dc.DrawRectangle(null, DarkPen, rect);
                    dc.DrawRectangle(null, LightDashPen, rect);
                }
                break;
            }
            case LinePreview l:
            {
                Point p0 = ImageToScreen(l.X0 + 0.5, l.Y0 + 0.5);
                Point p1 = ImageToScreen(l.X1 + 0.5, l.Y1 + 0.5);
                var thick = new Pen(LinePreviewBrush, Math.Max(1, l.Size * _zoom))
                {
                    StartLineCap = PenLineCap.Square,
                    EndLineCap = PenLineCap.Square,
                };
                dc.DrawLine(thick, p0, p1);
                dc.DrawLine(DarkPen, p0, p1);
                break;
            }
            case PolygonPreview pp:
            {
                var pts = new List<PointD>(pp.Points) { pp.Cursor };
                Geometry g = ScreenPolyline(pts, false);
                dc.DrawGeometry(null, DarkPen, g);
                dc.DrawGeometry(null, LightDashPen, g);
                foreach (PointD v in pp.Points)
                {
                    Point sp = ImageToScreen(v.X, v.Y);
                    dc.DrawRectangle(Brushes.White, DarkPen, new Rect(sp.X - 3, sp.Y - 3, 6, 6));
                }
                break;
            }
            case RectPreview r:
            {
                Rect rect = ToScreenRect(r.Rect);
                dc.DrawRectangle(RectPreviewBrush, DarkPen, rect);
                dc.DrawRectangle(null, LightDashPen, rect);
                break;
            }
        }
    }

    // ───────────── 마우스 ─────────────

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_base == null) return;

        Point p = e.GetPosition(this);
        _lastPos = p;

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && PanModifier))
        {
            _panning = true;
            _panStart = p;
            _panOffX = _offX;
            _panOffY = _offY;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            _leftCaptured = true;
            CaptureMouse();
            MapMouseDown?.Invoke(this, CreateArgs(p, e.ClickCount));
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_base == null) return;

        Point p = e.GetPosition(this);
        _lastPos = p;

        if (_panning)
        {
            _offX = _panOffX + (p.X - _panStart.X);
            _offY = _panOffY + (p.Y - _panStart.Y);
            OnViewChanged();
            return;
        }

        MapMouseMove?.Invoke(this, CreateArgs(p));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_panning && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left))
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _leftCaptured)
        {
            _leftCaptured = false;
            MapMouseUp?.Invoke(this, CreateArgs(e.GetPosition(this)));
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Cross;
        }
        // Alt+Tab 등으로 캡처를 잃으면 드래그 종료로 처리
        if (_leftCaptured)
        {
            _leftCaptured = false;
            MapMouseUp?.Invoke(this, CreateArgs(_lastPos));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_base == null) return;
        ZoomAt(e.GetPosition(this), _zoom * (e.Delta > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    private MapMouseEventArgs CreateArgs(Point p, int clickCount = 0)
    {
        Point ip = ScreenToImage(p);
        return new MapMouseEventArgs((int)Math.Floor(ip.X), (int)Math.Floor(ip.Y), Keyboard.Modifiers, clickCount);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
