using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace AmrMapEditor;

/// <summary>
/// 프로그램 업데이트: 시작할 때 확인 → 상태 칩 → 받기 → 종료할 때 교체 (깨끗할 때만 지금 다시 시작).
/// 확인 실패(오프라인 · 프록시 차단)는 조용히 넘어감
/// </summary>
public partial class MainWindow
{
    private enum UpdateState { None, Available, Downloading, Ready }

    private const string AutoCheckKey = "UpdateAuto";
    private const string SkipKey = "UpdateSkip";

    private UpdateState _updState;
    private ReleaseInfo? _updRelease;      // 지금보다 새 정식 릴리스 (확인 결과)
    private ReleaseInfo? _updLatest;       // 최신 정식 릴리스 (같은 버전이어도, '파일로 저장'용)
    private PendingUpdate? _updPending;    // 받아 둔 업데이트 (종료할 때 적용)
    private CancellationTokenSource? _updCts;
    private double _updProgress;
    private bool _updChecking;
    private string? _updCheckError;
    private DateTime? _updCheckedAt;
    private bool _restartAfterClose;
    private Window? _about;
    private Action? _aboutRefresh;

    private bool AutoCheckEnabled => !_settings.Values.TryGetValue(AutoCheckKey, out string? v) || v != "0";

    private void InitAppUpdate()
    {
        Updater.CleanupLeftovers();
        if (_settings.Values.Remove("UpdatedFrom", out string? from))
            SetStatus($"{Updater.VersionText} 버전으로 업데이트했습니다 (이전 {from}). 변경 내용은 정보(ⓘ) › 릴리스 페이지에서 볼 수 있습니다.");
        else if (_settings.Values.Remove("RolledBackFrom", out string? rolled))
            SetStatus($"{Updater.VersionText} 버전으로 되돌렸습니다. {rolled} 버전은 자동으로 다시 받지 않습니다.");

        _updPending = Updater.LoadPending(_settings.Values);
        if (_updPending != null) _updState = UpdateState.Ready;
        RefreshUpdateUi();

        Loaded += async (_, _) =>
        {
            if (!Updater.CanSelfUpdate || !AutoCheckEnabled) return;
            await Task.Delay(3000);   // 시작 직후 화면 그리기와 겹치지 않게
            await CheckForUpdateAsync(manual: false);
        };
        Closed += (_, _) =>
        {
            if (!_restartAfterClose) return;
            try
            {
                Updater.Restart();
            }
            catch (Exception ex)
            {
                ErrorReport.Write(ex, "업데이트 후 다시 시작");
            }
        };
    }

    // ───────────── 확인 ─────────────

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updChecking || _updState == UpdateState.Downloading) return;
        _updChecking = true;
        RefreshUpdateUi();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ReleaseInfo? r = await Updater.GetLatestAsync(cts.Token);
            _updLatest = r;
            _updCheckError = null;
            _updCheckedAt = DateTime.Now;
            if (r == null || r.Version <= Updater.CurrentVersion)
            {
                _updRelease = null;
                if (_updState == UpdateState.Available) _updState = UpdateState.None;
            }
            else if (_updPending != null && _updPending.Version >= r.Version)
            {
                _updRelease = r;
                _updState = UpdateState.Ready;
            }
            else if (manual || !IsSkipped(r.Version))
            {
                _updRelease = r;
                _updState = UpdateState.Available;
            }
        }
        catch (Exception ex)
        {
            // 자동 확인 실패는 알리지 않음 (정보 창에만 표시)
            if (manual) ErrorReport.Write(ex, "업데이트 확인");
            _updCheckError = ErrorReport.Describe(ex).Cause;
        }
        finally
        {
            _updChecking = false;
            RefreshUpdateUi();
        }
    }

    private bool IsSkipped(Version v) => _settings.Values.TryGetValue(SkipKey, out string? s) && s == v.ToString(3);

    // ───────────── 상태 칩 ─────────────

    private void RefreshUpdateUi()
    {
        (string? text, string tip) = _updState switch
        {
            UpdateState.Available when _updRelease != null =>
                ($"새 버전 {_updRelease.Version.ToString(3)}", "눌러서 변경 내용 보기 · 받기"),
            UpdateState.Downloading =>
                ($"업데이트 받는 중 {_updProgress:0}%", "정보 창에서 취소할 수 있습니다."),
            UpdateState.Ready when _updPending != null =>
                ($"{_updPending.Version.ToString(3)} 준비됨", "프로그램을 닫을 때 바뀝니다 · 눌러서 지금 다시 시작"),
            _ => (null, ""),
        };
        UpdateChip.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateChipText.Text = text ?? "";
        UpdateChip.ToolTip = tip;
        _aboutRefresh?.Invoke();
    }

    private void OnUpdateChip(object sender, RoutedEventArgs e)
    {
        switch (_updState)
        {
            case UpdateState.Available when _updRelease != null:
                ShowUpdateOffer(_updRelease, this);
                break;
            case UpdateState.Ready:
                ShowUpdateReady(this);
                break;
            default:
                ShowAbout();
                break;
        }
    }

    // ───────────── 받기 ─────────────

    /// <summary>새 버전 안내: 변경 내용 + 받기 / 건너뛰기 / 나중에. 자동 교체가 안 되는 환경이면 릴리스 페이지 안내</summary>
    private void ShowUpdateOffer(ReleaseInfo r, Window owner)
    {
        string v = r.Version.ToString(3);
        string notes = string.IsNullOrWhiteSpace(r.Notes) ? "변경 내용이 없습니다." : r.Notes.Trim();
        var details = new[] { ("크기", SizeText(r)), ("배포", $"{r.Published.LocalDateTime:yyyy-MM-dd}") };

        if (!Updater.CanSelfUpdate || !Updater.CanWriteExeDir())
        {
            string why = !Updater.CanSelfUpdate
                ? "로컬 빌드는 자동으로 바꾸지 않습니다."
                : $"프로그램이 있는 폴더({Path.GetDirectoryName(Updater.ExePath)})에 쓸 수 없어 자동으로 바꿀 수 없습니다.";
            int c = MessageDialog.Show(owner, DialogKind.Info, $"새 버전 {v}", why + " 파일로 저장한 뒤 지금 쓰는 exe와 바꿔 주세요.", details, notes,
                new[] { new DialogButton("파일로 저장…", Primary: true), new DialogButton("닫기", Cancel: true) }, 0,
                "변경 내용", moreExpanded: true, moreMono: false);
            if (c == 0) SaveLatestToFile(owner);
            return;
        }

        int choice = MessageDialog.Show(owner, DialogKind.Info, $"새 버전 {v}",
            $"지금 {Updater.VersionText} 버전을 쓰고 있습니다. 받아 두면 프로그램을 닫을 때 바뀌고, 하던 작업에는 영향이 없습니다.",
            details, notes,
            new[] { new DialogButton("받기", Primary: true), new DialogButton("이 버전 건너뛰기"), new DialogButton("나중에", Cancel: true) }, 0,
            "변경 내용", moreExpanded: true, moreMono: false);
        if (choice == 0)
        {
            StartUpdateDownload(r);
        }
        else if (choice == 1)
        {
            _settings.Values[SkipKey] = v;
            _settings.Save();
            _updState = _updPending != null ? UpdateState.Ready : UpdateState.None;
            RefreshUpdateUi();
            SetStatus($"{v} 버전은 건너뜁니다. 정보(ⓘ) 창에서 다시 받을 수 있습니다.");
        }
    }

    private async void StartUpdateDownload(ReleaseInfo r)
    {
        if (_updState == UpdateState.Downloading) return;
        var cts = new CancellationTokenSource();
        _updCts = cts;
        _updState = UpdateState.Downloading;
        _updProgress = 0;
        RefreshUpdateUi();
        var progress = new Progress<double>(p =>
        {
            if (_updState != UpdateState.Downloading) return;
            _updProgress = p;
            RefreshUpdateUi();
        });

        bool retry = false;
        try
        {
            PendingUpdate p = await Updater.DownloadAsync(r, progress, cts.Token);
            _updPending = p;
            Updater.SavePending(_settings.Values, p);
            _settings.Values.Remove(SkipKey);
            _settings.Save();
            _updState = UpdateState.Ready;
            SetStatus($"{p.Version.ToString(3)} 버전을 받았습니다. 프로그램을 닫을 때 바뀝니다.");
        }
        catch (OperationCanceledException)
        {
            _updState = UpdateState.Available;
            SetStatus("업데이트 받기를 취소했습니다.");
        }
        catch (Exception ex)
        {
            _updState = UpdateState.Available;
            retry = ShowFailure("업데이트를 받지 못했습니다", null, ex, canRetry: true);
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_updCts, cts)) _updCts = null;
            RefreshUpdateUi();
        }
        if (retry) StartUpdateDownload(r);
    }

    private static string SizeText(ReleaseInfo r) => r.Size > 0 ? $"{r.Size / 1048576.0:0} MB" : "약 60 MB";

    /// <summary>
    /// 최신 정식 exe를 원하는 위치에 저장 (다른 PC에 옮기기 · 로컬 빌드 · 쓰기 권한 없는 폴더).
    /// 프로그램이 직접 받으므로 '인터넷에서 받음' 표시가 없어 SmartScreen 경고가 뜨지 않음
    /// </summary>
    private async void SaveLatestToFile(Window owner)
    {
        if (_busyCts != null)
        {
            SetStatus("진행 중인 작업이 끝난 뒤 다시 시도하세요.");
            return;
        }
        ReleaseInfo? r = _updLatest;
        if (r == null)
        {
            SetStatus("최신 버전을 확인하는 중…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                r = await Updater.GetLatestAsync(cts.Token);
            }
            catch (Exception ex)
            {
                SetStatus("");
                ShowFailure("최신 버전을 확인하지 못했습니다", null, ex, canRetry: false);
                return;
            }
            SetStatus("");
            if (r == null)
            {
                ShowError("받을 정식 버전이 없습니다", "아직 정식 배포된 버전이 없습니다. 릴리스 페이지를 확인하세요.");
                return;
            }
            _updLatest = r;
        }

        string v = r.Version.ToString(3);
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlg = new SaveFileDialog
        {
            Title = $"AMR Map Editor {v} 저장",
            FileName = Updater.AssetName,
            Filter = "프로그램 (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(owner) != true) return;
        string path = dlg.FileName;
        if (Updater.ExePath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(Updater.ExePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowError("실행 중인 파일에는 덮어쓸 수 없습니다", "다른 위치에 저장하세요. 지금 쓰는 exe를 바꾸려면 업데이트 받기를 쓰세요.");
            return;
        }

        CancellationToken ct = BeginBusy($"{v} 버전 받는 중", lockEditing: false, cancellable: true, showProgress: true);
        bool retry = false;
        try
        {
            await Updater.DownloadToAsync(r, path, new Progress<double>(ReportBusy), ct);
            SetStatus($"{v} 버전을 저장했습니다: {path}");
            Updater.ShowInFolder(path);
        }
        catch (OperationCanceledException)
        {
            SetStatus("저장을 취소했습니다.");
        }
        catch (Exception ex)
        {
            retry = ShowFailure("최신 버전을 저장하지 못했습니다", path, ex, canRetry: true);
        }
        finally
        {
            EndBusy();
        }
        if (retry) SaveLatestToFile(owner);
    }

    // ───────────── 적용 ─────────────

    private void ShowUpdateReady(Window owner)
    {
        if (_updPending == null) return;
        string v = _updPending.Version.ToString(3);
        if (_dirty)
        {
            MessageDialog.Show(owner, DialogKind.Info, $"{v} 버전 준비됨",
                "프로그램을 닫을 때 자동으로 바뀝니다. 지금 바꾸려면 먼저 저장하세요.", null, null,
                new[] { new DialogButton("닫기", Cancel: true) }, 0);
            return;
        }
        int c = MessageDialog.Show(owner, DialogKind.Info, $"{v} 버전 준비됨",
            "프로그램을 닫을 때 자동으로 바뀝니다. 지금 다시 시작해서 바꿀 수도 있습니다.", null, null,
            new[] { new DialogButton("지금 다시 시작", Primary: true), new DialogButton("닫을 때 바꾸기", Cancel: true) }, 0);
        if (c == 0) RestartWithUpdate();
    }

    /// <summary>저장 안 한 변경이 없을 때만: 지금 교체하고 다시 시작</summary>
    private void RestartWithUpdate()
    {
        if (_updPending == null || BusyBlocked()) return;
        if (_dirty)
        {
            SetStatus("저장한 뒤 다시 시작할 수 있습니다.");
            return;
        }
        if (!ApplyPendingUpdate(closing: false)) return;
        _restartAfterClose = true;
        _about?.Close();
        Close();
    }

    /// <summary>종료할 때: 받아 둔 업데이트 적용 (실패하면 안내하고 지금 버전으로 닫음)</summary>
    private void ApplyUpdateOnClose()
    {
        _updCts?.Cancel();   // 받는 중이면 다음 실행 때 다시
        if (_updPending != null) ApplyPendingUpdate(closing: true);
    }

    private bool ApplyPendingUpdate(bool closing)
    {
        PendingUpdate p = _updPending!;
        try
        {
            Updater.Apply(p);
        }
        catch (Exception ex)
        {
            if (ex is UpdateException { DropPending: true })
            {
                _updPending = null;
                _updState = _updRelease != null ? UpdateState.Available : UpdateState.None;
                Updater.SavePending(_settings.Values, null);
                _settings.Save();
                RefreshUpdateUi();
            }
            string log = ErrorReport.Write(ex, "업데이트 적용");
            (string cause, string fix) = ErrorReport.Describe(ex);
            int c = MessageDialog.Show(this, DialogKind.Error, "업데이트를 적용하지 못했습니다",
                closing ? "지금 버전 그대로 닫습니다. 다음에 닫을 때 다시 시도합니다." : "지금 버전을 계속 씁니다.",
                new[] { ("원인", cause), ("해결", fix) }, $"{ex.GetType().Name}: {ex.Message}\n\n로그: {log}",
                new[] { new DialogButton("릴리스 페이지 열기"), new DialogButton("닫기", Cancel: true) }, 1);
            if (c == 0) Updater.OpenPage(_updRelease?.PageUrl ?? Updater.ReleasesPage);
            return false;
        }
        _updPending = null;
        _updState = UpdateState.None;
        Updater.SavePending(_settings.Values, null);
        _settings.Values["UpdatedFrom"] = Updater.VersionText;
        _settings.Save();
        return true;
    }

    /// <summary>이전 버전(.old)으로 되돌리고 다시 시작. 지금 버전은 건너뛰기로 표시</summary>
    private void RollbackAndRestart()
    {
        string? old = Updater.OldVersionText;
        if (old == null || BusyBlocked()) return;
        if (_dirty)
        {
            SetStatus("저장한 뒤 되돌릴 수 있습니다.");
            return;
        }
        if (!Confirm($"{old} 버전으로 되돌릴까요?",
                $"프로그램을 다시 시작합니다. 지금 버전({Updater.VersionText})은 건너뛰기로 표시해 자동으로 다시 받지 않습니다.",
                "되돌리고 다시 시작"))
            return;
        try
        {
            Updater.Rollback();
        }
        catch (Exception ex)
        {
            ShowFailure("이전 버전으로 되돌리지 못했습니다", Updater.ExePath, ex, canRetry: false);
            return;
        }
        _settings.Values[SkipKey] = Updater.VersionText;
        _settings.Values["RolledBackFrom"] = Updater.VersionText;
        Updater.SavePending(_settings.Values, null);   // 받아 둔 더 새 버전도 적용하지 않음
        _updPending = null;
        _settings.Save();
        _restartAfterClose = true;
        _about?.Close();
        Close();
    }

    // ───────────── 정보 창 ─────────────

    private void OnAbout(object sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>버전 · 빌드 · 업데이트 상태와 동작 (확인 · 받기 · 취소 · 다시 시작 · 자동 확인 · 되돌리기)</summary>
    private void ShowAbout()
    {
        if (_about != null)
        {
            _about.Activate();
            return;
        }

        var w = new Window
        {
            Title = "정보 · 업데이트",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = this.FontFamily,
            FontSize = 14,
            UseLayoutRounding = true,
        };
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        w.SetResourceReference(BackgroundProperty, "CardBrush");
        w.SetResourceReference(ForegroundProperty, "LabelBrush");
        w.SourceInitialized += (_, _) => Theme.ApplyTitleBar(w);

        var root = new StackPanel { Margin = new Thickness(24, 24, 24, 0) };
        root.Children.Add(new TextBlock { Text = "AMR Map Editor", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var version = new TextBlock
        {
            Text = $"버전 {Updater.VersionText}" + (Updater.Commit != null ? $" · 빌드 {Updater.Commit}" : "") + $" · {Updater.ChannelName}",
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        version.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        root.Children.Add(version);

        var header = new TextBlock { Text = "업데이트", Margin = new Thickness(0, 24, 0, 8) };
        header.SetResourceReference(FrameworkElement.StyleProperty, "SectionHeader");
        root.Children.Add(header);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        root.Children.Add(status);
        var bar = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(bar);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var primary = new Button { Padding = new Thickness(16, 0, 16, 0), MinWidth = 88 };
        primary.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        ToolTipService.SetShowOnDisabled(primary, true);
        var secondary = new Button { Padding = new Thickness(16, 0, 16, 0), MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(primary);
        actions.Children.Add(secondary);
        root.Children.Add(actions);

        var auto = new CheckBox
        {
            Content = "시작할 때 새 버전 확인",
            IsChecked = AutoCheckEnabled,
            IsEnabled = Updater.CanSelfUpdate,
            Margin = new Thickness(0, 16, 0, 0),
        };
        auto.SetResourceReference(FrameworkElement.StyleProperty, "Switch");
        auto.Click += (_, _) =>
        {
            _settings.Values[AutoCheckKey] = auto.IsChecked == true ? "1" : "0";
            _settings.Save();
        };
        root.Children.Add(auto);

        var rollback = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        rollback.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        ToolTipService.SetShowOnDisabled(rollback, true);
        rollback.Click += (_, _) => RollbackAndRestart();
        root.Children.Add(rollback);

        // 직접 받기: 다른 PC에 옮기거나 직접 바꿀 때. 프로그램이 받은 파일은 SmartScreen 경고가 없음
        var directHeader = new TextBlock { Text = "직접 받기", Margin = new Thickness(0, 24, 0, 8) };
        directHeader.SetResourceReference(FrameworkElement.StyleProperty, "SectionHeader");
        root.Children.Add(directHeader);
        var directText = new TextBlock
        {
            Text = "다른 PC에 옮기거나 exe를 직접 바꿀 때 씁니다. '파일로 저장'은 프로그램이 직접 받아 SmartScreen 경고가 뜨지 않습니다. 브라우저로 받으면 처음 실행할 때 경고가 뜰 수 있습니다.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        directText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        root.Children.Add(directText);

        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var download = new Button { ToolTip = "최신 정식 버전을 받아 원하는 위치에 저장합니다.", Margin = new Thickness(0, 0, 16, 0) };
        download.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        var downloadIcon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("I.Download"),
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 4, 0),
        };
        downloadIcon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
        var downloadContent = new StackPanel { Orientation = Orientation.Horizontal };
        downloadContent.Children.Add(downloadIcon);
        downloadContent.Children.Add(new TextBlock { Text = "파일로 저장…", VerticalAlignment = VerticalAlignment.Center });
        download.Content = downloadContent;
        download.Click += (_, _) => SaveLatestToFile(w);
        links.Children.Add(download);

        var copy = new Button { Content = "링크 복사", ToolTip = "다운로드 주소를 복사합니다 (동료에게 전달할 때).", Margin = new Thickness(0, 0, 16, 0) };
        copy.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(Updater.DownloadUrl);
                copy.Content = "복사됨";
                SetStatus($"다운로드 주소를 복사했습니다: {Updater.DownloadUrl}");
            }
            catch (Exception)
            {
                copy.Content = "복사 실패 · 다시 시도";   // 다른 프로그램이 클립보드를 쓰는 중
            }
        };
        copy.MouseLeave += (_, _) => copy.Content = "링크 복사";
        links.Children.Add(copy);

        var browser = new Button { Content = "브라우저로 받기", ToolTip = Updater.DownloadUrl, Margin = new Thickness(0, 0, 16, 0) };
        browser.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        browser.Click += (_, _) => Updater.OpenPage(Updater.DownloadUrl);
        links.Children.Add(browser);

        var page = new Button { Content = "릴리스 페이지 (변경 내용)" };
        page.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        page.Click += (_, _) => Updater.OpenPage(_updRelease?.PageUrl ?? Updater.ReleasesPage);
        links.Children.Add(page);
        root.Children.Add(links);

        var footer = new DockPanel { Margin = new Thickness(24), LastChildFill = false };
        var hint = new TextBlock { Text = "Esc  닫기", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        DockPanel.SetDock(hint, Dock.Left);
        footer.Children.Add(hint);
        var close = new Button { Content = "닫기", MinWidth = 88, Padding = new Thickness(16, 0, 16, 0), IsCancel = true };
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);

        var outer = new StackPanel();
        outer.Children.Add(root);
        outer.Children.Add(footer);
        w.Content = outer;

        // 상태별 문구 · 버튼
        Action onPrimary = () => { }, onSecondary = () => { };
        primary.Click += (_, _) => onPrimary();
        secondary.Click += (_, _) => onSecondary();

        void Refresh()
        {
            bar.Visibility = Visibility.Collapsed;
            primary.Visibility = Visibility.Visible;
            primary.IsEnabled = true;
            primary.ToolTip = null;
            secondary.Visibility = Visibility.Collapsed;

            if (_updChecking)
            {
                status.Text = "새 버전을 확인하는 중…";
                primary.Content = "확인 중";
                primary.IsEnabled = false;
            }
            else if (_updState == UpdateState.Downloading && _updRelease != null)
            {
                status.Text = $"{_updRelease.Version.ToString(3)} 버전 받는 중 {_updProgress:0}%";
                bar.Visibility = Visibility.Visible;
                bar.Value = _updProgress;
                primary.Visibility = Visibility.Collapsed;
                secondary.Visibility = Visibility.Visible;
                secondary.Content = "받기 취소";
                onSecondary = () => _updCts?.Cancel();
            }
            else if (_updState == UpdateState.Available && _updRelease != null)
            {
                ReleaseInfo r = _updRelease;
                status.Text = $"새 버전 {r.Version.ToString(3)} · {SizeText(r)} · {r.Published.LocalDateTime:yyyy-MM-dd} 배포";
                primary.Content = "변경 내용 · 받기";
                onPrimary = () => ShowUpdateOffer(r, w);
            }
            else if (_updState == UpdateState.Ready && _updPending != null)
            {
                status.Text = $"{_updPending.Version.ToString(3)} 버전 준비됨 · 프로그램을 닫을 때 바뀝니다." +
                              (_dirty ? " 지금 바꾸려면 먼저 저장하세요." : "");
                primary.Content = "지금 다시 시작";
                primary.IsEnabled = !_dirty;
                primary.ToolTip = _dirty ? "저장하지 않은 변경이 있어 다시 시작할 수 없습니다." : null;
                onPrimary = RestartWithUpdate;
            }
            else
            {
                status.Text = _updCheckError != null ? $"확인하지 못했습니다 · {_updCheckError} 아래 '브라우저로 받기'로 받을 수 있습니다."
                    : _updCheckedAt is DateTime t ? $"최신 버전입니다 ({t:HH:mm} 확인)"
                    : Updater.CanSelfUpdate ? "아직 확인하지 않았습니다."
                    : "로컬 빌드는 자동으로 업데이트하지 않습니다. 정식 버전은 아래 '파일로 저장'으로 받으세요.";
                primary.Content = "업데이트 확인";
                onPrimary = async () => await CheckForUpdateAsync(manual: true);
            }

            string? old = Updater.CanSelfUpdate ? Updater.OldVersionText : null;
            rollback.Visibility = old != null ? Visibility.Visible : Visibility.Collapsed;
            rollback.Content = $"이전 버전({old})으로 되돌리기";
            rollback.IsEnabled = !_dirty;
            rollback.ToolTip = _dirty ? "저장하지 않은 변경이 있어 되돌릴 수 없습니다." : "이 버전에 문제가 있을 때 바로 전 버전으로 돌아갑니다.";
        }

        _about = w;
        _aboutRefresh = Refresh;
        w.Closed += (_, _) =>
        {
            _about = null;
            _aboutRefresh = null;
        };
        Refresh();
        w.Loaded += (_, _) => primary.Focus();
        w.Show();
    }
}
