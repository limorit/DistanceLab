#include "distance_tracker_native.h"

#include <array>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

namespace {

std::string utf8Path(const std::filesystem::path& path) {
    const auto encoded = path.u8string();
    return {reinterpret_cast<const char*>(encoded.data()), encoded.size()};
}

bool testUnicodeImageIo() {
    const auto stamp = std::chrono::steady_clock::now().time_since_epoch().count();
    const auto directory = std::filesystem::temp_directory_path() /
                           ("distance_tracker_native_" + std::to_string(stamp));
    const auto imagePath = directory / std::filesystem::path(u8"测试帧图像.pgm");
    std::error_code ignored;
    std::filesystem::create_directories(directory);

    constexpr int expectedWidth = 48;
    constexpr int expectedHeight = 36;
    std::vector<std::uint8_t> pixels(expectedWidth * expectedHeight);
    for (std::size_t index = 0; index < pixels.size(); ++index) {
        pixels[index] = static_cast<std::uint8_t>(1 + index % 251);
    }
    {
        std::ofstream stream(imagePath, std::ios::binary);
        stream << "P5\n" << expectedWidth << ' ' << expectedHeight << "\n255\n";
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
    std::filesystem::remove_all(directory, ignored);
    if (!rendered || !patchesRendered)
        std::cerr << "Image or recognition patch render failed: " << dt_last_error() << '\n';
    return rendered && patchesRendered;
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
