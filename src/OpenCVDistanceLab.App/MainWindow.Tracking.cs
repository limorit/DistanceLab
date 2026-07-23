using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using OpenCVDistanceLab.Core;
using OpenCVDistanceLab.Interop;
using System.Diagnostics;
using System.Text;

namespace OpenCVDistanceLab.App;

public sealed partial class MainWindow
{
    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_starting || _running || _unitPerPixel is null ||
            _pointStates.Any(state => state != PointState.Ready)) return;
        if (_initialPointReady.Any(ready => !ready))
        {
            SetStatus("首帧初始点不完整，请重新确认 P1 和 P2。", true);
            return;
        }
        _starting = true;
        UpdateControls();
        try
        {
            if (File.Exists(_outputPath) &&
                !await MessageDialog.Confirm(this, "覆盖结果", $"{Path.GetFileName(_outputPath)} 已存在，是否覆盖？"))
                return;
            _visualSession++;
            DisposePendingSnapshot();
            await RestoreInitialTrackingStateAsync();
            string snapshot = SaveTrackingStartSnapshot();
            _trackingCancellation = new CancellationTokenSource();
            _tracker?.Dispose();
            PointD[] runStartPoints = [_initialTrackingPoints[0], _initialTrackingPoints[1]];
            _tracker = new NativeBackend.Tracker(_imagePaths[0], runStartPoints, ReadTrackingSettings());
            _running = true;
            _paused = false;
            _pauseConfirmed = false;
            _liveRecords.Clear();
            ShowLiveData(true);
            TrackingProgress.Value = 0;
            _etaCompletedFrames = 0;
            _etaDeadlineActiveSeconds = double.NaN;
            UpdateControls();
            await RunTrackingAsync(_trackingCancellation.Token, snapshot);
        }
        catch (OperationCanceledException)
        {
            SetStatus("追踪已停止，已经写入的 CSV 数据已保留。", false);
        }
        catch (Exception error)
        {
            string log = Path.Combine(_folder, "tracking_error.log");
            TryAppendLog(log, error.ToString());
            SetStatus("追踪发生错误。", true);
            await MessageDialog.Show(this, "OpenCV 追踪错误",
                $"{error.Message}{Environment.NewLine}{Environment.NewLine}诊断记录：{Path.GetFileName(log)}");
        }
        finally
        {
            _starting = false;
            _etaTimer.Stop();
            _trackingClock.Stop();
            _running = false;
            _paused = false;
            _pauseConfirmed = false;
            _visualSession++;
            DisposePendingSnapshot();
            _tracker?.Dispose();
            _tracker = null;
            UpdatePointUiState();
            UpdateControls();
        }
    }

    private async Task RestoreInitialTrackingStateAsync()
    {
        bool refreshFirstFrame = _currentFrame != 0 || _currentMeasurement is not null;
        _currentFrame = 0;
        _currentMeasurement = null;
        for (int index = 0; index < 2; index++)
        {
            _trackingPoints[index] = _initialTrackingPoints[index];
            _pointStates[index] = PointState.Ready;
        }
        _pointCount = 2;
        TrackingProgress.Value = 0;
        ProgressText.Text = "0%";
        FrameText.Text = $"帧 0 / {_imagePaths.Count - 1:N0}";
        UpdateDistance(_trackingPoints[0].DistanceTo(_trackingPoints[1]));
        UpdatePointUiState();
        UpdateControls();
        if (refreshFirstFrame) await RefreshPreviewAsync(true);
        else DrawOverlay();
    }

    private string SaveTrackingStartSnapshot()
    {
        if (_previewBitmap is null || ImageHost.Bounds.Width < 1 || ImageHost.Bounds.Height < 1)
            throw new InvalidOperationException("当前图像尚未完成显示，无法保存起始点截图。");
        DrawOverlay();
        string directory = Path.Combine(_folder, "tracking_start_snapshots");
        Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"tracking_start_{stamp}.png");
        for (int suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"tracking_start_{stamp}_{suffix}.png");
        bool busy = PreviewBusy.IsVisible;
        PreviewBusy.IsVisible = false;
        try
        {
            using RenderTargetBitmap target = new(new PixelSize(
                Math.Max(1, (int)Math.Ceiling(ImageHost.Bounds.Width)),
                Math.Max(1, (int)Math.Ceiling(ImageHost.Bounds.Height))), new Vector(96, 96));
            target.Render(ImageHost);
            target.Save(path, PngBitmapEncoderOptions.Default);
        }
        finally { PreviewBusy.IsVisible = busy; }
        return path;
    }

    private async Task RunTrackingAsync(CancellationToken token, string startSnapshot)
    {
        double initialDistance = _trackingPoints[0].DistanceTo(_trackingPoints[1]);
        await using StreamWriter csv = new(_outputPath, false, new UTF8Encoding(true));
        await csv.WriteLineAsync(MeasurementCsv.Header);
        TrackMeasurement initial = new(
            [_trackingPoints[0], _trackingPoints[1]], [1, 1],
            [TrackStatus.Manual, TrackStatus.Manual], [true, true]);
        _currentMeasurement = initial;
        await RecordFrameAsync(csv, 0, initial, initialDistance);
        SetStatus($"OpenCV 跨平台原生追踪已开始；起始截图：{Path.GetFileName(startSnapshot)}", false);
        _trackingClock.Restart();
        _etaTimer.Start();

        Dictionary<int, Task<NativeBackend.PreparedFrame>> preparedTasks = [];
        int unreadable = 0;
        for (int frame = 1; frame < Math.Min(_imagePaths.Count, 3); frame++)
            SchedulePreparedFrame(preparedTasks, frame);
        try
        {
            for (int frame = 1; frame < _imagePaths.Count; frame++)
            {
                await WaitWhilePausedAsync(token);
                token.ThrowIfCancellationRequested();
                Task<NativeBackend.PreparedFrame> preparation = preparedTasks[frame];
                preparedTasks.Remove(frame);
                NativeBackend.PreparedFrame? prepared = null;
                try
                {
                    prepared = await preparation;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unreadable++;
                    SchedulePreparedFrame(preparedTasks, frame + 2);
                    TrackMeasurement missing = new(
                        [_trackingPoints[0], _trackingPoints[1]], [double.NaN, double.NaN],
                        [TrackStatus.ImageMissing, TrackStatus.ImageMissing], [false, false]);
                    _currentMeasurement = missing;
                    SetCurrentFrame(frame, missing);
                    await RecordFrameAsync(csv, frame, missing, initialDistance);
                    AppendUnreadableFrameDiagnostic(frame, _imagePaths[frame], error);
                    UpdateTrackingEstimate(frame);
                    SetStatus($"已跳过无法读取的第 {frame:N0} 帧：{Path.GetFileName(_imagePaths[frame])}。", true);
                    continue;
                }

                using (prepared)
                {
                    SchedulePreparedFrame(preparedTasks, frame + 2);
                    TrackingSettings settings = ReadTrackingSettings();
                    (TrackMeasurement measurement, NativeBackend.TrackerSnapshot snapshot) = await Task.Run(() =>
                    {
                        TrackMeasurement result = _tracker!.Step(prepared, settings);
                        return (result, _tracker.Snapshot());
                    }, token);
                    _currentMeasurement = measurement;
                    _trackingPoints[0] = measurement.Points[0];
                    _trackingPoints[1] = measurement.Points[1];
                    for (int index = 0; index < 2; index++)
                        _pointStates[index] = measurement.Valid[index] ? PointState.Ready : PointState.Lost;
                    _pointCount = 2;
                    SetCurrentFrame(frame, measurement);
                    bool lost = settings.PauseOnLoss && measurement.Valid.Any(valid => !valid);
                    if (lost || frame == _imagePaths.Count - 1)
                        await RenderExactSnapshotAsync(frame, snapshot, ReadDisplaySettings());
                    else
                        QueueSnapshot(frame, snapshot, ReadDisplaySettings());

                    if (lost)
                    {
                        _paused = true;
                        _pauseConfirmed = true;
                        SetStatus("识别点丢失，已安全暂停。请修正黄色 P1/P2 并确认。", true);
                        UpdatePointUiState();
                        UpdateControls();
                        await WaitWhilePausedAsync(token);
                        measurement = _currentMeasurement ?? measurement;
                    }
                    await RecordFrameAsync(csv, frame, measurement, initialDistance);
                    UpdateTrackingEstimate(frame);
                }
            }
        }
        finally
        {
            foreach (Task<NativeBackend.PreparedFrame> task in preparedTasks.Values)
            {
                try { (await task).Dispose(); }
                catch { }
            }
        }
        TrackingProgress.Value = TrackingProgress.Maximum;
        ProgressText.Text = "100% · 完成";
        RefreshPlot();
        SetStatus(unreadable == 0
            ? $"追踪完成：{Path.GetFileName(_outputPath)}"
            : $"追踪完成：已跳过 {unreadable:N0} 张无法读取的图像。", unreadable > 0);
    }

    private void SchedulePreparedFrame(
        Dictionary<int, Task<NativeBackend.PreparedFrame>> tasks, int frame)
    {
        if (frame >= _imagePaths.Count || tasks.ContainsKey(frame)) return;
        string path = _imagePaths[frame];
        tasks[frame] = Task.Run(() => PrepareTrackingFrame(path, frame));
    }

    private static NativeBackend.PreparedFrame PrepareTrackingFrame(string path, int frame)
    {
        try { return NativeBackend.PrepareFrame(path); }
        catch (Exception first)
        {
            GC.Collect(1, GCCollectionMode.Forced, true, false);
            Thread.Sleep(250);
            try { return NativeBackend.PrepareFrame(path); }
            catch (Exception second)
            {
                throw new InvalidOperationException(
                    $"第 {frame:N0} 帧重试后仍无法读取：{Path.GetFileName(path)}", new AggregateException(first, second));
            }
        }
    }

    private async Task RecordFrameAsync(
        StreamWriter csv, int frame, TrackMeasurement measurement, double initialDistance)
    {
        string row = MeasurementCsv.CreateRow(
            frame, Path.GetFileName(_imagePaths[frame]), measurement,
            initialDistance, _unitPerPixel!.Value, _unit);
        await csv.WriteLineAsync(row);
        await csv.FlushAsync();
        _liveRecords.Add(MeasurementCsv.CreatePlotRecord(frame, measurement, initialDistance, _unitPerPixel.Value));
        PlotSourceText.Text = $"数据源：实时追踪（{_liveRecords.Count:N0} 条）";
        if (!_showingCsv && (frame == 0 || frame == _imagePaths.Count - 1 ||
            Stopwatch.GetElapsedTime(_lastPlotRefreshTicks).TotalMilliseconds >= 180))
        {
            _lastPlotRefreshTicks = Stopwatch.GetTimestamp();
            RefreshPlot();
        }
    }

    private long _lastPlotRefreshTicks;

    private void SetCurrentFrame(int frame, TrackMeasurement measurement)
    {
        _currentFrame = frame;
        TrackingProgress.Value = frame;
        UpdateDistance(measurement.DistancePixels);
        string frameText = $"帧 {frame:N0} / {_imagePaths.Count - 1:N0}";
        if (FrameText.Text != frameText) FrameText.Text = frameText;
        DrawOverlay();
    }

    private void QueueSnapshot(int frame, NativeBackend.TrackerSnapshot snapshot, DisplaySettings settings)
    {
        NativeBackend.TrackerSnapshot? replaced = _pendingSnapshot;
        _pendingSnapshot = snapshot;
        _pendingSnapshotFrame = frame;
        _pendingSnapshotSettings = settings;
        replaced?.Dispose();
        if (_visualWorkerRunning) return;
        _visualWorkerRunning = true;
        _ = RunVisualWorkerAsync(_visualSession);
    }

    private async Task RunVisualWorkerAsync(long session)
    {
        try
        {
            while (_pendingSnapshot is not null)
            {
                NativeBackend.TrackerSnapshot snapshot = _pendingSnapshot;
                int frame = _pendingSnapshotFrame;
                DisplaySettings settings = _pendingSnapshotSettings!;
                _pendingSnapshot = null;
                try
                {
                    PixelFrame rendered = await Task.Run(() => snapshot.Render(settings));
                    if (session == _visualSession && frame == _currentFrame) ApplyPixelFrame(rendered);
                }
                catch (Exception error)
                {
                    if (session == _visualSession) SetStatus($"追踪预览跳过：{error.Message}", true);
                }
                finally { snapshot.Dispose(); }
                if (settings.Method is DisplayMethod.NlmClahe or DisplayMethod.Retinex)
                    await Task.Delay(350);
            }
        }
        finally
        {
            _visualWorkerRunning = false;
            if (_pendingSnapshot is not null)
            {
                _visualWorkerRunning = true;
                _ = RunVisualWorkerAsync(_visualSession);
            }
        }
    }

    private async Task RenderExactSnapshotAsync(
        int frame, NativeBackend.TrackerSnapshot snapshot, DisplaySettings settings)
    {
        DisposePendingSnapshot();
        try
        {
            PixelFrame rendered = await Task.Run(() => snapshot.Render(settings));
            if (frame == _currentFrame) ApplyPixelFrame(rendered);
        }
        catch (Exception error) { SetStatus($"当前帧预览失败：{error.Message}", true); }
        finally { snapshot.Dispose(); }
    }

    private void DisposePendingSnapshot()
    {
        NativeBackend.TrackerSnapshot? pending = _pendingSnapshot;
        _pendingSnapshot = null;
        pending?.Dispose();
    }

    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        if (!_paused) return;
        _pauseConfirmed = true;
        _trackingClock.Stop();
        if (_pointStates.All(state => state == PointState.Ready))
            SetStatus("已进入安全暂停点；可调整 P1/P2，也可继续。", false);
        UpdatePointUiState();
        UpdateControls();
        while (_paused)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(40, token);
        }
        _trackingClock.Start();
        _pauseConfirmed = false;
        UpdateControls();
    }

    private void PauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_running) return;
        if (_paused)
        {
            if (_pointStates.Any(state => state != PointState.Ready))
            {
                SetStatus("请先修正并确认所有丢失点。", true);
                return;
            }
            _paused = false;
            SetStatus("追踪继续运行。", false);
        }
        else
        {
            _paused = true;
            _pauseConfirmed = false;
            SetStatus("正在等待当前 OpenCV 帧结束并进入安全暂停点…", true);
        }
        UpdateControls();
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _trackingCancellation?.Cancel();
        _paused = false;
        SetStatus("正在安全停止并关闭结果文件…", true);
    }

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        CancelPointEdit();
        _mode = InteractionMode.None;
        _pointStates[0] = _pointStates[1] = PointState.Missing;
        _pointCount = 0;
        _trackingPoints[0] = _trackingPoints[1] = default;
        ClearInitialTrackingPoints();
        _currentMeasurement = null;
        _currentFrame = 0;
        _liveRecords.Clear();
        ShowLiveData(true);
        TrackingProgress.Value = 0;
        ProgressText.Text = "0%";
        FrameText.Text = _imagePaths.Count > 0
            ? $"帧 0 / {_imagePaths.Count - 1:N0}" : "帧 — / —";
        _ = RefreshPreviewAsync(true);
        UpdateDistance(double.NaN);
        UpdatePointUiState();
        UpdateControls();
        DrawOverlay();
        SetStatus(_unitPerPixel is null ? "测量已重置。" : "测量已重置，比例尺已保留。", false);
    }

    private void UpdateTrackingEstimate(int completedFrame)
    {
        _etaCompletedFrames = completedFrame;
        int total = Math.Max(1, _imagePaths.Count - 1);
        double elapsed = _trackingClock.Elapsed.TotalSeconds;
        if (completedFrame > 0 && elapsed > 0)
            _etaDeadlineActiveSeconds = elapsed + elapsed / completedFrame * (total - completedFrame);
        UpdateProgressEstimate();
    }

    private void UpdateProgressEstimate()
    {
        if (!_running || _imagePaths.Count < 2) return;
        int total = _imagePaths.Count - 1;
        double elapsed = _trackingClock.Elapsed.TotalSeconds;
        double fraction = _etaCompletedFrames / (double)total;
        string frameText = $"帧 {_currentFrame:N0} / {total:N0}";
        if (FrameText.Text != frameText) FrameText.Text = frameText;
        if (TrackingProgress.Value != _etaCompletedFrames)
            TrackingProgress.Value = _etaCompletedFrames;
        if (!double.IsFinite(_etaDeadlineActiveSeconds))
        {
            string estimating = $"{fraction:P1} · 估算中";
            if (ProgressText.Text != estimating) ProgressText.Text = estimating;
            return;
        }
        double seconds = _etaDeadlineActiveSeconds - elapsed;
        string progressText = seconds > 0
            ? $"{fraction:P1} · {FormatDuration(TimeSpan.FromSeconds(seconds))}"
            : $"{fraction:P1} · 重估中";
        if (ProgressText.Text != progressText) ProgressText.Text = progressText;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString(@"mm\:ss");

    private void AppendUnreadableFrameDiagnostic(int frame, string path, Exception error) =>
        TryAppendLog(Path.Combine(_folder, "tracking_error.log"),
            $"SKIPPED UNREADABLE FRAME {frame:N0}{Environment.NewLine}Path: {path}{Environment.NewLine}{error}");

    private static void TryAppendLog(string path, string content)
    {
        try
        {
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}]{Environment.NewLine}{content}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
