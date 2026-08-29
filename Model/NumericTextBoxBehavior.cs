using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfDataObject = System.Windows.DataObject;

namespace SkyrimCraftingTool.Model
{
    public enum NumericMode
    {
        None,
        Integer,
        Decimal
    }

    // Attached behavior that restricts a TextBox to numeric input (typed or pasted), so fields bound
    // to int/float properties (Cost, Weight, ArmorRating, ...) can't end up with text WPF's binding
    // would just silently reject on LostFocus. Set NumericTextBoxBehavior.Mode="Integer" for int-backed
    // fields (Cost/Value, Damage, RequiredLevel, Stage, ComparisonValue) or "Decimal" for float-backed
    // ones (Weight, ArmorRating, Speed, Reach, Stagger).
    //
    // On LostFocus the value is additionally parsed and clamped into [Min, Max] (defaults 0 ..
    // no-upper-bound). This is the safety net the input regex can't cover: an empty box (which would
    // otherwise leave the bound property stale with a red validation border), a pasted overflow, or
    // a value pushed in programmatically. Set NumericTextBoxBehavior.Max on a field where a tighter
    // ceiling makes sense (e.g. weapon Speed).
    //
    // Uses a WpfTextBox alias because this project also references System.Windows.Forms (for the
    // folder-picker dialog), which makes the bare "TextBox" name ambiguous.
    public static class NumericTextBoxBehavior
    {
        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.RegisterAttached(
                "Mode",
                typeof(NumericMode),
                typeof(NumericTextBoxBehavior),
                new PropertyMetadata(NumericMode.None, OnModeChanged));

        public static void SetMode(DependencyObject element, NumericMode value) =>
            element.SetValue(ModeProperty, value);

        public static NumericMode GetMode(DependencyObject element) =>
            (NumericMode)element.GetValue(ModeProperty);

        public static readonly DependencyProperty MinProperty =
            DependencyProperty.RegisterAttached(
                "Min", typeof(double), typeof(NumericTextBoxBehavior), new PropertyMetadata(0.0));

        public static void SetMin(DependencyObject element, double value) => element.SetValue(MinProperty, value);
        public static double GetMin(DependencyObject element) => (double)element.GetValue(MinProperty);

        public static readonly DependencyProperty MaxProperty =
            DependencyProperty.RegisterAttached(
                "Max", typeof(double), typeof(NumericTextBoxBehavior), new PropertyMetadata(double.MaxValue));

        public static void SetMax(DependencyObject element, double value) => element.SetValue(MaxProperty, value);
        public static double GetMax(DependencyObject element) => (double)element.GetValue(MaxProperty);

        // No leading '-': every field this is used on (Cost, Weight, ArmorRating, Damage, Speed,
        // Reach, Stagger, RequiredLevel, Stage, ComparisonValue) is a non-negative game stat.
        private static readonly Regex IntegerRegex = new(@"^\d*$", RegexOptions.Compiled);
        private static readonly Regex DecimalRegex = new(@"^\d*(\.\d*)?$", RegexOptions.Compiled);

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not WpfTextBox textBox) return;
            if ((NumericMode)e.NewValue == NumericMode.None) return;

            textBox.PreviewTextInput -= OnPreviewTextInput;
            textBox.PreviewTextInput += OnPreviewTextInput;

            textBox.LostFocus -= OnLostFocus;
            textBox.LostFocus += OnLostFocus;

            WpfDataObject.RemovePastingHandler(textBox, OnPaste);
            WpfDataObject.AddPastingHandler(textBox, OnPaste);
        }

        private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = (WpfTextBox)sender;
            e.Handled = !IsValid(textBox, GetProposedText(textBox, e.Text));
        }

        private static void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = (WpfTextBox)sender;

            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }

            var pasted = (string)e.DataObject.GetData(typeof(string));
            if (!IsValid(textBox, GetProposedText(textBox, pasted)))
                e.CancelCommand();
        }

        private static void OnLostFocus(object sender, RoutedEventArgs e)
        {
            var textBox = (WpfTextBox)sender;
            var mode = GetMode(textBox);
            if (mode == NumericMode.None) return;

            var normalized = NormalizeAndClamp(textBox.Text, mode, GetMin(textBox), GetMax(textBox));
            if (textBox.Text == normalized) return;

            textBox.Text = normalized;
            // The bound property may already have been updated with the bad value (UpdateSourceTrigger
            // defaults to LostFocus) - force the corrected value through regardless of handler order.
            textBox.GetBindingExpression(WpfTextBox.TextProperty)?.UpdateSource();
        }

        // Parses text as a number (empty / garbage / NaN / Infinity -> Min), clamps into [Min, Max]
        // (Max is additionally capped at int.MaxValue for Integer mode), and renders it back.
        public static string NormalizeAndClamp(string? text, NumericMode mode, double min, double max)
        {
            if (mode == NumericMode.Integer && max > int.MaxValue)
                max = int.MaxValue;
            if (max < min)
                max = min;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value))
                value = min;

            value = Math.Clamp(value, min, max);

            return mode == NumericMode.Integer
                ? ((long)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);
        }

        private static string GetProposedText(WpfTextBox textBox, string input)
        {
            var text = textBox.Text;
            return text[..textBox.SelectionStart] + input + text[(textBox.SelectionStart + textBox.SelectionLength)..];
        }

        private static bool IsValid(WpfTextBox textBox, string proposedText)
        {
            var pattern = GetMode(textBox) == NumericMode.Decimal ? DecimalRegex : IntegerRegex;
            return pattern.IsMatch(proposedText);
        }
    }
}
