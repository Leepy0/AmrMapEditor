using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;
using AmrMapEditor.Models;
using Microsoft.Win32;

namespace AmrMapEditor;

public enum EditTool { Brush, Eraser, Line, Rect, Fill, Picker, Select, Polygon, BlobPick, Restore, Wall, Pillar }

public partial class MainWindow : Window
{
    private const string PgmFilter = "PGM 맵 (*.pgm)|*.pgm|모든 파일 (*.*)|*.*";

    // 입력값 오류 표시 (Apple 시스템 빨강)
    private static readonly Brush ErrorBrush = CreateFrozen(Color.FromRgb(0xFF, 0x3B, 0x30));

    // InitializeComponent 중 발생하는 Checked/TextChanged 이벤트 무시용
    private readonly bool _ready;
    private readonly AppSettings _settings = AppSettings.Load();

    // 맵 상태
    private MapImage? _map;
    private MapImage? _original;     // 열기/저장 시점 스냅샷 (저장 전 검증, 변경 이력용)
    private string? _path;
    private MapMeta _meta = new();
    private bool _metaChanged;       // 기울기 보정으로 크기·origin 변경됨 → 저장 시 yaml/사이드카 갱신

    // 편집
    private EditTracker? _tracker;
    private readonly UndoStack _undo = new();
    private readonly List<string> _opLog = new();   // 저장 시 이력 파일에 남길 작업 목록
    private uint[] _lut = MapPalettes.Create(MapDisplayMode.Standard);

    // 설정 (기본값은 XAML과 일치)
    private EditTool _tool = EditTool.Brush;
    private byte _drawValue = MapValues.Obstacle;
    private int _brushSize = 3;
    private bool _brushRound;
    private byte _occThreshold = 128;
    private PixelRegion? _selection;
    private bool _dirty;

    // 드래그 상태
    private bool _dragging;
    private int _startX, _startY, _lastX, _lastY;

    // 후보 목록
    private readonly ObservableCollection<BlobItem> _candidates = new();
    private readonly ObservableCollection<BlobItem> _dupCandidates = new();
    private readonly ObservableCollection<BlobItem> _gapCandidates = new();
    private readonly ObservableCollection<RegionItem> _diffRegions = new();
    private MapMarker? _focusMarker;
    private bool _bulk;   // 일괄 변경 중 마커 갱신 억제

    public MainWindow()
    {
        InitializeComponent();

        CandidateList.ItemsSource = _candidates;
        DupList.ItemsSource = _dupCandidates;
        GapList.ItemsSource = _gapCandidates;
        DiffRegionList.ItemsSource = _diffRegions;
        ProtectList.ItemsSource = _protect;
        DxfLayerList.ItemsSource = _dxfLayers;

        MapViewer.MapMouseDown += OnMapMouseDown;
        MapViewer.MapMouseMove += OnMapMouseMove;
        MapViewer.MapMouseUp += OnMapMouseUp;
        MapViewer.ViewChanged += (_, _) => UpdateZoomStatus();
        MapViewer.MouseLeave += (_, _) =>
        {
            if (!_dragging && _polyPoints.Count == 0) MapViewer.Preview = null;
        };
        Loaded += OnLoaded;

        LoadSettings();
        _ready = true;

        if (MapValues.TryParse(OccThresholdBox.Text, out byte thr) && thr >= 2 && thr <= 254) _occThreshold = thr;
        UpdateValueUi();
        UpdateUnitLabels();
        UpdateTitle();
        UpdateUndoButtons();
        UpdateInfo();
        UpdateSelectionUi();
        RefreshMarkers();
        RefreshUpdateGuide();
    }

    private IntRect Full => _map?.Bounds ?? IntRect.Empty;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 실행 인자로 전달된 파일 열기 (pgm을 exe에 끌어놓기 등)
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) OpenMap(args[1]);
    }

    // ───────────── 입력값 저장 ─────────────

    private TextBox[] PersistedBoxes => new[]
    {
        OccThresholdBox, NoiseMaxAreaBox, NoiseMaxSideBox, NoiseExpandBox, OffsetRadiusBox, OffsetAngleBox,
        DupDistBox, DupMinAreaBox, DupExpandBox, SnapTolBox, WallThicknessBox, GapMaxBox, GapMinRunBox, FreeMaxBox,
    };

    private void LoadSettings()
    {
        foreach (TextBox box in PersistedBoxes)
            if (_settings.Values.TryGetValue(box.Name, out string? v)) box.Text = v;
    }

    private void SaveSettings()
    {
        foreach (TextBox box in PersistedBoxes) _settings.Values[box.Name] = box.Text;
        _settings.Save();
    }

    // ───────────── 파일 ─────────────

    private void OnOpen(object sender, RoutedEventArgs e) => OpenWithDialog();

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void OnSaveAs(object sender, RoutedEventArgs e) => SaveAs();

    private void OnDropFile(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (!ConfirmDiscard()) return;
            OpenMap(files[0]);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        SaveSettings();
    }

    private void OpenWithDialog()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = PgmFilter, Title = "맵 열기" };
        if (dlg.ShowDialog(this) == true) OpenMap(dlg.FileName);
    }

    private void OpenMap(string path)
    {
        MapImage map;
        try
        {
            map = PgmIO.Read(path);
        }
        catch (Exception ex)
        {
            ShowError($"파일을 열 수 없습니다.\n{path}\n\n{ex.Message}");
            return;
        }

        CancelDrag();
        CancelPolygon();
        CancelAlign(true);
        CloseReference();
        CloseDxf();

        _map = map;
        _original = map.Clone();
        _path = path;
        _metaChanged = false;
        _tracker = new EditTracker(map);
        _undo.Clear();
        _opLog.Clear();

        MapMeta? meta = MapMeta.TryLoadForImage(path);
        _meta = meta ?? new MapMeta { Resolution = _meta.Resolution };
        ResolutionBox.Text = _meta.Resolution.ToString(CultureInfo.InvariantCulture);

        _selection = null;
        _candidates.Clear();
        _dupCandidates.Clear();
        _gapCandidates.Clear();
        _diffRegions.Clear();
        _updateAreas.Clear();
        _updateMask = null;
        _focusMarker = null;
        _axis = null;
        UpdateAxisText();

        MapViewer.CreateImage(map.Width, map.Height);
        RedrawBase(Full);
        LoadProtect();
        LoadDxfLink();
        MapViewer.FitToView();

        SetDirty(false);
        UpdateInfo();
        UpdateSelectionUi();
        UpdateAreasChanged();
        RefreshMarkers();
        UpdateUndoButtons();
        UpdateDiffStats();
        UpdateZoomStatus();
        UpdateUnitLabels();

        MapStats st = MapStats.Compute(map);
        string warn = st.Invalid > 0 ? $"  ⚠ 범위 외 값(255) {st.Invalid:N0} px" : "";
        string prot = _protect.Count > 0 ? $"  · 보호 영역 {_protect.Count}개" : "";
        SetStatus($"열기 완료: {Path.GetFileName(path)}{prot}{warn}");
    }

    /// <summary>변경 내용이 있으면 저장 여부 확인. 계속 진행해도 되면 true</summary>
    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        MessageBoxResult r = MessageBox.Show(this, "변경 내용을 저장할까요?", "AMR Map Editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private bool Save()
    {
        if (_map == null) return false;
        return _path == null ? SaveAs() : SaveTo(_path);
    }

    private bool SaveAs()
    {
        if (_map == null) return false;
        var dlg = new SaveFileDialog
        {
            Filter = PgmFilter,
            Title = "다른 이름으로 저장",
            FileName = _path != null ? Path.GetFileName(_path) : "map.pgm",
            InitialDirectory = (_path != null ? Path.GetDirectoryName(_path) : null) ?? string.Empty,
        };
        return dlg.ShowDialog(this) == true && SaveTo(dlg.FileName);
    }

    private bool SaveTo(string path)
    {
        if (_map == null || _original == null) return false;
        CancelDrag();
        CancelPolygon();

        if (MessageBox.Show(this, BuildSaveReport(), "저장 전 확인", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        bool renamed = _path == null ||
                       !string.Equals(Path.GetFullPath(_path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        try
        {
            string? backup = File.Exists(path) ? BackupFile(path) : null;
            PgmIO.Write(path, _map);
            string yamlNote = SaveYaml(path, renamed);

            _path = path;
            if (renamed || _metaChanged)
            {
                // 새 경로이거나 좌표가 바뀐 경우 사이드카도 함께 저장
                SaveProtect(true);
                SaveDxfLink(true);
            }

            string historyNote = HistoryCheck.IsChecked == true ? WriteHistory(path) : "";

            _original = _map.Clone();
            _opLog.Clear();
            _metaChanged = false;
            SetDirty(false);
            UpdateInfo();
            SetStatus($"저장 완료: {Path.GetFileName(path)}" +
                      (backup != null ? $"  (백업: _backup\\{Path.GetFileName(backup)})" : "") + yamlNote + historyNote);
            return true;
        }
        catch (Exception ex)
        {
            ShowError($"저장하지 못했습니다.\n{path}\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>기존 파일을 _backup 폴더에 시각 붙여 복사</summary>
    private static string BackupFile(string path)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "_backup");
        Directory.CreateDirectory(dir);
        string name = $"{Path.GetFileNameWithoutExtension(path)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(path)}";
        string dest = Path.Combine(dir, name);
        File.Copy(path, dest, overwrite: true);
        return dest;
    }

    /// <summary>
    /// yaml 처리: 같은 파일로 저장 → origin 변경 시에만 갱신.
    /// 새 이름으로 저장 → 새 이름의 yaml 생성 (image 항목 교체). 기존 yaml은 _backup에 백업
    /// </summary>
    private string SaveYaml(string path, bool renamed)
    {
        string? source = _meta.SourcePath;
        if (source == null || !File.Exists(source))
            return _metaChanged
                ? $"  ⚠ yaml 없음: origin ({_meta.OriginX:0.###}, {_meta.OriginY:0.###})을 직접 반영하세요"
                : "";

        string target = renamed
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", Path.GetFileNameWithoutExtension(path) + ".yaml")
            : source;
        if (!renamed && !_metaChanged) return "";
        if (renamed && !_metaChanged && File.Exists(target)) return "";

        string text = MapMeta.RewriteYaml(File.ReadAllText(source), renamed ? Path.GetFileName(path) : null,
            _meta.OriginX, _meta.OriginY, _meta.OriginTheta);
        if (File.Exists(target)) BackupFile(target);
        File.WriteAllText(target, text);
        _meta.SourcePath = target;
        return $"  · yaml 갱신: {Path.GetFileName(target)}";
    }

    private string WriteHistory(string path)
    {
        try
        {
            var note = new StringBuilder();
            if (_reference != null)
            {
                DiffStats st = MapDiff.Count(_map!, _reference, _occThreshold);
                note.AppendLine($"기준 맵({_referenceName}) 대비: 추가 장애물 {st.Added:N0} / 사라진 장애물 {st.Removed:N0} / 기타 {st.Other:N0} px");
            }
            if (_opLog.Count > 0)
            {
                note.AppendLine("작업:");
                foreach (string s in _opLog) note.AppendLine("  " + s);
            }
            ChangeHistory.Write(path, _original!, _map!, _occThreshold, note.ToString().TrimEnd());
            return "  · 이력 기록";
        }
        catch (Exception ex)
        {
            return $"  ⚠ 이력 기록 실패: {ex.Message}";
        }
    }

    private string BuildSaveReport()
    {
        MapImage map = _map!;
        MapImage orig = _original!;
        MapStats st = MapStats.Compute(map);

        var sb = new StringBuilder();
        sb.AppendLine($"크기: {map.Width} × {map.Height}  ({map.Format}, maxval {map.MaxVal})");
        sb.AppendLine();
        sb.AppendLine($"Free (1): {st.Free:N0}");
        sb.AppendLine($"장애물 (254): {st.Obstacle:N0}");
        sb.AppendLine($"Unknown (0): {st.Unknown:N0}");
        sb.AppendLine($"확률값 (2~253): {st.Probability:N0}");
        if (st.Invalid > 0) sb.AppendLine($"⚠ 범위 외 값 (255): {st.Invalid:N0}");
        if (map.MaxVal < 255 && st.CountAbove(map.MaxVal) > 0)
            sb.AppendLine($"⚠ maxval({map.MaxVal}) 초과 값: {st.CountAbove(map.MaxVal):N0}");
        sb.AppendLine();

        if (map.Width != orig.Width || map.Height != orig.Height)
        {
            sb.AppendLine($"⚠ 크기 변경: {orig.Width} × {orig.Height} → {map.Width} × {map.Height}");
            sb.AppendLine("   좌표계가 바뀌었습니다. 스테이션·경로를 다시 티칭해야 합니다.");
            sb.AppendLine(_meta.SourcePath != null
                ? "   yaml origin을 함께 갱신합니다."
                : $"   yaml이 없어 origin ({_meta.OriginX:0.###}, {_meta.OriginY:0.###})은 직접 반영해야 합니다.");
        }
        else
        {
            (long changed, IntRect bounds) = MapStats.Compare(orig, map);
            sb.AppendLine(changed == 0 ? "열었을 때와 달라진 픽셀 없음" : $"달라진 픽셀: {changed:N0}  범위 {bounds}");
        }

        if (_reference != null)
        {
            DiffStats d = MapDiff.Count(map, _reference, _occThreshold);
            sb.AppendLine($"기준 맵 대비: 추가 장애물 {d.Added:N0} / 사라진 장애물 {d.Removed:N0} / 기타 {d.Other:N0} px");
            if (_updateMask != null)
            {
                long outside = UpdateCorrection.CountOutside(map, _reference, _updateMask);
                sb.AppendLine(outside == 0 ? "업데이트 영역 밖 변경 없음" : $"⚠ 업데이트 영역 밖 변경 {outside:N0} px");
            }
        }
        if (_protect.Count > 0) sb.AppendLine($"보호 영역 {_protect.Count}개" + (ProtectEnableCheck.IsChecked == true ? " 적용 중" : " (적용 해제됨)"));

        sb.AppendLine();
        sb.Append("저장할까요? 기존 파일은 _backup 폴더에 백업됩니다.");
        return sb.ToString();
    }

    // ───────────── 편집 공통 ─────────────

    /// <summary>편집 시작. useClip=false는 자체 범위를 가진 작업(영역 밖 복원, 외곽 정리 등)</summary>
    private void BeginEdit(string name, bool useClip = true)
    {
        if (_tracker == null) return;
        _tracker.Clip = useClip && ClipToSelectionCheck.IsChecked == true ? _selection : null;
        _tracker.Protect = ProtectEnableCheck.IsChecked == true ? _protectMask : null;
        _tracker.Begin(name);
    }

    /// <summary>드래그 중 바뀐 영역만 화면 반영</summary>
    private void FlushEdit()
    {
        if (_tracker == null) return;
        IntRect r = _tracker.TakeDirty();
        if (!r.IsEmpty) AfterPixelsChanged(r);
    }

    /// <summary>작업 확정. 실제 바뀐 픽셀 수 반환</summary>
    private int CommitEdit()
    {
        if (_tracker == null) return 0;
        FlushEdit();
        ChangeSet? cs = _tracker.Commit();
        if (cs == null) return 0;
        _undo.Push(cs);
        _opLog.Add($"{DateTime.Now:HH:mm:ss} {cs.Name} ({cs.Count:N0} px)");
        SetDirty(true);
        UpdateDiffStats();
        UpdateUndoButtons();
        return cs.Count;
    }

    /// <summary>마지막 작업에서 보호 영역 때문에 막힌 픽셀이 있으면 안내 문구</summary>
    private string BlockedNote() => _tracker != null && _tracker.BlockedCount > 0 ? "  · 보호 영역은 제외됨" : "";

    private void AfterPixelsChanged(IntRect r)
    {
        RedrawBase(r);
        RedrawOverlay(r);
    }

    private void RedrawBase(IntRect r)
    {
        MapImage? map = _map;
        if (map == null) return;
        uint[] lut = _lut;
        MapViewer.UpdateBase(r, (chunk, buf) => MapRender.FillBase(map, lut, chunk, buf));
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_map == null || _dragging) return;
        ChangeSet? cs = _undo.Undo(_map);
        if (cs == null) return;
        AfterUndoRedo(cs, "실행 취소");
    }

    private void Redo()
    {
        if (_map == null || _dragging) return;
        ChangeSet? cs = _undo.Redo(_map);
        if (cs == null) return;
        AfterUndoRedo(cs, "다시 실행");
    }

    private void AfterUndoRedo(ChangeSet cs, string action)
    {
        AfterPixelsChanged(cs.Bounds);
        _opLog.Add($"{DateTime.Now:HH:mm:ss} {action}: {cs.Name}");
        SetDirty(true);
        UpdateDiffStats();
        UpdateUndoButtons();
        SetStatus($"{action}: {cs.Name} ({cs.Count:N0} px)");
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.CanUndo;
        RedoButton.IsEnabled = _undo.CanRedo;
        UndoButton.ToolTip = _undo.UndoName != null ? $"Ctrl+Z · {_undo.UndoName}" : "Ctrl+Z";
        RedoButton.ToolTip = _undo.RedoName != null ? $"Ctrl+Y · {_undo.RedoName}" : "Ctrl+Y";
    }

    // ───────────── 표시 ─────────────

    private void OnFit(object sender, RoutedEventArgs e) => MapViewer.FitToView();

    private void OnGridToggle(object sender, RoutedEventArgs e)
    {
        MapViewer.ShowGrid = GridCheck.IsChecked == true;
        MapViewer.Refresh();
    }

    private void OnDisplayModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out MapDisplayMode mode))
        {
            _lut = MapPalettes.Create(mode);
            RedrawBase(Full);
            UpdateValueUi();
        }
    }

    private void OnResolutionChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (double.TryParse(ResolutionBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0)
        {
            _meta.Resolution = r;
            ResolutionBox.ClearValue(Control.BorderBrushProperty);
            UpdateUnitLabels();
            UpdateSelectionUi();
        }
        else
        {
            ResolutionBox.BorderBrush = ErrorBrush;
        }
    }

    private void OnThresholdChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (MapValues.TryParse(OccThresholdBox.Text, out byte v) && v >= 2 && v <= 254)
        {
            OccThresholdBox.ClearValue(Control.BorderBrushProperty);
            if (v == _occThreshold) return;
            _occThreshold = v;
            _axis = null;
            UpdateAxisText();
            RebuildOverlay();
            UpdateDiffStats();
        }
        else
        {
            OccThresholdBox.BorderBrush = ErrorBrush;
        }
    }

    // ───────────── 키보드 ─────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool inText = Keyboard.FocusedElement is TextBox;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O:
                    OpenWithDialog();
                    e.Handled = true;
                    break;
                case Key.S:
                    if (shift) SaveAs(); else Save();
                    e.Handled = true;
                    break;
                case Key.Z when !inText:
                    Undo();
                    e.Handled = true;
                    break;
                case Key.Y when !inText:
                    Redo();
                    e.Handled = true;
                    break;
                case Key.D when !inText:
                    SetSelection(null);
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (inText) return;

        switch (e.Key)
        {
            case Key.Space:
                MapViewer.PanModifier = true;
                e.Handled = true;
                break;
            case Key.B: SelectTool(EditTool.Brush); e.Handled = true; break;
            case Key.E: SelectTool(EditTool.Eraser); e.Handled = true; break;
            case Key.L: SelectTool(EditTool.Line); e.Handled = true; break;
            case Key.R: SelectTool(EditTool.Rect); e.Handled = true; break;
            case Key.G: SelectTool(EditTool.Fill); e.Handled = true; break;
            case Key.I: SelectTool(EditTool.Picker); e.Handled = true; break;
            case Key.M: SelectTool(EditTool.Select); e.Handled = true; break;
            case Key.P: SelectTool(EditTool.Polygon); e.Handled = true; break;
            case Key.D: SelectTool(EditTool.BlobPick); e.Handled = true; break;
            case Key.H: SelectTool(EditTool.Restore); e.Handled = true; break;
            case Key.W: SelectTool(EditTool.Wall); e.Handled = true; break;
            case Key.C: SelectTool(EditTool.Pillar); e.Handled = true; break;
            case Key.F: MapViewer.FitToView(); e.Handled = true; break;
            case Key.Enter when _polyPoints.Count > 0:
                FinishPolygon();
                e.Handled = true;
                break;
            case Key.Back when _polyPoints.Count > 0:
                RemoveLastPolygonPoint();
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                BrushSizeSlider.Value = Math.Max(BrushSizeSlider.Minimum, BrushSizeSlider.Value - 1);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                BrushSizeSlider.Value = Math.Min(BrushSizeSlider.Maximum, BrushSizeSlider.Value + 1);
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                SetDrawValue(MapValues.Free);
                e.Handled = true;
                break;
            case Key.D2:
            case Key.NumPad2:
                SetDrawValue(MapValues.Obstacle);
                e.Handled = true;
                break;
            case Key.D3:
            case Key.NumPad3:
                SetDrawValue(MapValues.Unknown);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_alignStep > 0) CancelAlign(true);
                else if (_polyPoints.Count > 0) CancelPolygon();
                else if (_dragging && _tool is EditTool.Line or EditTool.Rect or EditTool.Select or EditTool.Wall)
                {
                    // 드래그형 도구는 그리기 전에 취소 가능
                    _dragging = false;
                    MapViewer.Preview = null;
                }
                else SetSelection(null);
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            MapViewer.PanModifier = false;
            if (Keyboard.FocusedElement is not TextBox) e.Handled = true;
        }
    }

    // ───────────── 상태 표시 ─────────────

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string name = _path != null ? Path.GetFileName(_path) : "";
        Title = _map == null ? "AMR Map Editor" : $"{name}{(_dirty ? " — 편집됨" : "")} · AMR Map Editor";
    }

    /// <summary>파일 정보와 맵 유무에 따른 화면 상태 (빈 화면 안내, 인스펙터 활성)</summary>
    private void UpdateInfo()
    {
        bool open = _map != null;
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        InspectorBody.IsEnabled = open;
        SaveButton.IsEnabled = open;
        SaveAsButton.IsEnabled = open;

        if (_map == null)
        {
            FileNameText.Text = "열린 맵 없음";
            FileMetaText.Text = "Ctrl+O 또는 파일을 끌어놓으세요";
            FileMetaText.ToolTip = null;
            return;
        }
        string yaml = _meta.SourcePath != null
            ? $"origin ({_meta.OriginX:0.###}, {_meta.OriginY:0.###})"
            : "yaml 없음 · 해상도 직접 입력";
        string changed = _metaChanged ? "\n⚠ 기울기 보정됨 · 저장 시 origin 갱신" : "";
        FileNameText.Text = Path.GetFileName(_path);
        FileMetaText.Text = $"{_map.Width} × {_map.Height} px · {yaml}{changed}";
        FileMetaText.ToolTip = $"{_path}\n{_map.Format}, maxval {_map.MaxVal}" +
                               (_meta.SourcePath != null ? $"\nyaml: {Path.GetFileName(_meta.SourcePath)}" : "");
    }

    private void UpdateCursorStatus(int x, int y)
    {
        if (_map == null || !_map.InBounds(x, y))
        {
            StatusPos.Text = "-";
            StatusWorld.Text = "";
            StatusValue.Text = "";
            return;
        }
        StatusPos.Text = $"{x}, {y} px";
        (double wx, double wy) = _meta.PixelToWorld(x, y, _map.Height);
        StatusWorld.Text = $"{wx:0.000}, {wy:0.000} m";
        StatusValue.Text = MapValues.Describe(_map.Get(x, y));
    }

    private void UpdateZoomStatus() => StatusZoom.Text = MapViewer.HasImage ? $"{MapViewer.Zoom * 100:0}%" : "";

    private void SetStatus(string message) => StatusMessage.Text = message;

    private Brush Res(string key) => (Brush)FindResource(key);

    private static Brush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ───────────── 입력 파싱 ─────────────

    private bool ReadInt(TextBox box, int min, int max, string label, out int value)
    {
        bool ok = int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                  && value >= min && value <= max;
        MarkInput(box, ok, label, $"{min}~{max}");
        return ok;
    }

    private bool ReadDouble(TextBox box, double min, double max, string label, out double value)
    {
        bool ok = double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                  && value >= min && value <= max;
        MarkInput(box, ok, label, $"{min}~{max}");
        return ok;
    }

    private void MarkInput(TextBox box, bool ok, string label, string range)
    {
        if (ok)
        {
            box.ClearValue(Control.BorderBrushProperty);
            return;
        }
        box.BorderBrush = ErrorBrush;
        SetStatus($"{label}: {range} 범위의 숫자로 입력하세요.");
    }

    /// <summary>using 블록 동안 대기 커서 표시</summary>
    private sealed class WaitCursor : IDisposable
    {
        public WaitCursor() => Mouse.OverrideCursor = Cursors.Wait;

        public void Dispose() => Mouse.OverrideCursor = null;
    }
}
