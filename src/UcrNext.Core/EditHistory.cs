namespace UcrNext.Core;

/// <summary>
/// Undo/redo of whole editing states. Each state is the serialized profile list plus a key
/// that decides whether two states differ (changes that aren't edits, like ticking a profile
/// on or off, don't create an undo step).
/// </summary>
public sealed class EditHistory
{
    private readonly List<(string State, string Key)> _items = new();
    private int _index = -1;

    public EditHistory(int limit = 200) => Limit = limit;

    public int Limit { get; }
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _items.Count - 1;

    /// <summary>Forget everything; <paramref name="state"/> becomes the only entry.</summary>
    public void Reset(string state, string key)
    {
        _items.Clear();
        _items.Add((state, key));
        _index = 0;
    }

    /// <summary>Adds a step if the key changed (drops any redo steps). Returns true if a step was added.</summary>
    public bool Record(string state, string key)
    {
        if (_index >= 0 && _items[_index].Key == key)
        {
            _items[_index] = (state, key); // same edit content: just keep the latest state
            return false;
        }
        if (_index < _items.Count - 1) _items.RemoveRange(_index + 1, _items.Count - _index - 1);
        _items.Add((state, key));
        if (_items.Count > Limit) _items.RemoveAt(0);
        _index = _items.Count - 1;
        return true;
    }

    public string? Undo() => CanUndo ? _items[--_index].State : null;
    public string? Redo() => CanRedo ? _items[++_index].State : null;
}
