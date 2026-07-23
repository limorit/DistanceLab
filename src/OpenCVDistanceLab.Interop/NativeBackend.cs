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
    private static extern nint dt_image_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out int width, out int height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_image_close(nint image);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_image_render(
        nint image, int method, double contrastClip, double gamma,
        double denoiseStrength, double retinexSigma, nint destination, int stride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_image_render_tracking_input(
        nint image, nint destination, int stride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_tracker_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string firstImage,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] NativePoint[] points,
        ref NativeSettings settings);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_tracker_destroy(nint tracker);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_step_prepared(
        nint tracker, nint prepared, ref NativeSettings settings, out NativeResult result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_frame_prepare(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out int width, out int height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_frame_prepare_destroy(nint prepared);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_reselect(nint tracker, int index, NativePoint point);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dt_tracker_snapshot_create(nint tracker, out int width, out int height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_tracker_snapshot_destroy(nint snapshot);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_snapshot_render(
        nint snapshot, int method, double contrastClip, double gamma,
        double denoiseStrength, double retinexSigma,
        nint preview, int previewStride, nint tracking, int trackingStride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dt_tracker_self_test();

    public static string LastError =>
        Marshal.PtrToStringUTF8(dt_last_error()) ?? "未知的 OpenCV 原生错误。";

    public static void SelfTest()
    {
        int version = dt_abi_version();
        if (version != 1) throw new InvalidOperationException($"不兼容的原生接口版本：{version}。");
        if (dt_tracker_self_test() == 0) throw new InvalidOperationException(LastError);
    }

    public static PreparedFrame PrepareFrame(string path)
    {
        nint handle = dt_frame_prepare(path, out int width, out int height);
        if (handle == 0)
            throw new InvalidOperationException(
                $"无法读取图像“{Path.GetFileName(path)}”。{Environment.NewLine}" +
                $"完整路径：{path}{Environment.NewLine}{LastError}");
        return new PreparedFrame(handle, width, height);
    }

    public static PixelFrame RenderFrame(string path, DisplaySettings settings)
    {
        nint image = dt_image_open(path, out int width, out int height);
        if (image == 0) throw new InvalidOperationException(LastError);
        try
        {
            int previewStride = checked(width * 4);
            int trackingStride = width;
            byte[] preview = new byte[checked(previewStride * height)];
            byte[] tracking = new byte[checked(trackingStride * height)];
            WithPinnedBuffers(preview, tracking, (previewPointer, trackingPointer) =>
            {
                if (dt_image_render(
                        image, (int)settings.Method, settings.ContrastClip, settings.Gamma,
                        settings.DenoiseStrength, settings.RetinexSigma,
                        previewPointer, previewStride) == 0 ||
                    dt_image_render_tracking_input(image, trackingPointer, trackingStride) == 0)
                    throw new InvalidOperationException(LastError);
            });
            return new PixelFrame(width, height, previewStride, preview, trackingStride, tracking);
        }
        finally { dt_image_close(image); }
    }

    public sealed class Tracker : IDisposable
    {
        private nint _handle;

        public Tracker(string firstImage, IReadOnlyList<PointD> points, TrackingSettings settings)
        {
            if (points.Count != 2) throw new ArgumentException("追踪器需要两个初始点。", nameof(points));
            NativePoint[] nativePoints = [new(points[0].X, points[0].Y), new(points[1].X, points[1].Y)];
            NativeSettings nativeSettings = ToNative(settings);
            _handle = dt_tracker_create(firstImage, nativePoints, ref nativeSettings);
            if (_handle == 0) throw new InvalidOperationException(LastError);
        }

        public TrackMeasurement Step(PreparedFrame prepared, TrackingSettings settings)
        {
            ThrowIfDisposed();
            NativeSettings nativeSettings = ToNative(settings);
            if (dt_tracker_step_prepared(
                    _handle, prepared.DangerousHandle, ref nativeSettings, out NativeResult result) == 0)
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

        public PixelFrame Render(DisplaySettings settings)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            int previewStride = checked(_width * 4);
            int trackingStride = _width;
            byte[] preview = new byte[checked(previewStride * _height)];
            byte[] tracking = new byte[checked(trackingStride * _height)];
            WithPinnedBuffers(preview, tracking, (previewPointer, trackingPointer) =>
            {
                if (dt_tracker_snapshot_render(
                        _handle, (int)settings.Method, settings.ContrastClip, settings.Gamma,
                        settings.DenoiseStrength, settings.RetinexSigma,
                        previewPointer, previewStride, trackingPointer, trackingStride) == 0)
                    throw new InvalidOperationException(LastError);
            });
            return new PixelFrame(
                _width, _height, previewStride, preview, trackingStride, tracking);
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
