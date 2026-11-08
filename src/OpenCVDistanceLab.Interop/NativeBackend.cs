using System.Runtime.InteropServices;
using OpenCVDistanceLab.Core;

namespace OpenCVDistanceLab.Interop;

public static class NativeBackend
{
    private const string Library = "distance_tracker_native";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public double X;
        public double Y;

        public NativePoint(double x, double y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSettings
    {
        public int WindowSize;
        public int SearchRadius;
        public double ReferenceThreshold;
        public double ForwardBackwardThreshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFilter
    {
        public int Method;
        public double ContrastClipPercent;
        public double Gamma;
        public double DenoiseStrength;
        public double RetinexSigma;

        public NativeFilter(DisplaySettings settings)
        {
            Method = (int)settings.Method;
            ContrastClipPercent = settings.ContrastClip;
            Gamma = settings.Gamma;
            DenoiseStrength = settings.DenoiseStrength;
            RetinexSigma = settings.RetinexSigma;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult
    {
        public NativePoint Point0;
        public NativePoint Point1;
        public double Quality0;
        public double Quality1;
        public int Status0;
        public int Status1;
        public int Valid0;
        public int Valid1;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_last_error();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_tracker_create_prepared(
        nint firstFrame,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] NativePoint[] points,
        ref NativeSettings settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_tracker_destroy(nint tracker);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_step_filtered(
        nint tracker, nint prepared, ref NativeFilter filter, ref NativeSettings settings, out NativeResult result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_frame_prepare_filtered(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref NativeFilter filter, out int width, out int height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_frame_prepare_destroy(nint prepared);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_reselect(nint tracker, int index, NativePoint point);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_tracker_snapshot_create(nint tracker, out int width, out int height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_tracker_snapshot_destroy(nint snapshot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_filtered_frame_render(
        nint frame, nint preview, int previewStride, nint tracking, int trackingStride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_snapshot_render_patches(
        nint snapshot,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] NativePoint[] points,
        int patchSize,
        nint point1, int point1Stride,
        nint point2, int point2Stride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_filtered_tracker_self_test();

    public static string LastError =>
        Marshal.PtrToStringUTF8(dt_last_error()) ?? "未知的 OpenCV 原生错误。";

    public static void SelfTest()
    {
        int version = dt_abi_version();
        if (version != 1) throw new InvalidOperationException($"不兼容的原生接口版本：{version}。");
        try
        {
            if (dt_filtered_tracker_self_test() == 0) throw new InvalidOperationException(LastError);
        }
        catch (EntryPointNotFoundException error)
        {
            throw new InvalidOperationException("请更新配套的 OpenCV 原生追踪库以使用图像滤镜。", error);
        }
    }

    public static PreparedFrame PrepareFrame(string path, DisplaySettings settings)
    {
        NativeFilter filter = new(settings);
        nint handle = dt_frame_prepare_filtered(path, ref filter, out int width, out int height);
        if (handle == 0)
            throw new InvalidOperationException(
                $"无法读取图像“{Path.GetFileName(path)}”。{Environment.NewLine}" +
                $"完整路径：{path}{Environment.NewLine}{LastError}");
        return new PreparedFrame(handle, width, height);
    }

    public static PixelFrame RenderFrame(string path, DisplaySettings settings)
    {
        using PreparedFrame prepared = PrepareFrame(path, settings);
        return prepared.Render();
    }

    private static PixelFrame RenderPixels(nint handle, int width, int height)
    {
        int previewStride = checked(width * 4);
        byte[] preview = new byte[checked(previewStride * height)];
        byte[] tracking = new byte[checked(width * height)];
        WithPinnedBuffers(preview, tracking, (previewPointer, trackingPointer) =>
        {
            if (dt_filtered_frame_render(handle, previewPointer, previewStride, trackingPointer, width) == 0)
                throw new InvalidOperationException(LastError);
        });
        return new PixelFrame(width, height, previewStride, preview, width, tracking);
    }

    public sealed class Tracker : IDisposable
    {
        private nint _handle;

        public Tracker(PreparedFrame firstFrame, IReadOnlyList<PointD> points, TrackingSettings settings)
        {
            if (points.Count != 2) throw new ArgumentException("追踪器需要两个初始点。", nameof(points));
            NativePoint[] nativePoints = [new(points[0].X, points[0].Y), new(points[1].X, points[1].Y)];
            NativeSettings nativeSettings = ToNative(settings);
            _handle = dt_tracker_create_prepared(firstFrame.DangerousHandle, nativePoints, ref nativeSettings);
            if (_handle == 0) throw new InvalidOperationException(LastError);
        }

        public TrackMeasurement Step(PreparedFrame prepared, TrackingSettings settings, DisplaySettings filter)
        {
            ThrowIfDisposed();
            NativeSettings nativeSettings = ToNative(settings);
            NativeFilter nativeFilter = new(filter);
            if (dt_tracker_step_filtered(
                    _handle, prepared.DangerousHandle, ref nativeFilter, ref nativeSettings, out NativeResult result) == 0)
                throw new InvalidOperationException(LastError);
            return FromNative(result);
        }

        public void Reselect(int index, PointD point)
        {
            ThrowIfDisposed();
            if (dt_tracker_reselect(_handle, index, new NativePoint(point.X, point.Y)) == 0)
                throw new InvalidOperationException(LastError);
        }

        public TrackerSnapshot Snapshot()
        {
            ThrowIfDisposed();
            nint snapshot = dt_tracker_snapshot_create(_handle, out int width, out int height);
            if (snapshot == 0) throw new InvalidOperationException(LastError);
            return new TrackerSnapshot(snapshot, width, height);
        }

        public void Dispose()
        {
            if (_handle == 0) return;
            dt_tracker_destroy(_handle);
            _handle = 0;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);

        ~Tracker() => Dispose();
    }

    public sealed class PreparedFrame : IDisposable
    {
        private nint _handle;
        internal nint DangerousHandle
        {
            get
            {
                ObjectDisposedException.ThrowIf(_handle == 0, this);
                return _handle;
            }
        }

        public int Width { get; }
        public int Height { get; }

        internal PreparedFrame(nint handle, int width, int height)
        {
            _handle = handle;
            Width = width;
            Height = height;
        }

        public PixelFrame Render() => RenderPixels(DangerousHandle, Width, Height);

        public void Dispose()
        {
            if (_handle == 0) return;
            dt_frame_prepare_destroy(_handle);
            _handle = 0;
            GC.SuppressFinalize(this);
        }

        ~PreparedFrame() => Dispose();
    }

    public sealed class TrackerSnapshot : IDisposable
    {
        private nint _handle;
        private readonly int _width;
        private readonly int _height;

        internal TrackerSnapshot(nint handle, int width, int height)
        {
            _handle = handle;
            _width = width;
            _height = height;
        }

        public PixelFrame Render()
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return RenderPixels(_handle, _width, _height);
        }

        public RecognitionPatches RenderRecognitionPatches(
            IReadOnlyList<PointD> points, int requestedSize)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            if (points.Count != 2)
                throw new ArgumentException("实时识别预览需要两个追踪点。", nameof(points));
            int size = Math.Max(15, requestedSize) | 1;
            NativePoint[] nativePoints =
            [
                new(points[0].X, points[0].Y),
                new(points[1].X, points[1].Y)
            ];
            byte[] point1 = new byte[checked(size * size)];
            byte[] point2 = new byte[checked(size * size)];
            WithPinnedBuffers(point1, point2, (point1Pointer, point2Pointer) =>
            {
                if (dt_tracker_snapshot_render_patches(
                        _handle, nativePoints, size,
                        point1Pointer, size, point2Pointer, size) == 0)
                    throw new InvalidOperationException(LastError);
            });
            return new RecognitionPatches(size, point1, point2);
        }

        public void Dispose()
        {
            if (_handle == 0) return;
            dt_tracker_snapshot_destroy(_handle);
            _handle = 0;
            GC.SuppressFinalize(this);
        }

        ~TrackerSnapshot() => Dispose();
    }

    public sealed record RecognitionPatches(int Size, byte[] Point1, byte[] Point2);

    private static TrackMeasurement FromNative(NativeResult result) => new(
        [new PointD(result.Point0.X, result.Point0.Y), new PointD(result.Point1.X, result.Point1.Y)],
        [result.Quality0, result.Quality1],
        [(TrackStatus)result.Status0, (TrackStatus)result.Status1],
        [result.Valid0 != 0, result.Valid1 != 0]);

    private static NativeSettings ToNative(TrackingSettings settings)
    {
        settings = settings.Normalize();
        return new NativeSettings
        {
            WindowSize = settings.WindowSize,
            SearchRadius = settings.SearchRadius,
            ReferenceThreshold = settings.ReferenceThreshold,
            ForwardBackwardThreshold = settings.ForwardBackwardThreshold
        };
    }

    private static void WithPinnedBuffers(
        byte[] first, byte[] second, Action<nint, nint> operation)
    {
        GCHandle firstPin = GCHandle.Alloc(first, GCHandleType.Pinned);
        GCHandle secondPin = GCHandle.Alloc(second, GCHandleType.Pinned);
        try { operation(firstPin.AddrOfPinnedObject(), secondPin.AddrOfPinnedObject()); }
        finally
        {
            secondPin.Free();
            firstPin.Free();
        }
    }
}
