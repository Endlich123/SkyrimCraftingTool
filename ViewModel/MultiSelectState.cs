using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    // What a tree with Ctrl/Shift multi-select needs of the view model behind it. Implemented by
    // both sub-tab kinds of the Container/LeveledList tab; Model.MultiSelectTreeBehavior is the
    // view half, and the comments there are where the subtlety lives.
    //
    // Typed as object because the two tabs have different rows, and the behaviour hands over
    // whatever the TreeViewItem was showing - a branch as readily as a leaf. IsRow is how the view
    // model says which of the two it just got.
    public interface IMultiSelectTree
    {
        bool IsRow(object candidate);

        // A leaf was clicked, with these modifiers held.
        void HandleRowClick(object row, bool ctrl, bool shift);

        // The tree selected something the ordinary way - a branch, or arrow keys. Any leftover
        // multi-selection belongs to the gesture before this one and goes.
        void SelectSingle(object row);
    }

    // Ctrl/Shift click arithmetic for a tree: anchor, range, toggle.
    //
    // MainContentVM.HandleItemNodeClick has carried this logic for the item tree since long before
    // these tabs existed, and it is NOT reused here on purpose - that copy is welded to ItemNodeVM,
    // to the item editor's anchor and to its own per-item bookkeeping (SetItemSelected keeps
    // MainContentVM.SelectedItems and the bulk editor in step). What is worth not writing a third
    // time is the arithmetic itself, so it lives here and both new trees share it. Rewriting the
    // item tab to use this was not part of giving these tabs multi-select; if that copy ever moves
    // over, this is where it moves to.
    //
    // The row flag is set through a callback rather than an interface: the two trees mark different
    // things (OwnerRowVM.IsPicked, PlaceableRowVM.IsPicked), and one more interface for one bool
    // buys nothing.
    public sealed class MultiSelectState<T> where T : class
    {
        private readonly Func<List<T>> _rowsInScreenOrder;
        private readonly Action<T, bool> _mark;

        // Membership is asked once per row of a Shift range, so it cannot be a scan of Picked: a
        // range over a few thousand rows would be quadratic. The set and the collection are kept in
        // step by Set() below and nothing else writes to either.
        private readonly HashSet<T> _picked = new();

        private T? _anchor;

        // `picked` is for a caller that already has the collection the rest of its view binds to -
        // the item tab's SelectedItems, which its bulk editor reads. Handing it in rather than
        // mirroring it keeps ONE collection: two would be the same list twice, which is the thing
        // this class exists to stop.
        public MultiSelectState(
            Func<List<T>> rowsInScreenOrder, Action<T, bool> mark, ObservableCollection<T>? picked = null)
        {
            _rowsInScreenOrder = rowsInScreenOrder;
            _mark = mark;
            Picked = picked ?? new ObservableCollection<T>();

            foreach (var row in Picked) _picked.Add(row);
        }

        // Membership is this class's to change. A `mark` callback that also adds or removes here
        // would double every pick - the callback is for the SIDE EFFECTS of being picked.
        public ObservableCollection<T> Picked { get; }

        public void Handle(T clicked, bool ctrl, bool shift)
        {
            if (clicked == null) return;

            // Shift with nothing to span from is an ordinary click - there is no range yet.
            if (shift && _anchor != null)
            {
                var rows = _rowsInScreenOrder();
                int anchorIndex = rows.IndexOf(_anchor);
                int clickedIndex = rows.IndexOf(clicked);

                // The anchor can have been filtered out from under the selection since it was set.
                // Falling back to a plain click is better than silently selecting nothing.
                if (anchorIndex < 0 || clickedIndex < 0)
                {
                    Only(clicked);
                    return;
                }

                // Ctrl+Shift extends, Shift alone replaces - the convention every file list uses.
                if (!ctrl) ClearPicked();

                int lo = Math.Min(anchorIndex, clickedIndex);
                int hi = Math.Max(anchorIndex, clickedIndex);
                for (int i = lo; i <= hi; i++)
                    Set(rows[i], true);

                // The anchor deliberately stays put, so dragging Shift further keeps spanning from
                // the same end rather than from the last row touched.
                return;
            }

            if (ctrl)
            {
                Set(clicked, !_picked.Contains(clicked));
                _anchor = clicked;
                return;
            }

            Only(clicked);
        }

        private void Only(T clicked)
        {
            foreach (var row in Picked.ToList())
                if (!ReferenceEquals(row, clicked))
                    Set(row, false);

            Set(clicked, true);
            _anchor = clicked;
        }

        public void Clear()
        {
            ClearPicked();
            _anchor = null;
        }

        private void ClearPicked()
        {
            foreach (var row in Picked.ToList())
                Set(row, false);
        }

        private void Set(T row, bool value)
        {
            if (_picked.Contains(row) == value) return;

            if (value)
            {
                _picked.Add(row);
                Picked.Add(row);
            }
            else
            {
                _picked.Remove(row);
                Picked.Remove(row);
            }

            _mark(row, value);
        }
    }
}
