using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using AmrMapEditor.Controls;
using AmrMapEditor.Core;

namespace AmrMapEditor;

/// <summary>보호 영역: 모든 편집에서 제외되는 영역 (&lt;맵&gt;.protect.json)</summary>
public partial class MainWindow
{
    private readonly ObservableCollection<NamedRegion> _protect = new();
    private bool[]? _protectMask;

    private void LoadProtect()
    {
        _protect.Clear();
        if (_path != null && _map != null)
        {
            try
            {
                foreach (NamedRegion r in RegionStore.Load(_path, _map.Bounds)) _protect.Add(r);
            }
            catch (Exception ex)
            {
                SetStatus($"보호 영역 파일을 읽지 못했습니다: {ex.Message}");
            }
        }
        ApplyProtect();
    }

    /// <summary>보호 영역 파일 저장. 기울기 보정 후(좌표 변경)에는 맵 저장 시에만 저장</summary>
    private void SaveProtect(bool force = false)
    {
        if (_path == null || (_metaChanged && !force)) return;
        try
        {
            RegionStore.Save(_path, _protect);
        }
        catch (Exception ex)
        {
            SetStatus($"보호 영역 저장 실패: {ex.Message}");
        }
    }

    private void ApplyProtect()
    {
        _protectMask = _map != null && _protect.Count > 0
            ? PixelRegion.BuildMask(_protect.Select(p => p.Region), _map.Width, _map.Height)
            : null;
        if (_tracker != null) _tracker.Protect = ProtectEnableCheck.IsChecked == true ? _protectMask : null;
        ProtectList.Visibility = _protect.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshRegions();
    }

    private void OnAddProtect(object sender, RoutedEventArgs e)
    {
        if (_map == null) return;
        if (_selection == null)
        {
            SetStatus("보호할 곳을 사각형(M)이나 폴리곤(P)으로 먼저 선택하세요.");
            return;
        }
        string name = ProtectNameBox.Text.Trim();
        if (name.Length == 0) name = $"보호 {_protect.Count + 1}";
        _protect.Add(new NamedRegion(name, _selection));
        SetSelection(null);
        ApplyProtect();
        SaveProtect();
        SetStatus($"보호 영역 추가: {name}");
    }

    private void OnRemoveProtect(object sender, RoutedEventArgs e)
    {
        if (ProtectList.SelectedItem is not NamedRegion nr)
        {
            SetStatus("삭제할 보호 영역을 목록에서 선택하세요.");
            return;
        }
        _protect.Remove(nr);
        ApplyProtect();
        SaveProtect();
        SetStatus($"보호 영역 삭제: {nr.Name}");
    }

    private void OnProtectToggle(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyProtect();
    }

    /// <summary>보호 영역에 걸친 후보는 목록에서 제외</summary>
    private bool TouchesProtect(Blob b)
    {
        if (_protectMask == null || ProtectEnableCheck.IsChecked != true || b.Pixels == null) return false;
        foreach (int i in b.Pixels)
            if (_protectMask[i]) return true;
        return false;
    }

    /// <summary>업데이트 영역 / 보호 영역 화면 표시 갱신</summary>
    private void RefreshRegions()
    {
        var list = new List<RegionOverlay>();
        foreach (PixelRegion r in _updateAreas) list.Add(new RegionOverlay(r, RegionKind.Update));
        if (ProtectShowCheck.IsChecked == true)
            foreach (NamedRegion p in _protect) list.Add(new RegionOverlay(p.Region, RegionKind.Protect));
        MapViewer.Regions = list;
    }
}
