using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenCVDistanceLab.Core;
using OpenCVDistanceLab.Interop;

namespace OpenCVDistanceLab.App;

public sealed partial class MainWindow
{
    private void NativeOpenFolderMenu_Click(object? sender, EventArgs e) =>
        OpenFolderMenu_Click(sender, new RoutedEventArgs());

    private void NativeLoadCsvMenu_Click(object? sender, EventArgs e) =>
        LoadCsvMenu_Click(sender, new RoutedEventArgs());

    private void NativeSaveSettingsMenu_Click(object? sender, EventArgs e) =>
        SaveSettingsMenu_Click(sender, new RoutedEventArgs());

    private void NativeLoadSettingsMenu_Click(object? sender, EventArgs e) =>
        LoadSettingsMenu_Click(sender, new RoutedEventArgs());

    private void NativeSelfTestMenu_Click(object? sender, EventArgs e) =>
        SelfTestMenu_Click(sender, new RoutedEventArgs());

    private void NativeSystemThemeMenu_Click(object? sender, EventArgs e) => SetThemeFromMenu("System");
    private void NativeLightThemeMenu_Click(object? sender, EventArgs e) => SetThemeFromMenu("Light");
    private void NativeDarkThemeMenu_Click(object? sender, EventArgs e) => SetThemeFromMenu("Dark");

    private void PopulatePlotFields(IEnumerable<string> fields, string x, string y)
    {
        string[] values = fields.ToArray();
        XFieldCombo.ItemsSource = values;
        YFieldCombo.ItemsSource = values;
        XFieldCombo.SelectedItem = values.Contains(x) ? x : values.FirstOrDefault();
        YFieldCombo.SelectedItem = values.Contains(y) ? y : values.Skip(1).FirstOrDefault() ?? values.FirstOrDefault();
    }

    private async void LoadCsvMenu_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择计算完成的 CSV",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] }]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            (List<PlotRecord> records, List<string> fields) = CsvPlotReader.Read(path);
            _csvRecords = records;
            _showingCsv = true;
            PopulatePlotFields(fields,
                fields.Contains("frame_index") ? "frame_index" : fields[0],
                fields.Contains("distance_actual") ? "distance_actual" : fields[Math.Min(1, fields.Count - 1)]);
            PlotSourceText.Text = $"数据源：CSV {Path.GetFileName(path)}（{records.Count:N0} 行）";
            RefreshPlot();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await MessageDialog.Show(this, "CSV 读取失败", error.Message);
        }
    }

    private void ShowLiveButton_Click(object? sender, RoutedEventArgs e) => ShowLiveData(false);

    private void ShowLiveData(bool resetAxes)
    {
        _showingCsv = false;
        if (resetAxes || XFieldCombo.SelectedItem is not string x || !DataFields.Plot.Contains(x) ||
            YFieldCombo.SelectedItem is not string y || !DataFields.Plot.Contains(y))
            PopulatePlotFields(DataFields.Plot, "frame_index", "distance_actual");
        PlotSourceText.Text = $"数据源：实时追踪（{_liveRecords.Count:N0} 条）";
        RefreshPlot();
    }

    private void PlotField_Changed(object? sender, SelectionChangedEventArgs e) => RefreshPlot();

    private void RefreshPlot()
    {
        if (LivePlot is null || XFieldCombo?.SelectedItem is not string x || YFieldCombo?.SelectedItem is not string y) return;
        LivePlot.SetData(_showingCsv ? _csvRecords : _liveRecords, x, y);
    }

    private async void SaveSettingsMenu_Click(object? sender, RoutedEventArgs e)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 OpenCV Distance Lab 参数",
            SuggestedFileName = "distance-lab.ocvsettings.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("OpenCV 设置") { Patterns = ["*.ocvsettings.json", "*.json"] }],
            ShowOverwritePrompt = true
        });
        string? path = file?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            CaptureProductSettings().Save(path);
            SetStatus($"参数已保存：{Path.GetFileName(path)}", false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await MessageDialog.Show(this, "参数保存失败", error.Message);
        }
    }

    private async void LoadSettingsMenu_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "读取 OpenCV Distance Lab 参数",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenCV 设置") { Patterns = ["*.ocvsettings.json", "*.json"] }]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            ApplyProductSettings(ProductSettings.Load(path), true);
            SavePreferences();
            SetStatus($"已读取参数：{Path.GetFileName(path)}", false);
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            await MessageDialog.Show(this, "参数读取失败", error.Message);
        }
    }

    private void ThemeMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        SetThemeFromMenu(item.Tag?.ToString() ?? "System");
    }

    private void SetThemeFromMenu(string theme)
    {
        _preferences.Settings.Theme = theme;
        ApplyTheme(theme);
        SavePreferences();
        DrawOverlay();
        RefreshPlot();
    }

    private async void SelfTestMenu_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在运行跨平台原生自检…", false);
            await Task.Run(NativeBackend.SelfTest);
            await MessageDialog.Show(this, "自检通过", "OpenCV 原生 ABI、亚像素模板匹配和图像缓冲区检查均已通过。");
            SetStatus("原生自检通过。", false);
        }
        catch (Exception error)
        {
            SetStatus("原生自检失败。", true);
            await MessageDialog.Show(this, "自检失败", error.Message);
        }
    }

    internal Task ShowAboutAsync() =>
        MessageDialog.Show(this, "关于 OpenCV Distance Lab",
            "OpenCV Distance Lab 2.0.6\nAvalonia 跨平台界面 + C++20/OpenCV 原生追踪\n支持 Windows x64、macOS Apple Silicon 和 Intel。");

    private async void AboutMenu_Click(object? sender, RoutedEventArgs e) =>
        await ShowAboutAsync();

    private void ExitMenu_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_running && !_closingConfirmed)
        {
            e.Cancel = true;
            if (!await MessageDialog.Confirm(this, "确认退出", "追踪仍在进行。是否安全停止、保留已写入结果并退出？")) return;
            _closingConfirmed = true;
            _paused = false;
            _trackingCancellation?.Cancel();
            while (_running) await Task.Delay(50);
            Close();
            return;
        }
        SavePreferences();
        _trackingCancellation?.Cancel();
        _tracker?.Dispose();
        DisposePendingSnapshot();
        DisposeBitmaps();
    }
}
