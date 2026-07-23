#pragma once

#include <cstdint>

#if defined(_WIN32)
#  if defined(DISTANCE_TRACKER_NATIVE_EXPORTS)
#    define DT_EXPORT __declspec(dllexport)
#  else
#    define DT_EXPORT __declspec(dllimport)
#  endif
#else
#  define DT_EXPORT __attribute__((visibility("default")))
#endif

#define DT_API extern "C" DT_EXPORT

struct DtPoint {
    double x;
    double y;
};

struct DtTrackerSettings {
    int windowSize;
    int searchRadius;
    double referenceThreshold;
    double forwardBackwardThreshold;
};

struct DtTrackResult {
    DtPoint points[2];
    double qualities[2];
    int statuses[2];
    int valid[2];
};

enum DtDisplayMethod {
    DT_LINEAR_CONTRAST = 0,
    DT_ORIGINAL = 1,
    DT_HISTOGRAM_EQUALIZATION = 2,
    DT_CLAHE = 3,
    DT_NLM_CLAHE = 4,
    DT_RETINEX = 5,
    DT_GAMMA = 6,
};

enum DtTrackStatus {
    DT_LOST = 0,
    DT_LK = 1,
    DT_TEMPLATE = 2,
    DT_REFERENCE = 3,
    DT_KEYFRAME = 4,
    DT_MANUAL = 5,
};

DT_API int dt_abi_version();
DT_API const char* dt_last_error();

DT_API void* dt_image_open(const char* utf8Path, int* width, int* height);
DT_API void dt_image_close(void* image);
DT_API int dt_image_render(
    void* image,
    int displayMethod,
    double contrastClipPercent,
    double gamma,
    double denoiseStrength,
    double retinexSigma,
    std::uint8_t* destinationBgra,
    int destinationStride);
DT_API int dt_image_render_tracking_input(
    void* image,
    std::uint8_t* destinationGray,
    int destinationStride);

DT_API void* dt_tracker_create(
    const char* firstImageUtf8Path,
    const DtPoint* initialPoints,
    const DtTrackerSettings* settings);
DT_API void dt_tracker_destroy(void* tracker);
DT_API int dt_tracker_step(
    void* tracker,
    const char* imageUtf8Path,
    const DtTrackerSettings* settings,
    DtTrackResult* result);
DT_API void* dt_frame_prepare(const char* imageUtf8Path, int* width, int* height);
DT_API void dt_frame_prepare_destroy(void* preparedFrame);
DT_API int dt_tracker_step_prepared(
    void* tracker,
    void* preparedFrame,
    const DtTrackerSettings* settings,
    DtTrackResult* result);
DT_API int dt_tracker_reselect(void* tracker, int pointIndex, DtPoint point);
DT_API void* dt_tracker_snapshot_create(void* tracker, int* width, int* height);
DT_API void dt_tracker_snapshot_destroy(void* snapshot);
DT_API int dt_tracker_snapshot_render(
    void* snapshot,
    int displayMethod,
    double contrastClipPercent,
    double gamma,
    double denoiseStrength,
    double retinexSigma,
    std::uint8_t* destinationPreviewBgra,
    int previewStride,
    std::uint8_t* destinationTrackingGray,
    int trackingStride);
DT_API int dt_tracker_self_test();
