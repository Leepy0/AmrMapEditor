using System;
using System.Collections.Generic;

namespace AmrMapEditor.Core;

/// <summary>한 번의 편집 작업으로 바뀐 픽셀 목록 (Undo 단위)</summary>
public sealed class ChangeSet
{
    public ChangeSet(string name, int[] indices, byte[] oldValues, byte[] newValues, IntRect bounds)
    {
        Name = name;
        Indices = indices;
        OldValues = oldValues;
        NewValues = newValues;
        Bounds = bounds;
    }

    public string Name { get; }
    public int[] Indices { get; }
    public byte[] OldValues { get; }
    public byte[] NewValues { get; }
    public IntRect Bounds { get; }
    public int Count => Indices.Length;

    public void ApplyOld(MapImage map)
    {
        for (int k = 0; k < Indices.Length; k++) map.Data[Indices[k]] = OldValues[k];
    }

    public void ApplyNew(MapImage map)
    {
        for (int k = 0; k < Indices.Length; k++) map.Data[Indices[k]] = NewValues[k];
    }
}

/// <summary>
/// 모든 픽셀 수정은 이 클래스를 거침.
/// 픽셀별 최초 원본값만 기록하고, Clip이 있으면 영역 밖 수정은 무시.
/// </summary>
public sealed class EditTracker
{
    private readonly MapImage _map;
    private readonly bool[] _touched;
    private readonly List<int> _indices = new();
    private readonly List<byte> _oldValues = new();

    // 전체 변경 범위
    private int _minX, _minY, _maxX, _maxY;
    // 마지막 TakeDirty 이후 변경 범위 (드래그 중 화면 갱신용)
    private int _dMinX, _dMinY, _dMaxX, _dMaxY;

    public EditTracker(MapImage map)
    {
        _map = map;
        _touched = new bool[map.Width * map.Height];
        ResetBounds();
        ResetDirty();
    }

    public MapImage Map => _map;

    /// <summary>편집 허용 영역 (null이면 전체)</summary>
    public PixelRegion? Clip { get; set; }

    /// <summary>보호 마스크 (true인 픽셀은 수정 불가, 전체 이미지 크기)</summary>
    public bool[]? Protect { get; set; }

    /// <summary>현재 작업에서 보호 영역 때문에 막힌 픽셀 수 (Begin 시 초기화)</summary>
    public int BlockedCount { get; private set; }
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; }
    public int PendingCount => _indices.Count;

    public void Begin(string name)
    {
        Name = name;
        IsActive = true;
        BlockedCount = 0;
    }

    public bool Set(int x, int y, byte value)
    {
        if ((uint)x >= (uint)_map.Width || (uint)y >= (uint)_map.Height) return false;
        if (Clip != null && !Clip.Contains(x, y)) return false;

        int i = y * _map.Width + x;
        byte cur = _map.Data[i];
        if (cur == value) return false;
        if (Protect != null && Protect[i])
        {
            BlockedCount++;
            return false;
        }

        if (!_touched[i])
        {
            _touched[i] = true;
            _indices.Add(i);
            _oldValues.Add(cur);
        }
        _map.Data[i] = value;

        if (x < _minX) _minX = x;
        if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y;
        if (y > _maxY) _maxY = y;
        if (x < _dMinX) _dMinX = x;
        if (x > _dMaxX) _dMaxX = x;
        if (y < _dMinY) _dMinY = y;
        if (y > _dMaxY) _dMaxY = y;
        return true;
    }

    public bool SetIndex(int index, byte value) => Set(index % _map.Width, index / _map.Width, value);

    /// <summary>마지막 호출 이후 바뀐 영역 반환 후 초기화</summary>
    public IntRect TakeDirty()
    {
        if (_dMaxX < _dMinX) return IntRect.Empty;
        var r = new IntRect(_dMinX, _dMinY, _dMaxX - _dMinX + 1, _dMaxY - _dMinY + 1);
        ResetDirty();
        return r;
    }

    /// <summary>작업 확정. 실제 변경이 없으면 null</summary>
    public ChangeSet? Commit()
    {
        IsActive = false;
        int n = _indices.Count;
        if (n == 0)
        {
            ResetBounds();
            ResetDirty();
            return null;
        }

        var idx = new List<int>(n);
        var oldV = new List<byte>(n);
        var newV = new List<byte>(n);
        for (int k = 0; k < n; k++)
        {
            int i = _indices[k];
            _touched[i] = false;
            byte now = _map.Data[i];
            if (now == _oldValues[k]) continue; // 드래그 중 원래 값으로 되돌아온 픽셀 제외
            idx.Add(i);
            oldV.Add(_oldValues[k]);
            newV.Add(now);
        }

        var bounds = new IntRect(_minX, _minY, _maxX - _minX + 1, _maxY - _minY + 1);
        _indices.Clear();
        _oldValues.Clear();
        ResetBounds();
        ResetDirty();

        return idx.Count == 0 ? null : new ChangeSet(Name, idx.ToArray(), oldV.ToArray(), newV.ToArray(), bounds);
    }

    private void ResetBounds()
    {
        _minX = _minY = int.MaxValue;
        _maxX = _maxY = int.MinValue;
    }

    private void ResetDirty()
    {
        _dMinX = _dMinY = int.MaxValue;
        _dMaxX = _dMaxY = int.MinValue;
    }
}

public sealed class UndoStack
{
    private readonly List<ChangeSet> _undo = new();
    private readonly List<ChangeSet> _redo = new();

    public int MaxSteps { get; set; } = 100;

    /// <summary>Undo 이력 전체 픽셀 수 상한 (메모리 보호)</summary>
    public long MaxPixels { get; set; } = 60_000_000;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => CanUndo ? _undo[^1].Name : null;
    public string? RedoName => CanRedo ? _redo[^1].Name : null;

    public void Push(ChangeSet cs)
    {
        _undo.Add(cs);
        _redo.Clear();
        Trim();
    }

    public ChangeSet? Undo(MapImage map)
    {
        if (!CanUndo) return null;
        ChangeSet cs = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        cs.ApplyOld(map);
        _redo.Add(cs);
        return cs;
    }

    public ChangeSet? Redo(MapImage map)
    {
        if (!CanRedo) return null;
        ChangeSet cs = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        cs.ApplyNew(map);
        _undo.Add(cs);
        return cs;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>새 작업이 시작될 때 다시 실행 목록 비움</summary>
    public void ClearRedo() => _redo.Clear();

    private void Trim()
    {
        long total = 0;
        foreach (ChangeSet c in _undo) total += c.Count;
        while (_undo.Count > 1 && (_undo.Count > MaxSteps || total > MaxPixels))
        {
            total -= _undo[0].Count;
            _undo.RemoveAt(0);
        }
    }
}
