using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenCVDistanceLab.Core;
using OpenCVDistanceLab.Interop;
using System.Runtime.InteropServices;

namespace OpenCVDistanceLab.App;

public sealed partial class MainWindow
{
    private async void CalibrateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mode == InteractionMode.Calibration)
        {
            await CommitScaleEditAsync();
            return;
        }
        if (_running || _imagePaths.Count == 0 || _editingPointIndex is not null) return;
        _mode = InteractionMode.Calibration;
        ResetCalibrationZoom();
        _scaleDrawStart = _scaleStart;
        _scaleDrag = _scaleEnd;
        ImageHost.Focus();
        SetStatus("比例尺调整中：滚轮缩放、右键拖图、左键调整标尺；按钮或 Enter 确认。", true);
        UpdatePointUiState();
        DrawOverlay();
        UpdateControls();
    }

    private async void Point1Button_Click(object? sender, RoutedEventArgs e) => await TogglePointEditAsync(0);
    private async void Point2Button_Click(object? sender, RoutedEventArgs e) => await TogglePointEditAsync(1);

    private async Task TogglePointEditAsync(int index)
    {
        if (_editingPointIndex == index)
        {
            CommitPointEdit(index);
            return;
        }
        if (_editingPointIndex is not null)
        {
            SetStatus($"请先确认 P{_editingPointIndex + 1}。", true);
            return;
        }
        if (_imagePaths.Count == 0 || _mode == InteractionMode.Calibration ||
            (_running && (!_paused || !_pauseConfirmed))) return;

        if (!_running && _currentFrame != 0)
        {
            _currentFrame = 0;
            _pointStates[0] = _pointStates[1] = PointState.Missing;
            _pointCount = 0;
            ClearInitialTrackingPoints();
            _currentMeasurement = null;
            await RefreshPreviewAsync(true);
        }
        _editingPointIndex = index;
        _mode = index == 0 ? InteractionMode.EditP1 : InteractionMode.EditP2;
        _editingPoint = _pointStates[index] == PointState.Missing
            ? DefaultPointPosition(index)
            : ClampPointToImage(_trackingPoints[index]);
        ImageHost.Focus();
        SetStatus($"P{index + 1} 调整中：点击、拖动或使用方向键，再次点击按钮确认。", true);
        UpdatePointUiState();
        DrawOverlay();
        UpdateControls();
    }

    private void ImageHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint current = e.GetCurrentPoint(ImageHost);
        Point canvas = e.GetPosition(ImageHost);
        if (current.Properties.IsRightButtonPressed && _mode == InteractionMode.Calibration)
        {
            _panning = true;
            _pointerStart = canvas;
            _panOffsetStart = _calibrationOffset;
            e.Pointer.Capture(ImageHost);
            e.Handled = true;
            return;
        }
        if (!current.Properties.IsLeftButtonPressed) return;
        PointD? imagePoint = CanvasToImage(canvas, true);
        if (imagePoint is null) return;
        if (_mode == InteractionMode.Calibration)
        {
            BeginScaleDrag(imagePoint.Value, canvas);
            e.Pointer.Capture(ImageHost);
        }
        else if (_editingPointIndex is not null)
        {
            _editingPoint = ClampPointToImage(imagePoint.Value);
            _draggingPoint = true;
            e.Pointer.Capture(ImageHost);
            UpdatePointUiState();
            DrawOverlay();
        }
    }

    private void ImageHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        PointerPoint current = e.GetCurrentPoint(ImageHost);
        if (_panning && current.Properties.IsRightButtonPressed)
        {
            Vector movement = e.GetPosition(ImageHost) - _pointerStart;
            _calibrationOffset = _panOffsetStart + movement;
            ClampCalibrationZoom();
            ApplyCalibrationTransform();
            DrawOverlay();
            return;
        }
        if (_draggingPoint && _editingPointIndex is not null && current.Properties.IsLeftButtonPressed)
        {
            PointD? point = CanvasToImage(e.GetPosition(ImageHost), true);
            if (point is not null)
            {
                _editingPoint = ClampPointToImage(point.Value);
                UpdatePointUiState();
                DrawOverlay();
            }
            return;
        }
        if (_mode != InteractionMode.Calibration || _scaleDragMode == ScaleDragMode.None ||
            !current.Properties.IsLeftButtonPressed) return;
        PointD? image = CanvasToImage(e.GetPosition(ImageHost), true);
        if (image is null) return;
        ApplyScaleDrag(image.Value, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        UpdatePointUiState();
        DrawOverlay();
    }

    private void ImageHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            return;
        }
        if (_draggingPoint)
        {
            PointD? point = CanvasToImage(e.GetPosition(ImageHost), true);
            if (point is not null) _editingPoint = ClampPointToImage(point.Value);
            _draggingPoint = false;
            e.Pointer.Capture(null);
            UpdatePointUiState();
            DrawOverlay();
            return;
        }
        if (_scaleDragMode != ScaleDragMode.None)
        {
            _scaleDragMode = ScaleDragMode.None;
            e.Pointer.Capture(null);
            UpdatePointUiState();
            DrawOverlay();
        }
    }

    private void ImageHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_mode != InteractionMode.Calibration || _pixelFrame is null) return;
        Point pointer = e.GetPosition(ImageHost);
        PointD? imagePoint = CanvasToImage(pointer, true);
        if (imagePoint is null) return;
        double oldZoom = _calibrationZoom;
        double factor = Math.Pow(1.18, e.Delta.Y);
        _calibrationZoom = Math.Clamp(oldZoom * factor, 1, 12);
        if (Math.Abs(_calibrationZoom - oldZoom) < 1e-9) return;
        (double baseScale, double baseLeft, double baseTop) = BaseImageTransform();
        _calibrationOffset = new Vector(
            pointer.X - (baseLeft + imagePoint.Value.X * baseScale) * _calibrationZoom,
            pointer.Y - (baseTop + imagePoint.Value.Y * baseScale) * _calibrationZoom);
        ClampCalibrationZoom();
        ApplyCalibrationTransform();
        UpdatePointUiState();
        DrawOverlay();
        e.Handled = true;
    }

    private void BeginScaleDrag(PointD imagePoint, Point canvasPoint)
    {
        _scaleOriginalStart = _scaleDrawStart;
        _scaleOriginalEnd = _scaleDrag;
        _pointerStart = canvasPoint;
        if (_scaleDrawStart is not null && _scaleDrag is not null)
        {
            Point a = ImageToCanvas(_scaleDrawStart.Value);
            Point b = ImageToCanvas(_scaleDrag.Value);
            if (PointDistance(a, canvasPoint) <= 11) _scaleDragMode = ScaleDragMode.StartEndpoint;
            else if (PointDistance(b, canvasPoint) <= 11) _scaleDragMode = ScaleDragMode.EndEndpoint;
            else if (DistanceToSegment(canvasPoint, a, b) <= 9) _scaleDragMode = ScaleDragMode.Translate;
            else
            {
                _scaleDragMode = ScaleDragMode.Draw;
                _scaleDrawStart = _scaleDrag = imagePoint;
            }
        }
        else
        {
            _scaleDragMode = ScaleDragMode.Draw;
            _scaleDrawStart = _scaleDrag = imagePoint;
        }
    }

    private void ApplyScaleDrag(PointD point, bool constrain)
    {
        switch (_scaleDragMode)
        {
            case ScaleDragMode.Draw:
            case ScaleDragMode.EndEndpoint:
                if (_scaleDrawStart is not null)
                    _scaleDrag = constrain ? Constrain(_scaleDrawStart.Value, point) : point;
                break;
            case ScaleDragMode.StartEndpoint:
                if (_scaleDrag is not null)
                    _scaleDrawStart = constrain ? Constrain(_scaleDrag.Value, point) : point;
                break;
            case ScaleDragMode.Translate:
                if (_scaleOriginalStart is null || _scaleOriginalEnd is null) break;
                PointD? start = CanvasToImage(_pointerStart, true);
                if (start is null) break;
                PointD delta = point - start.Value;
                TranslateScale(delta);
                break;
        }
    }

    private void TranslateScale(PointD delta)
    {
        if (_pixelFrame is null || _scaleOriginalStart is null || _scaleOriginalEnd is null) return;
        double minX = Math.Min(_scaleOriginalStart.Value.X, _scaleOriginalEnd.Value.X);
        double maxX = Math.Max(_scaleOriginalStart.Value.X, _scaleOriginalEnd.Value.X);
        double minY = Math.Min(_scaleOriginalStart.Value.Y, _scaleOriginalEnd.Value.Y);
        double maxY = Math.Max(_scaleOriginalStart.Value.Y, _scaleOriginalEnd.Value.Y);
        delta = new PointD(
            Math.Clamp(delta.X, -minX, _pixelFrame.Width - 1.001 - maxX),
            Math.Clamp(delta.Y, -minY, _pixelFrame.Height - 1.001 - maxY));
        _scaleDrawStart = _scaleOriginalStart.Value + delta;
        _scaleDrag = _scaleOriginalEnd.Value + delta;
    }

    private async Task CommitScaleEditAsync()
    {
        if (_scaleDialogOpen) return;
        if (_scaleDrawStart is null || _scaleDrag is null ||
            _scaleDrawStart.Value.DistanceTo(_scaleDrag.Value) < 2)
        {
            SetStatus("标尺线太短，请重新绘制。", true);
            return;
        }
        double pixels = _scaleDrawStart.Value.DistanceTo(_scaleDrag.Value);
        _scaleDialogOpen = true;
        try
        {
            ScaleInputDialog dialog = new(pixels, _defaultScaleLength, _unit);
            bool accepted = await dialog.ShowDialog<bool>(this);
            if (!accepted) return;
            _defaultScaleLength = dialog.ActualLength;
            _unit = dialog.Unit;
            _unitPerPixel = dialog.ActualLength / pixels;
            _scaleStart = _scaleDrawStart;
            _scaleEnd = _scaleDrag;
            _mode = InteractionMode.None;
            ResetCalibrationZoom();
            SetStatus($"比例尺已确认：1 px = {_unitPerPixel.Value:G8} {_unit}。", false, true);
            UpdatePointUiState();
            UpdateControls();
            DrawOverlay();
            SavePreferences();
        }
        catch (Exception error)
        {
            SetStatus($"无法打开比例尺设置：{error.Message}", true);
            Console.Error.WriteLine($"Scale dialog failed: {error}");
        }
        finally
        {
            _scaleDialogOpen = false;
        }
    }

    private void CommitPointEdit(int index)
    {
        if (_editingPoint is null) return;
        PointD point = ClampPointToImage(_editingPoint.Value);
        if (_running)
        {
            if (!_paused || !_pauseConfirmed || _tracker is null) return;
            _tracker.Reselect(index, point);
        }
        _trackingPoints[index] = point;
        _pointStates[index] = PointState.Ready;
        if (!_running && _currentFrame == 0)
        {
            _initialTrackingPoints[index] = point;
            _initialPointReady[index] = true;
        }
        _pointCount = _pointStates.Count(state => state != PointState.Missing);
        if (_currentMeasurement is not null)
        {
            PointD[] points = (PointD[])_currentMeasurement.Points.Clone();
            double[] qualities = (double[])_currentMeasurement.Qualities.Clone();
            TrackStatus[] statuses = (TrackStatus[])_currentMeasurement.Statuses.Clone();
            bool[] valid = (bool[])_currentMeasurement.Valid.Clone();
            points[index] = point;
            qualities[index] = double.NaN;
            statuses[index] = TrackStatus.Manual;
            valid[index] = true;
            _currentMeasurement = new TrackMeasurement(points, qualities, statuses, valid);
        }
        CancelPointEdit();
        UpdatePointUiState();
        UpdateControls();
        DrawOverlay();
        SetStatus($"P{index + 1} 已确认。", false, true);
    }

    private void CancelPointEdit()
    {
        _editingPointIndex = null;
        _editingPoint = null;
        _draggingPoint = false;
        if (_mode is InteractionMode.EditP1 or InteractionMode.EditP2) _mode = InteractionMode.None;
    }

    private void ClearInitialTrackingPoints()
    {
        Array.Clear(_initialTrackingPoints);
        Array.Clear(_initialPointReady);
    }

    private PointD DefaultPointPosition(int index) => _pixelFrame is null
        ? default
        : ClampPointToImage(new PointD(_pixelFrame.Width * (index == 0 ? .4 : .6), _pixelFrame.Height * .5));

    private PointD ClampPointToImage(PointD point)
    {
        if (_pixelFrame is null) return point;
        int referenceSize = Math.Clamp(ReadTrackingSettings().WindowSize, 31, 61) | 1;
        double margin = Math.Min(referenceSize / 2d + 2, Math.Min(_pixelFrame.Width, _pixelFrame.Height) / 2d - 1);
        return new PointD(
            Math.Clamp(point.X, margin, _pixelFrame.Width - margin - .001),
            Math.Clamp(point.Y, margin, _pixelFrame.Height - margin - .001));
    }

    private static PointD Constrain(PointD start, PointD current) =>
        Math.Abs(current.X - start.X) >= Math.Abs(current.Y - start.Y)
            ? new PointD(current.X, start.Y)
            : new PointD(start.X, current.Y);

    private static double DistanceToSegment(Point point, Point start, Point end)
    {
        Vector segment = end - start;
        if (segment.SquaredLength < 1e-9) return PointDistance(point, start);
        Vector delta = point - start;
        double dot = delta.X * segment.X + delta.Y * segment.Y;
        double fraction = Math.Clamp(dot / segment.SquaredLength, 0, 1);
        return PointDistance(point, start + segment * fraction);
    }

    private static double PointDistance(Point left, Point right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Finish routing the native key event before creating a modal
            // window. Opening it synchronously from macOS KeyDown can re-enter
            // the route and attempt to create the dialog more than once.
            e.Handled = true;
            if (_mode == InteractionMode.Calibration)
            {
                await Task.Yield();
                await CommitScaleEditAsync();
            }
            else if (_editingPointIndex is int index) CommitPointEdit(index);
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_mode == InteractionMode.Calibration)
            {
                _mode = InteractionMode.None;
                _scaleDrawStart = _scaleStart;
                _scaleDrag = _scaleEnd;
                ResetCalibrationZoom();
            }
            else CancelPointEdit();
            UpdatePointUiState();
            UpdateControls();
            DrawOverlay();
            e.Handled = true;
            return;
        }
        if (_editingPointIndex is null || _editingPoint is null) return;
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? .1 : 1;
        PointD delta = e.Key switch
        {
            Key.Left => new PointD(-step, 0),
            Key.Right => new PointD(step, 0),
            Key.Up => new PointD(0, -step),
            Key.Down => new PointD(0, step),
            _ => default
        };
        if (delta != default)
        {
            _editingPoint = ClampPointToImage(_editingPoint.Value + delta);
            UpdatePointUiState();
            DrawOverlay();
            e.Handled = true;
        }
    }

    private (double Scale, double Left, double Top) BaseImageTransform()
    {
        if (_pixelFrame is null) return (1, 0, 0);
        double scale = Math.Min(ImageHost.Bounds.Width / _pixelFrame.Width, ImageHost.Bounds.Height / _pixelFrame.Height);
        return (scale, (ImageHost.Bounds.Width - _pixelFrame.Width * scale) / 2,
            (ImageHost.Bounds.Height - _pixelFrame.Height * scale) / 2);
    }

    private (double Scale, double Left, double Top) ImageTransform()
    {
        (double scale, double left, double top) = BaseImageTransform();
        return (scale * _calibrationZoom,
            left * _calibrationZoom + _calibrationOffset.X,
            top * _calibrationZoom + _calibrationOffset.Y);
    }

    private PointD? CanvasToImage(Point canvas, bool clamp)
    {
        if (_pixelFrame is null) return null;
        (double scale, double left, double top) = ImageTransform();
        if (scale <= 0) return null;
        double x = (canvas.X - left) / scale;
        double y = (canvas.Y - top) / scale;
        if (clamp)
            return new PointD(Math.Clamp(x, 0, _pixelFrame.Width - 1.001), Math.Clamp(y, 0, _pixelFrame.Height - 1.001));
        return x >= 0 && y >= 0 && x < _pixelFrame.Width && y < _pixelFrame.Height ? new PointD(x, y) : null;
    }

    private Point ImageToCanvas(PointD image)
    {
        (double scale, double left, double top) = ImageTransform();
        return new Point(left + image.X * scale, top + image.Y * scale);
    }

    private void ApplyCalibrationTransform()
    {
        PreviewImage.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        if (_calibrationZoom <= 1.0001)
        {
            PreviewImage.RenderTransform = null;
        }
        else
        {
            PreviewImage.RenderTransform = new MatrixTransform(new Matrix(
                _calibrationZoom, 0, 0, _calibrationZoom,
                _calibrationOffset.X, _calibrationOffset.Y));
        }
    }

    private void ClampCalibrationZoom()
    {
        if (_pixelFrame is null || _calibrationZoom <= 1.0001)
        {
            _calibrationOffset = default;
            return;
        }
        (double scale, double left, double top) = BaseImageTransform();
        double width = _pixelFrame.Width * scale * _calibrationZoom;
        double height = _pixelFrame.Height * scale * _calibrationZoom;
        double x = width >= ImageHost.Bounds.Width
            ? Math.Clamp(_calibrationOffset.X,
                ImageHost.Bounds.Width - (left + _pixelFrame.Width * scale) * _calibrationZoom,
                -left * _calibrationZoom)
            : (ImageHost.Bounds.Width - width) / 2 - left * _calibrationZoom;
        double y = height >= ImageHost.Bounds.Height
            ? Math.Clamp(_calibrationOffset.Y,
                ImageHost.Bounds.Height - (top + _pixelFrame.Height * scale) * _calibrationZoom,
                -top * _calibrationZoom)
            : (ImageHost.Bounds.Height - height) / 2 - top * _calibrationZoom;
        _calibrationOffset = new Vector(x, y);
    }

    private void ResetCalibrationZoom()
    {
        _panning = false;
        _calibrationZoom = 1;
        _calibrationOffset = default;
        ApplyCalibrationTransform();
    }

    private void ImageHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_calibrationZoom > 1.0001)
        {
            ClampCalibrationZoom();
            ApplyCalibrationTransform();
        }
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        if (OverlayCanvas is null) return;
        OverlayCanvas.Children.Clear();
        if (_pixelFrame is null) return;
        double opacity = OpacitySlider.Value;
        double width = MarkerWidthSlider.Value;
        double markerSize = MarkerSizeSlider.Value;
        PointD? scaleA = _mode == InteractionMode.Calibration ? _scaleDrawStart : _scaleStart;
        PointD? scaleB = _mode == InteractionMode.Calibration ? _scaleDrag : _scaleEnd;
        if (scaleA is not null && scaleB is not null)
        {
            Point a = ImageToCanvas(scaleA.Value), b = ImageToCanvas(scaleB.Value);
            AddLine(a, b, new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, opacity + .25)), 0, 224, 255)), Math.Max(1, width));
            AddEndpoint(a); AddEndpoint(b);
        }

        bool[] visible =
        [
            _pointStates[0] != PointState.Missing || _editingPointIndex == 0,
            _pointStates[1] != PointState.Missing || _editingPointIndex == 1
        ];
        PointD[] points = [_trackingPoints[0], _trackingPoints[1]];
        if (_editingPointIndex is int editing && _editingPoint is not null) points[editing] = _editingPoint.Value;
        double roiSize = ReadTrackingSettings().WindowSize * ImageTransform().Scale;
        for (int index = 0; index < 2; index++)
        {
            if (!visible[index]) continue;
            Point center = ImageToCanvas(points[index]);
            IBrush color = PointBrush(index);
            Rectangle roi = new()
            {
                Width = roiSize, Height = roiSize, Stroke = color, StrokeThickness = .8,
                StrokeDashArray = new AvaloniaList<double>([4, 3]), Opacity = opacity * .65
            };
            Canvas.SetLeft(roi, center.X - roiSize / 2); Canvas.SetTop(roi, center.Y - roiSize / 2);
            OverlayCanvas.Children.Add(roi);
        }
        if (visible.All(value => value))
            AddLine(ImageToCanvas(points[0]), ImageToCanvas(points[1]), Brushes.Gold, Math.Max(.9, width));
        for (int index = 0; index < 2; index++)
        {
            if (!visible[index]) continue;
            Point center = ImageToCanvas(points[index]);
            IBrush color = PointBrush(index);
            double gap = Math.Min(4, markerSize * .28);
            AddLine(new Point(center.X - markerSize, center.Y), new Point(center.X - gap, center.Y), color, width);
            AddLine(new Point(center.X + gap, center.Y), new Point(center.X + markerSize, center.Y), color, width);
            AddLine(new Point(center.X, center.Y - markerSize), new Point(center.X, center.Y - gap), color, width);
            AddLine(new Point(center.X, center.Y + gap), new Point(center.X, center.Y + markerSize), color, width);
            TextBlock label = new()
            {
                Text = $"P{index + 1}" + (_pointStates[index] == PointState.Lost ? " · 已丢失" : string.Empty),
                Foreground = color, FontWeight = FontWeight.SemiBold, FontSize = 12,
                Background = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0)), Padding = new Thickness(5, 2)
            };
            Canvas.SetLeft(label, center.X + markerSize + 5); Canvas.SetTop(label, center.Y - 14);
            OverlayCanvas.Children.Add(label);
        }
    }

    private IBrush PointBrush(int index) => _editingPointIndex == index || _pointStates[index] == PointState.Lost
        ? Brushes.Orange
        : index == 0 ? Brushes.Red : Brushes.DodgerBlue;

    private void AddLine(Point start, Point end, IBrush brush, double thickness) =>
        OverlayCanvas.Children.Add(new Line
        {
            StartPoint = start, EndPoint = end, Stroke = brush,
            StrokeThickness = thickness, StrokeLineCap = PenLineCap.Round, Opacity = OpacitySlider.Value
        });

    private void AddEndpoint(Point center)
    {
        Rectangle square = new()
        {
            Width = 7, Height = 7, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = .8
        };
        Canvas.SetLeft(square, center.X - 3.5); Canvas.SetTop(square, center.Y - 3.5);
        OverlayCanvas.Children.Add(square);
    }

    private void RefreshRecognitionWindows()
    {
        UpdateRecognitionWindowDescription(_running);
        ReplaceRecognitionBitmap(0, CreateRecognitionPatch(GetDisplayedPoint(0), ReadTrackingSettings().WindowSize));
        ReplaceRecognitionBitmap(1, CreateRecognitionPatch(GetDisplayedPoint(1), ReadTrackingSettings().WindowSize));
        UpdateRecognitionText(0, P1RecognitionStateText);
        UpdateRecognitionText(1, P2RecognitionStateText);
    }

    private void ApplyRecognitionPatches(NativeBackend.RecognitionPatches patches)
    {
        UpdateRecognitionWindowDescription(true);
        ReplaceRecognitionBitmap(
            0, CreateBitmap(
                patches.Point1, patches.Size, patches.Size, patches.Size,
                PixelFormats.Gray8, AlphaFormat.Opaque));
        ReplaceRecognitionBitmap(
            1, CreateBitmap(
                patches.Point2, patches.Size, patches.Size, patches.Size,
                PixelFormats.Gray8, AlphaFormat.Opaque));
        UpdateRecognitionText(0, P1RecognitionStateText);
        UpdateRecognitionText(1, P2RecognitionStateText);
    }

    private void UpdateRecognitionWindowDescription(bool live)
    {
        int size = ReadTrackingSettings().WindowSize;
        RecognitionWindowSizeText.Text = live
            ? $"● 实时 · OpenCV CLAHE · {size} × {size} px"
            : $"OpenCV CLAHE 追踪输入 · 稳定窗口 {size} × {size} px";
        if (live) RecognitionWindowSizeText.Foreground = ReadyStateBrush;
        else RecognitionWindowSizeText.ClearValue(TextBlock.ForegroundProperty);
    }

    private PointD? GetDisplayedPoint(int index) => _editingPointIndex == index && _editingPoint is not null
        ? _editingPoint
        : _pointStates[index] != PointState.Missing ? _trackingPoints[index] : null;

    private Bitmap? CreateRecognitionPatch(PointD? point, int requestedSize)
    {
        if (_pixelFrame is null || point is null) return null;
        int size = Math.Max(15, requestedSize | 1);
        byte[] patch = new byte[size * size];
        int half = size / 2;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double sourceX = point.Value.X + x - half;
                double sourceY = point.Value.Y + y - half;
                patch[y * size + x] = SampleTrackingGray(sourceX, sourceY);
            }
        }
        return CreateBitmap(patch, size, size, size, PixelFormats.Gray8, AlphaFormat.Opaque);
    }

    private byte SampleTrackingGray(double x, double y)
    {
        PixelFrame frame = _pixelFrame!;
        x = Math.Clamp(x, 0, frame.Width - 1d);
        y = Math.Clamp(y, 0, frame.Height - 1d);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, frame.Width - 1);
        int y1 = Math.Min(y0 + 1, frame.Height - 1);
        double fx = x - x0, fy = y - y0;
        int topLeft = frame.TrackingGray[y0 * frame.TrackingStride + x0];
        int topRight = frame.TrackingGray[y0 * frame.TrackingStride + x1];
        int bottomLeft = frame.TrackingGray[y1 * frame.TrackingStride + x0];
        int bottomRight = frame.TrackingGray[y1 * frame.TrackingStride + x1];
        double top = topLeft + (topRight - topLeft) * fx;
        double bottom = bottomLeft + (bottomRight - bottomLeft) * fx;
        return (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy), 0, 255);
    }

    private void ReplaceRecognitionBitmap(int index, Bitmap? bitmap)
    {
        Image image = index == 0 ? P1RecognitionImage : P2RecognitionImage;
        Bitmap? old = index == 0 ? _recognitionP1 : _recognitionP2;
        if (index == 0) _recognitionP1 = bitmap; else _recognitionP2 = bitmap;
        image.Source = bitmap;
        bool pixel = (RecognitionScalingCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Smooth";
        RenderOptions.SetBitmapInterpolationMode(image,
            pixel ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        old?.Dispose();
    }

    private void RecognitionScaling_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_uiReady) RefreshRecognitionWindows();
    }

    private void UpdateRecognitionText(int index, TextBlock text)
    {
        PointD? point = GetDisplayedPoint(index);
        if (point is null)
        {
            text.Text = "未选择";
            text.ClearValue(TextBlock.ForegroundProperty);
            return;
        }
        string status = _currentMeasurement is null
            ? "已确认"
            : MeasurementCsv.StatusName(_currentMeasurement.Statuses[index]);
        double quality = _currentMeasurement?.Qualities[index] ?? double.NaN;
        text.Text = double.IsFinite(quality)
            ? $"({point.Value.X:F2}, {point.Value.Y:F2}) px · {status} · q={quality:F3}"
            : $"({point.Value.X:F2}, {point.Value.Y:F2}) px · {status}";
        text.Foreground = _editingPointIndex == index || _pointStates[index] == PointState.Lost
            ? WarningStateBrush
            : ReadyStateBrush;
    }

    private void UpdateDistance(double pixels)
    {
        if (!double.IsFinite(pixels))
        {
            DistanceText.Text = "实际距离：当前帧无有效测量";
            PixelDistanceText.Text = "像素距离：—";
            return;
        }
        PixelDistanceText.Text = $"像素距离：{pixels:F5} px";
        DistanceText.Text = _unitPerPixel is null
            ? "实际距离：比例尺未设置"
            : $"实际距离：{pixels * _unitPerPixel.Value:G10} {_unit}";
    }

    private void UpdateControls()
    {
        bool images = _imagePaths.Count >= 2;
        bool pointsReady = _pointStates.All(state => state == PointState.Ready);
        bool initialPointsReady = _initialPointReady.All(ready => ready);
        StartButton.IsEnabled = !_starting && !_running && images && _unitPerPixel is not null &&
            pointsReady && initialPointsReady && _mode == InteractionMode.None;
        CalibrateButton.IsEnabled = !_starting && !_running && images && _editingPointIndex is null;
        bool canEdit = !_starting && images && _mode != InteractionMode.Calibration &&
            (!_running || (_paused && _pauseConfirmed));
        Point1Button.IsEnabled = canEdit && (_editingPointIndex is null or 0);
        Point2Button.IsEnabled = canEdit && (_editingPointIndex is null or 1);
        PauseButton.IsEnabled = _running && _editingPointIndex is null && (!_paused || pointsReady);
        PauseButton.Content = _paused ? "继续" : "暂停";
        StopButton.IsEnabled = _running;
        ResetButton.IsEnabled = !_starting && !_running && images;
    }

    private void UpdatePointUiState()
    {
        bool allReady = _pointStates.All(state => state == PointState.Ready);
        bool scaleReady = _mode != InteractionMode.Calibration && _unitPerPixel is not null;
        ScaleStateText.Text = _mode == InteractionMode.Calibration
            ? $"● 比例尺调整中 · {_calibrationZoom:F2}×"
            : scaleReady ? $"● 已标定 · 1 px = {_unitPerPixel!.Value:G6} {_unit}" : "● 比例尺未设置";
        ScaleStateText.Foreground = _mode == InteractionMode.Calibration
            ? ActiveStateBrush
            : scaleReady ? ReadyStateBrush : WarningStateBrush;
        CalibrateButton.Content = _mode == InteractionMode.Calibration
            ? "确认比例尺  ↵"
            : scaleReady ? "✓ 比例尺" : "1  标定比例尺";
        if (_editingPointIndex is int editing)
        {
            PointStateText.Text = $"● 正在调整 P{editing + 1}";
            PointStateText.Foreground = ActiveStateBrush;
            InteractionHint.Text = $"调整 P{editing + 1}：点击、拖动或方向键；按钮/Enter 确认";
        }
        else
        {
            int[] lost = Enumerable.Range(0, 2).Where(i => _pointStates[i] == PointState.Lost).ToArray();
            int[] missing = Enumerable.Range(0, 2).Where(i => _pointStates[i] == PointState.Missing).ToArray();
            if (lost.Length > 0)
            {
                string names = string.Join('/', lost.Select(i => $"P{i + 1}"));
                PointStateText.Text = $"● 识别点丢失：{names}";
                PointStateText.Foreground = WarningStateBrush;
                InteractionHint.Text = $"点击黄色 {names} 按钮修正";
            }
            else if (missing.Length > 0)
            {
                string names = string.Join('/', missing.Select(i => $"P{i + 1}"));
                PointStateText.Text = $"● 待选择：{names}";
                PointStateText.Foreground = WarningStateBrush;
                InteractionHint.Text = $"请选择 {names}";
            }
            else
            {
                PointStateText.Text = "● P1/P2 已确认";
                PointStateText.Foreground = ReadyStateBrush;
                InteractionHint.Text = _running && _paused ? "选点已准备；可继续追踪" : "可以开始追踪";
            }
        }
        Point1Button.Content = _editingPointIndex == 0 ? "确认 P1  ↵" : _pointStates[0] switch
        {
            PointState.Ready => "✓ P1", PointState.Lost => "! 修正 P1", _ => "2  选择 P1"
        };
        Point2Button.Content = _editingPointIndex == 1 ? "确认 P2  ↵" : _pointStates[1] switch
        {
            PointState.Ready => "✓ P2", PointState.Lost => "! 修正 P2", _ => "选择 P2"
        };
        StepBadge.Text = _mode == InteractionMode.Calibration
            ? "步骤 1 / 3 · 调整比例尺"
            : _unitPerPixel is null ? "步骤 1 / 3 · 标定比例尺"
            : allReady ? "步骤 3 / 3 · 准备追踪" : "步骤 2 / 3 · 确认选点";
        RefreshRecognitionWindows();
    }

    private static readonly IBrush WarningStateBrush = new SolidColorBrush(Color.Parse("#D07A00"));
    private static readonly IBrush ActiveStateBrush = new SolidColorBrush(Color.Parse("#0A84FF"));
    private static readonly IBrush ReadyStateBrush = new SolidColorBrush(Color.Parse("#22A447"));

    private void SetStatus(string text, bool warning, bool success = false)
    {
        StatusText.Text = text;
        if (warning) StatusText.Foreground = WarningStateBrush;
        else if (success) StatusText.Foreground = ReadyStateBrush;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }
}
