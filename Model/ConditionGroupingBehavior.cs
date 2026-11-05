using SkyrimCraftingTool.ViewModel;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SkyrimCraftingTool.Model
{
    // Keeps the OR-group drawing of a condition list current: set
    // model:ConditionGroupingBehavior.IsEnabled="True" on the ItemsControl and every row's
    // GroupPosition / Connector / IsDanglingOr follows the list (ConditionGroups.Apply).
    //
    // A view behavior rather than code in the owners, because there are four of them (ItemNodeVM,
    // MultiSelectDetailVM, BreakdownRecipeVM, twice each) and the grouping is the same for all - it
    // only depends on the list and on each row's OR flag.
    //
    // Subscribes while the control is loaded, never longer: the condition lists outlive the views
    // (a recipe's list stays when another item is opened), and a lingering handler would keep every
    // view that ever showed it alive.
    public static class ConditionGroupingBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ConditionGroupingBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

        private static readonly DependencyProperty TrackerProperty =
            DependencyProperty.RegisterAttached("Tracker", typeof(Tracker), typeof(ConditionGroupingBehavior));

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ItemsControl ic) return;

            if ((bool)e.NewValue)
            {
                var tracker = new Tracker(ic);
                ic.SetValue(TrackerProperty, tracker);
                ic.Loaded += tracker.OnLoaded;
                ic.Unloaded += tracker.OnUnloaded;
                if (ic.IsLoaded) tracker.Attach();
            }
            else if (ic.GetValue(TrackerProperty) is Tracker tracker)
            {
                tracker.Detach();
                ic.Loaded -= tracker.OnLoaded;
                ic.Unloaded -= tracker.OnUnloaded;
                ic.ClearValue(TrackerProperty);
            }
        }

        private sealed class Tracker
        {
            private readonly ItemsControl _ic;
            private INotifyCollectionChanged? _source;
            private readonly List<BaseConditionViewModel> _rows = new();
            private readonly DependencyPropertyDescriptor _itemsSourceDescriptor =
                DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl));

            public Tracker(ItemsControl ic) => _ic = ic;

            public void OnLoaded(object sender, RoutedEventArgs e) => Attach();
            public void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

            public void Attach()
            {
                Detach();
                _itemsSourceDescriptor.AddValueChanged(_ic, OnItemsSourceChanged);
                Hook();
            }

            public void Detach()
            {
                _itemsSourceDescriptor.RemoveValueChanged(_ic, OnItemsSourceChanged);
                Unhook();
            }

            private void OnItemsSourceChanged(object? sender, System.EventArgs e)
            {
                Unhook();
                Hook();
            }

            private void Hook()
            {
                _source = _ic.ItemsSource as INotifyCollectionChanged;
                if (_source != null) _source.CollectionChanged += OnCollectionChanged;
                Refresh();
            }

            private void Unhook()
            {
                if (_source != null) _source.CollectionChanged -= OnCollectionChanged;
                _source = null;
                foreach (var r in _rows) r.PropertyChanged -= OnRowChanged;
                _rows.Clear();
            }

            private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

            // Only the flag matters to the grouping; everything else a row raises is ignored.
            private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName is nameof(BaseConditionViewModel.HasOrFlag)
                    or nameof(BaseConditionViewModel.IsOr))
                    Apply();
            }

            private void Refresh()
            {
                foreach (var r in _rows) r.PropertyChanged -= OnRowChanged;
                _rows.Clear();
                if (_ic.ItemsSource is IEnumerable items)
                    _rows.AddRange(items.OfType<BaseConditionViewModel>());
                foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
                Apply();
            }

            private void Apply() => ConditionGroups.Apply(_rows);
        }
    }
}
