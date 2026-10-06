using System.Threading;
using System.Windows;
using AmrMapEditor.Core;

namespace AmrMapEditor;

/// <summary>
/// 오래 걸릴 수 있는 계산의 진행 표시 · 취소, 그리고 '현재 맵에 반영' 되돌리기 연동
/// </summary>
public partial class MainWindow
{
    private CancellationTokenSource? _busyCts;
    private bool _busy;               // 편집 잠금 (맵 클릭 · 되돌리기 · 열기 막음)
    private bool _busyCancellable;

    /// <summary>
    /// 작업 시작: 상태바에 진행 표시 (+ 취소). lockEditing이면 결과가 맵 내용에 의존하므로 끝날 때까지 편집을 막음.
    /// 화면 이동 · 확대는 계속 됨
    /// </summary>
    private CancellationToken BeginBusy(string label, bool lockEditing, bool cancellable, bool showProgress)
    {
        _busyCts = new CancellationTokenSource();
        _busyCancellable = cancellable;
        _busy = lockEditing;
        if (lockEditing) InspectorBody.IsEnabled = false;
        SetStatus(label + "…");
        StatusProgress.Value = 0;
        StatusProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        StatusCancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        return _busyCts.Token;
    }

    private void ReportBusy(double percent) => StatusProgress.Value = percent;

    private void EndBusy()
    {
        _busyCts?.Dispose();
        _busyCts = null;
        _busy = false;
        _busyCancellable = false;
        InspectorBody.IsEnabled = _map != null;
        StatusProgress.Visibility = Visibility.Collapsed;
        StatusCancelButton.Visibility = Visibility.Collapsed;
    }

    private void OnCancelBusy(object sender, RoutedEventArgs e)
    {
        if (_busyCancellable) _busyCts?.Cancel();
    }

    /// <summary>편집이 잠긴 작업 중이면 안내하고 true</summary>
    private bool BusyBlocked()
    {
        if (!_busy) return false;
        SetStatus("진행 중인 작업이 끝난 뒤 다시 시도하세요." + (_busyCancellable ? "  Esc = 취소" : ""));
        return true;
    }

    // ───────────── '현재 맵에 반영' 되돌리기 ─────────────

    /// <summary>반영 작업과 함께 지정한 기준 맵 · 업데이트 영역 (Ctrl+Z 한 번에 같이 되돌림)</summary>
    private sealed record ApplyUndo(ChangeSet Change, MapImage? Reference, string? ReferenceName, PixelRegion Area);

    private ApplyUndo? _applyUndo;
    private ChangeSet? _lastCommit;

    private void RevertApplyContext()
    {
        ApplyUndo a = _applyUndo!;
        if (a.Reference != null && ReferenceEquals(_reference, a.Reference))
        {
            CloseReference();   // 반영하며 만든 기준 맵 · 업데이트 영역까지
        }
        else if (_updateAreas.Remove(a.Area))
        {
            UpdateAreasChanged();   // 사용자가 연 기준 맵은 그대로 두고 반영 범위만 뺌
        }
        if (_second != null)
        {
            SecondShowOverlay.IsChecked = true;
            SegSecond.IsChecked = true;
        }
        SetStatus("맞출 맵 반영을 되돌렸습니다 (기준 맵 · 업데이트 영역 포함).");
    }

    private void RedoApplyContext()
    {
        ApplyUndo a = _applyUndo!;
        if (a.Reference != null && _reference == null)
            SetReference(a.Reference, a.ReferenceName ?? "반영 전", "맞출 맵 반영 전 상태");
        if (!_updateAreas.Contains(a.Area))
        {
            _updateAreas.Add(a.Area);
            UpdateAreasChanged();
        }
        if (_second != null) SecondShowHidden.IsChecked = true;
        SegUpdate.IsChecked = true;
        SetStatus("맞출 맵 반영을 다시 적용했습니다.");
    }
}
