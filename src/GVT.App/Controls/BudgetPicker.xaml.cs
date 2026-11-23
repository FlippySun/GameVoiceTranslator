using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GVT.App.Controls;

/// <summary>单次花费上限选择器。Value：元，0 = 不限。用户改了就触发 Changed。</summary>
public partial class BudgetPicker : UserControl
{
    private double _value = 5;

    public event Action<double>? Changed;

    public BudgetPicker() => InitializeComponent();

    public double Value
    {
        get => _value;
        set { _value = value; Sync(); }
    }

    private void Sync()
    {
        foreach (RadioButton r in Presets.Children)
            r.IsChecked = double.Parse((string)r.Tag, CultureInfo.InvariantCulture) == _value;
        bool preset = Presets.Children.OfType<RadioButton>().Any(r => r.IsChecked == true);
        CustomBox.Text = preset ? "" : _value.ToString("0.##", CultureInfo.InvariantCulture);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) Commit(double.Parse(tag, CultureInfo.InvariantCulture));
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyCustom();

    private void CustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCustom();
    }

    private void ApplyCustom()
    {
        var s = CustomBox.Text.Trim().TrimStart('¥', '￥');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0.5 || v > 1000)
        {
            ErrorText.Text = "请输入 0.5 到 1000 之间的金额";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Commit(Math.Round(v, 2));
    }

    private void Commit(double v)
    {
        _value = v;
        Sync();
        Changed?.Invoke(v);
    }

    public static string Describe(double v) => v <= 0 ? "不限" : $"¥{v:0.##}";
}
