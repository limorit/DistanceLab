using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenCVDistanceLab.Core;
using OpenCVDistanceLab.Interop;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenCVDistanceLab.App;

public sealed partial class MainWindow : Window
{
    private enum InteractionMode { None, Calibration, EditP1, EditP2 }
    private enum PointState { Missing, Ready, Lost }
    private enum ScaleDragMode { None, Draw, StartEndpoint, EndEndpoint, Translate }

    private readonly List<DisplayOption> _displayOptions =
    [
        new(DisplayMethod.LinearContrast, "线性对比度（推荐，低噪声）", "百分位线性拉伸，只改变全局对比度。"),
        new(DisplayMethod.Original, "原始图像", "保留相机原始色彩，不做显示增强。"),
        new(DisplayMethod.HistogramEqualization, "全局直方图均衡", "增强全局对比度，也可能放大噪声。"),
        new(DisplayMethod.Clahe, "低强度 CLAHE", "分区限制对比度，适合不均匀照明。"),
        new(DisplayMethod.NlmClahe, "NLM 去噪 + CLAHE", "先去噪，再进行局部增强。"),
        new(DisplayMethod.Retinex, "单尺度 Retinex", "抑制缓慢变化的照明背景。"),
        new(DisplayMethod.Gamma, "Gamma 校正", "Gamma 小于 1 时提亮暗部。")
    ];

    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _etaTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _trackingClock = new();
    private readonly AppPreferences _preferences;
    private readonly PointD[] _trackingPoints = new PointD[2];
    private readonly PointD[] _initialTrackingPoints = new PointD[2];
    private readonly bool[] _initialPointReady = new bool[2];
    private readonly PointState[] _pointStates = [PointState.Missing, PointState.Missing];
    private readonly List<PlotRecord> _liveRecords = [];
    private List<PlotRecord> _csvRecords = [];

    private IReadOnlyList<string> _imagePaths = [];
    private string _folder = string.Empty;
    private string _outputPath = string.Empty;
    private int _currentFrame;
    private bool _uiReady;
    private long _previewGeneration;
    private PixelFrame? _pixelFrame;
    private Bitmap? _previewBitmap;
    private Bitmap? _recognitionP1;
    private Bitmap? _recognitionP2;
    private InteractionMode _mode;
    private PointD? _scaleStart;
    private PointD? _scaleEnd;
    private PointD? _scaleDrawStart;
    private PointD? _scaleDrag;
    private ScaleDragMode _scaleDragMode;
    private Point _pointerStart;
    private PointD? _scaleOriginalStart;
    private PointD? _scaleOriginalEnd;
    private double _calibrationZoom = 1;
    private Vector _calibrationOffset;
    private bool _panning;
    private Vector _panOffsetStart;
    private bool _scaleDialogOpen;
    private double? _unitPerPixel;
    private string _unit = "μm";
    private double _defaultScaleLength = 100;
    private int? _editingPointIndex;
    private PointD? _editingPoint;
    private PointD? _pointDragAnchor;
    private bool _draggingPoint;
    private int _pointCount;
    private bool _starting;
    private bool _running;
    private bool _paused;
    private bool _pauseConfirmed;
    private bool _closingConfirmed;
    private CancellationTokenSource? _trackingCancellation;
    private NativeBackend.Tracker? _tracker;
    private TrackMeasurement? _currentMeasurement;
    private bool _showingCsv;
    private int _etaCompletedFrames;
    private double _etaDeadlineActiveSeconds = double.NaN;
    private NativeBackend.TrackerSnapshot? _pendingSnapshot;
    private int _pendingSnapshotFrame;
    private DisplaySettings? _pendingSnapshotSettings;
    private bool _visualWorkerRunning;
    private long _visualSession;
    private long _lastRecognitionRefreshTicks;

    public MainWindow()
    {
        InitializeComponent();
        App.ApplyPlatformWindowStyle(this);
        if (OperatingSystem.IsMacOS()) AppMenu.IsVisible = false;
        _preferences = AppPreferences.Load();
        DisplayMethodCombo.ItemsSource = _displayOptions;
        ApplyProductSettings(_preferences.Settings, false);
        PopulatePlotFields(DataFields.Plot, "frame_index", "distance_actual");
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _ = RefreshPreviewAsync(true);
        };
        _etaTimer.Tick += (_, _) => UpdateProgressEstimate();
        KeyDown += MainWindow_KeyDown;
        _uiReady = true;
        UpdateDisplayLabels();
        UpdatePointUiState();
        UpdateControls();
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("正在验证 OpenCV 原生后端…", false);
            await Task.Run(NativeBackend.SelfTest);
            string? requested = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(Directory.Exists);
            string? initialFolder = requested ??
                (Directory.Exists(_preferences.LastWorkingDirectory) ? _preferences.LastWorkingDirectory : null) ??
                (ImageSequence.Find(Environment.CurrentDirectory).Count >= 2 ? Environment.CurrentDirectory : null);
            if (initialFolder is not null) await LoadWorkingDirectoryAsync(initialFolder);
            else await ChooseWorkingDirectoryAsync(true);
        }
        catch (Exception error)
        {
            SetStatus("初始化失败。", true);
            await MessageDialog.Show(this, "无法启动 DistanceLab", error.Message);
        }
    }

    private async void OpenFolderMenu_Click(object? sender, RoutedEventArgs e) =>
        await ChooseWorkingDirectoryAsync(false);

    private async Task ChooseWorkingDirectoryAsync(bool requireSelection)
    {
        if (_running)
        {
            await MessageDialog.Show(this, "正在追踪", "请先停止当前追踪，再切换工作目录。");
            return;
        }
        IStorageFolder? start = Directory.Exists(_folder)
            ? await StorageProvider.TryGetFolderFromPathAsync(_folder)
            : await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含图像序列的工作目录",
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        string? path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
        {
            try { await LoadWorkingDirectoryAsync(path); }
            catch (Exception error) { await MessageDialog.Show(this, "无法打开工作目录", error.Message); }
        }
        else if (requireSelection)
        {
            InteractionHint.Text = "请从“文件 → 选择工作目录”开始";
            SetStatus("尚未选择工作目录。", true);
        }
    }

    private async Task LoadWorkingDirectoryAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        SetStatus("正在扫描工作目录…", false);
        IReadOnlyList<string> images = await Task.Run(() => ImageSequence.Find(folder));
        if (images.Count < 2) throw new InvalidOperationException("所选目录中至少需要两张支持的序列图片。");

        _folder = folder;
        _outputPath = Path.Combine(folder, "tracking_results.csv");
        _imagePaths = images;
        _currentFrame = 0;
        _mode = InteractionMode.None;
        ResetCalibrationZoom();
        _scaleStart = _scaleEnd = _scaleDrawStart = _scaleDrag = null;
        _scaleDragMode = ScaleDragMode.None;
        _unitPerPixel = null;
        _pointCount = 0;
        _pointStates[0] = _pointStates[1] = PointState.Missing;
        ClearInitialTrackingPoints();
        CancelPointEdit();
        _currentMeasurement = null;
        _liveRecords.Clear();
        _csvRecords.Clear();
        ShowLiveData(true);
        DisposeBitmaps();
        TrackingProgress.Minimum = 0;
        TrackingProgress.Maximum = images.Count - 1;
        TrackingProgress.Value = 0;
        ProgressText.Text = "0%";
        WorkDirectoryText.Text = folder;
        ToolTip.SetTip(WorkDirectoryText, folder);
        Title = $"DistanceLab — {new DirectoryInfo(folder).Name}";
        FrameText.Text = $"帧 0 / {images.Count - 1:N0}";
        StepBadge.Text = "步骤 1 / 3";
        InteractionHint.Text = "点击“标定比例尺”，然后在图像中画线";
        DistanceText.Text = "实际距离：—";
        PixelDistanceText.Text = "像素距离：—";
        _preferences.LastWorkingDirectory = folder;
        SavePreferences();
        await RefreshPreviewAsync(true);
        UpdatePointUiState();
        UpdateControls();
        SetStatus($"已载入 {images.Count:N0} 张图片。结果将保存到 {Path.GetFileName(_outputPath)}。", false);
    }

    private void SchedulePreview()
    {
        if (_imagePaths.Count == 0) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task RefreshPreviewAsync(bool showBusy)
    {
        if (_imagePaths.Count == 0 || _currentFrame >= _imagePaths.Count) return;
        long generation = Interlocked.Increment(ref _previewGeneration);
        string path = _imagePaths[_currentFrame];
        DisplaySettings settings = ReadDisplaySettings();
        PreviewBusy.IsVisible = showBusy;
        try
        {
            PixelFrame frame = await Task.Run(() => NativeBackend.RenderFrame(path, settings));
            if (generation != _previewGeneration || _imagePaths[_currentFrame] != path) return;
            ApplyPixelFrame(frame);
        }
        catch (Exception error)
        {
            if (generation == _previewGeneration) SetStatus($"预览失败：{error.Message}", true);
        }
        finally
        {
            if (generation == _previewGeneration) PreviewBusy.IsVisible = false;
        }
    }

    private void ApplyPixelFrame(PixelFrame frame)
    {
        _pixelFrame = frame;
        Bitmap next = CreateBitmap(frame.PreviewBgra, frame.Width, frame.Height, frame.PreviewStride, PixelFormats.Bgra8888, AlphaFormat.Unpremul);
        Bitmap? old = _previewBitmap;
        _previewBitmap = next;
        PreviewImage.Source = next;
        old?.Dispose();
        RefreshRecognitionWindows();
        DrawOverlay();
    }

    private static Bitmap CreateBitmap(
        byte[] pixels, int width, int height, int stride, PixelFormat format, AlphaFormat alpha)
    {
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new WriteableBitmap(
                format, alpha, pin.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), stride);
        }
        finally { pin.Free(); }
    }

    private DisplaySettings ReadDisplaySettings() => new(
        (DisplayMethodCombo.SelectedItem as DisplayOption)?.Method ?? DisplayMethod.LinearContrast,
        ContrastSlider.Value, GammaSlider.Value, DenoiseSlider.Value, RetinexSlider.Value);

    private TrackingSettings ReadTrackingSettings() => new TrackingSettings(
        ParseInt(WindowSizeBox.Text, 61, 15),
        ParseInt(SearchRadiusBox.Text, 80, 10),
        ParseDouble(ReferenceThresholdBox.Text, .48),
        ParseDouble(FbThresholdBox.Text, 1.5),
        PauseOnLossCheck.IsChecked == true).Normalize();

    private static int ParseInt(string? text, int fallback, int minimum) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Max(value, minimum) : fallback;

    private static double ParseDouble(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value : fallback;

    private ProductSettings CaptureProductSettings() => new()
    {
        Theme = _preferences.Settings.Theme,
        DisplayMethod = ReadDisplaySettings().Method,
        ContrastClip = ContrastSlider.Value,
        Gamma = GammaSlider.Value,
        DenoiseStrength = DenoiseSlider.Value,
        RetinexSigma = RetinexSlider.Value,
        MarkerSize = MarkerSizeSlider.Value,
        MarkerWidth = MarkerWidthSlider.Value,
        MarkerOpacity = OpacitySlider.Value,
        WindowSize = ReadTrackingSettings().WindowSize,
        SearchRadius = ReadTrackingSettings().SearchRadius,
        ReferenceThreshold = ReadTrackingSettings().ReferenceThreshold,
        ForwardBackwardThreshold = ReadTrackingSettings().ForwardBackwardThreshold,
        PauseOnLoss = PauseOnLossCheck.IsChecked == true,
        DefaultScaleLength = _defaultScaleLength,
        DefaultUnit = _unit
    };

    private void ApplyProductSettings(ProductSettings settings, bool refreshPreview)
    {
        bool ready = _uiReady;
        _uiReady = false;
        int windowSize = settings.SchemaVersion < 4 && settings.WindowSize == 41
            ? 61
            : settings.WindowSize;
        settings.SchemaVersion = 4;
        _preferences.Settings.Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System";
        ApplyTheme(_preferences.Settings.Theme);
        DisplayMethodCombo.SelectedItem = _displayOptions.First(option => option.Method == settings.DisplayMethod);
        ContrastSlider.Value = Math.Clamp(settings.ContrastClip, 0, 10);
        GammaSlider.Value = Math.Clamp(settings.Gamma, .1, 3);
        DenoiseSlider.Value = Math.Clamp(settings.DenoiseStrength, 0, 25);
        RetinexSlider.Value = Math.Clamp(settings.RetinexSigma, 5, 200);
        MarkerSizeSlider.Value = Math.Clamp(settings.MarkerSize, 7, 30);
        MarkerWidthSlider.Value = Math.Clamp(settings.MarkerWidth, .6, 3);
        OpacitySlider.Value = Math.Clamp(settings.MarkerOpacity, .15, .9);
        WindowSizeBox.Text = windowSize.ToString(CultureInfo.InvariantCulture);
        SearchRadiusBox.Text = settings.SearchRadius.ToString(CultureInfo.InvariantCulture);
        ReferenceThresholdBox.Text = settings.ReferenceThreshold.ToString("G", CultureInfo.InvariantCulture);
        FbThresholdBox.Text = settings.ForwardBackwardThreshold.ToString("G", CultureInfo.InvariantCulture);
        PauseOnLossCheck.IsChecked = settings.PauseOnLoss;
        _defaultScaleLength = settings.DefaultScaleLength > 0 ? settings.DefaultScaleLength : 100;
        _unit = string.IsNullOrWhiteSpace(settings.DefaultUnit) ? "μm" : settings.DefaultUnit;
        _uiReady = ready;
        UpdateDisplayLabels();
        if (refreshPreview) SchedulePreview();
        DrawOverlay();
    }

    private void SavePreferences()
    {
        try
        {
            _preferences.Settings = CaptureProductSettings();
            _preferences.Save();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ApplyTheme(string name)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = name switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private void DisplaySetting_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateDisplayLabels();
        SchedulePreview();
        SavePreferences();
    }

    private void OverlaySetting_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        DrawOverlay();
        SavePreferences();
    }

    private void TrackingSetting_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshRecognitionWindows();
        DrawOverlay();
    }

    private void UpdateDisplayLabels()
    {
        ContrastValue.Text = $"{ContrastSlider.Value:F1}%";
        GammaValue.Text = $"{GammaSlider.Value:F2}";
        DenoiseValue.Text = $"{DenoiseSlider.Value:F1}";
        RetinexValue.Text = $"{RetinexSlider.Value:F0}";
        DisplayMethod method = ReadDisplaySettings().Method;
        AlgorithmDescription.Text = _displayOptions.First(option => option.Method == method).Description;
        ContrastParameterRow.IsVisible = method == DisplayMethod.LinearContrast;
        GammaParameterRow.IsVisible = method == DisplayMethod.Gamma;
        DenoiseParameterRow.IsVisible = method == DisplayMethod.NlmClahe;
        RetinexParameterRow.IsVisible = method == DisplayMethod.Retinex;
    }

    private void DisposeBitmaps()
    {
        PreviewImage.Source = null;
        P1RecognitionImage.Source = null;
        P2RecognitionImage.Source = null;
        _previewBitmap?.Dispose();
        _recognitionP1?.Dispose();
        _recognitionP2?.Dispose();
        _previewBitmap = _recognitionP1 = _recognitionP2 = null;
        _pixelFrame = null;
    }
}
