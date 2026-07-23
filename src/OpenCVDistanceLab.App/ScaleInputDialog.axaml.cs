using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Globalization;

namespace OpenCVDistanceLab.App;

public sealed partial class ScaleInputDialog : Window
{
    public double ActualLength { get; private set; }
    public string Unit { get; private set; }

    public ScaleInputDialog() : this(1, 100, "μm") { }

    public ScaleInputDialog(double pixelLength, double defaultLength, string defaultUnit)
    {
        InitializeComponent();
        App.ApplyUnifiedWindowStyle(this);
        PixelLengthText.Text = $"图中线段长度：{pixelLength:F3} px";
        LengthBox.Text = defaultLength.ToString("G", CultureInfo.InvariantCulture);
        UnitBox.Text = defaultUnit;
        Unit = defaultUnit;
        Opened += (_, _) =>
        {
            LengthBox.Focus();
            LengthBox.SelectAll();
        };
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(LengthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || value <= 0 || string.IsNullOrWhiteSpace(UnitBox.Text))
        {
            await MessageDialog.Show(this, "参数错误", "请输入大于零的实际长度和非空单位。");
            return;
        }
        ActualLength = value;
        Unit = UnitBox.Text.Trim();
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
