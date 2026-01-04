namespace SkyrimCraftingTool;

using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
public partial class SharedInfoPanel : UserControl
{
    public SharedInfoPanel()
    {
        InitializeComponent();
    }

    public IGameRecord Record
    {
        get => (IGameRecord)GetValue(RecordProperty);
        set => SetValue(RecordProperty, value);
    }

    public static readonly DependencyProperty RecordProperty =
        DependencyProperty.Register(
            nameof(Record),
            typeof(IGameRecord),
            typeof(SharedInfoPanel),
            new PropertyMetadata(null, OnRecordChanged));

    private static void OnRecordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SharedInfoPanel)d;
        control.DataContext = new SharedInfoPanelVM((IGameRecord)e.NewValue);
    }
}
