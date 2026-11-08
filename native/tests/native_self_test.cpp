#include "distance_tracker_native.h"

#include <array>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <string>
#include <vector>

namespace {

std::string utf8Path(const std::filesystem::path& path) {
    const auto encoded = path.u8string();
    return {reinterpret_cast<const char*>(encoded.data()), encoded.size()};
}

// Compare independently rendered previews with the pixels used by both LK points.
// Reuse an original-mode prefetch to exercise a filter change after a frame is queued.
bool testTrackingFilters(const std::string& path, int width, int height) {
    const DtFilterSettings original{DT_ORIGINAL, 2, 1, 5, 8};
    const DtPoint points[2]{{22, 24}, {54, 42}};
    const DtTrackerSettings settings{21, 12, 0.48, 1.5};
    using Frame = std::unique_ptr<void, decltype(&dt_frame_prepare_destroy)>;
    Frame firstFrame(dt_frame_prepare_filtered(path.c_str(), &original, nullptr, nullptr), dt_frame_prepare_destroy);
    Frame queued(dt_frame_prepare_filtered(path.c_str(), &original, nullptr, nullptr), dt_frame_prepare_destroy);
    std::unique_ptr<void, decltype(&dt_tracker_destroy)> tracker(
        dt_tracker_create_prepared(firstFrame.get(), points, &settings), dt_tracker_destroy);
    std::unique_ptr<void, decltype(&dt_image_close)> image(
        dt_image_open(path.c_str(), nullptr, nullptr), dt_image_close);
    if (!queued || !tracker || !image) return false;

    std::vector<std::uint8_t> expected(width * height * 4), preview(expected.size());
    std::vector<std::uint8_t> gray(width * height), rawGray(gray.size()), previousGray;
    if (!dt_filtered_frame_render(queued.get(), preview.data(), width * 4, rawGray.data(), width)) return false;
    const DtFilterSettings filters[]{
        original, {DT_LINEAR_CONTRAST, 1, 1, 5, 8}, {DT_LINEAR_CONTRAST, 8, 1, 5, 8},
        {DT_HISTOGRAM_EQUALIZATION, 2, 1, 5, 8}, {DT_CLAHE, 2, 1, 5, 8},
        {DT_NLM_CLAHE, 2, 1, 5, 8}, {DT_NLM_CLAHE, 2, 1, 23, 8},
        {DT_RETINEX, 2, 1, 5, 5}, {DT_RETINEX, 2, 1, 5, 18},
        {DT_GAMMA, 2, 0.55, 5, 8}, {DT_GAMMA, 2, 2.1, 5, 8}
    };
    int previousMethod = -1;
    for (const DtFilterSettings& filter : filters) {
        if (!dt_image_render(image.get(), filter.method, filter.contrastClipPercent, filter.gamma,
                             filter.denoiseStrength, filter.retinexSigma, expected.data(), width * 4)) return false;
        Frame selected(dt_frame_prepare_filtered(path.c_str(), &filter, nullptr, nullptr), dt_frame_prepare_destroy);
        if (!selected || !dt_filtered_frame_render(selected.get(), preview.data(), width * 4, gray.data(), width) ||
            preview != expected) return false;
        for (std::size_t i = 0; i < gray.size(); ++i) {
            const auto* color = expected.data() + 4 * i;
            if (filter.method == DT_ORIGINAL) {
                // The fixture is a color PPM. LK uses luminance and the preview retains color.
                const double intensity = 0.114 * color[0] + 0.587 * color[1] + 0.299 * color[2];
                if (std::abs(gray[i] - intensity) > 1.0) return false;
            } else if (color[0] != gray[i] || color[1] != gray[i] || color[2] != gray[i]) {
                return false;
            }
        }
        if (filter.method != DT_ORIGINAL && gray == rawGray) return false;
        if (filter.method == previousMethod && gray == previousGray) return false;
        previousMethod = filter.method;
        previousGray = gray;
        DtTrackResult result{};
        if (!dt_tracker_step_filtered(tracker.get(), queued.get(), &filter, &settings, &result)) return false;
        std::unique_ptr<void, decltype(&dt_tracker_snapshot_destroy)> snapshot(
            dt_tracker_snapshot_create(tracker.get(), nullptr, nullptr), dt_tracker_snapshot_destroy);
        std::vector<std::uint8_t> actual(gray.size());
        if (!snapshot || !dt_filtered_frame_render(snapshot.get(), preview.data(), width * 4, actual.data(), width) ||
            preview != expected || actual != gray) return false;
        std::uint8_t first = 0, second = 0;
        if (!dt_tracker_snapshot_render_patches(snapshot.get(), points, 1, &first, 1, &second, 1)) return false;
        for (int i = 0; i < 2; ++i) {
            const auto pixel = static_cast<int>(points[i].y) * width + static_cast<int>(points[i].x);
            if ((i == 0 ? first : second) != gray[pixel] || !result.valid[i] || result.qualities[i] < 0.999 ||
                std::hypot(result.points[i].x - points[i].x, result.points[i].y - points[i].y) > 0.02)
                return false;
        }
        if (filter.method == DT_CLAHE && !dt_tracker_reselect(tracker.get(), 0, points[0])) return false;
    }
    return true;
}

bool testUnicodeImageIo() {
    const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
    const auto directory = std::filesystem::temp_directory_path() /
                           ("distance_tracker_native_" + std::to_string(stamp));
    const auto imagePath = directory / std::filesystem::path(u8"测试帧图像.ppm");
    std::error_code ignored;
    std::filesystem::create_directories(directory);

    constexpr int expectedWidth = 80;
    constexpr int expectedHeight = 68;
    std::vector<std::uint8_t> pixels(expectedWidth * expectedHeight * 3);
    std::uint32_t state = 317;
    for (auto& pixel : pixels) {
        state = 1664525u * state + 1013904223u;
        pixel = static_cast<std::uint8_t>(35 + (state >> 24) % 180);
    }
    {
        std::ofstream stream(imagePath, std::ios::binary);
        stream << "P6\n" << expectedWidth << ' ' << expectedHeight << "\n255\n";
        stream.write(reinterpret_cast<const char*>(pixels.data()),
                     static_cast<std::streamsize>(pixels.size()));
        if (!stream) {
            std::cerr << "Could not create the Unicode-path image fixture.\n";
            std::filesystem::remove_all(directory, ignored);
            return false;
        }
    }

    int width = 0;
    int height = 0;
    const std::string encodedPath = utf8Path(imagePath);
    void* image = dt_image_open(encodedPath.c_str(), &width, &height);
    if (image == nullptr || width != expectedWidth || height != expectedHeight) {
        std::cerr << "Unicode-path image open failed: " << dt_last_error() << '\n';
        if (image != nullptr) dt_image_close(image);
        std::filesystem::remove_all(directory, ignored);
        return false;
    }

    std::vector<std::uint8_t> preview(width * height * 4);
    std::vector<std::uint8_t> tracking(width * height);
    const bool rendered =
        dt_image_render(image, DT_ORIGINAL, 1.0, 1.0, 5.0, 80.0,
                        preview.data(), width * 4) != 0 &&
        dt_image_render_tracking_input(image, tracking.data(), width) != 0;
    dt_image_close(image);
    DtPoint points[2]{{18.0, 18.0}, {29.0, 18.0}};
    DtTrackerSettings settings{15, 10, 0.48, 1.5};
    void* tracker = dt_tracker_create(encodedPath.c_str(), points, &settings);
    int snapshotWidth = 0;
    int snapshotHeight = 0;
    void* snapshot = tracker == nullptr
        ? nullptr
        : dt_tracker_snapshot_create(tracker, &snapshotWidth, &snapshotHeight);
    constexpr int patchSize = 15;
    std::array<std::uint8_t, patchSize * patchSize> point1{};
    std::array<std::uint8_t, patchSize * patchSize> point2{};
    const bool patchesRendered =
        snapshot != nullptr &&
        dt_tracker_snapshot_render_patches(
            snapshot, points, patchSize,
            point1.data(), patchSize, point2.data(), patchSize) != 0 &&
        snapshotWidth == expectedWidth && snapshotHeight == expectedHeight;
    if (snapshot != nullptr) dt_tracker_snapshot_destroy(snapshot);
    if (tracker != nullptr) dt_tracker_destroy(tracker);
    const bool filtersPassed = testTrackingFilters(encodedPath, width, height);
    if (!filtersPassed) std::cerr << "Selected tracking filter test failed: " << dt_last_error() << '\n';
    std::filesystem::remove_all(directory, ignored);
    if (!rendered || !patchesRendered)
        std::cerr << "Image or recognition patch render failed: " << dt_last_error() << '\n';
    return rendered && patchesRendered && filtersPassed;
}

} // namespace

int main() {
    if (dt_abi_version() != 1) {
        std::cerr << "Unexpected native ABI version.\n";
        return 2;
    }
    if (dt_tracker_self_test() == 0) {
        std::cerr << dt_last_error() << '\n';
        return 1;
    }
    if (!testUnicodeImageIo()) return 1;
    std::cout << "distance_tracker_native self-test: OK\n";
    return 0;
}
