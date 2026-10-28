using System;
using System.Collections.Generic;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// The editor's undo and redo (BL-229 increment 3): whole-graph snapshots, one per change. A change carrying the same key
    /// as the one before it, within <see cref="CoalesceWindow"/>, REPLACES it - typing in one field is one step to undo, not one
    /// per key. A new change after an undo drops what could have been redone. At most <see cref="Capacity"/> steps are kept.
    /// </summary>
    public sealed class EditHistory
    {
        public const int Capacity = 100;

        public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1.5);

        private readonly List<string> _undo = new();
        private readonly List<string> _redo = new();
        private string _current;
        private string _lastKey;
        private DateTime _lastAt;

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        /// <summary>A loaded or saved-and-reloaded graph: nothing before it to go back to.</summary>
        public void Reset(string state)
        {
            _undo.Clear();
            _redo.Clear();
            _current = state;
            _lastKey = null;
        }

        /// <summary>The state after a change; false when nothing changed.</summary>
        public bool Record(string state, string coalesceKey, DateTime now)
        {
            if (string.Equals(state, _current, StringComparison.Ordinal))
            {
                return false;
            }

            var coalesce = coalesceKey is not null && coalesceKey == _lastKey && now - _lastAt <= CoalesceWindow;
            if (!coalesce && _current is not null)
            {
                _undo.Add(_current);
                if (_undo.Count > Capacity)
                {
                    _undo.RemoveAt(0);
                }
            }

            _redo.Clear();
            _current = state;
            _lastKey = coalesceKey;
            _lastAt = now;
            return true;
        }

        /// <summary>The state to show after undoing the last change; null when there is none.</summary>
        public string Undo()
        {
            if (_undo.Count == 0)
            {
                return null;
            }

            _redo.Add(_current);
            _current = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _lastKey = null;
            return _current;
        }

        /// <summary>The state to show after redoing the last undone change; null when there is none.</summary>
        public string Redo()
        {
            if (_redo.Count == 0)
            {
                return null;
            }

            _undo.Add(_current);
            _current = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _lastKey = null;
            return _current;
        }
    }
}
