#include "distance_tracker_native.h"

#include <opencv2/opencv.hpp>
#include <opencv2/photo.hpp>
#include <opencv2/video/tracking.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#endif

namespace {

thread_local std::string g_lastError;
// OpenCV defaults to every logical CPU. On high-core hybrid processors that
// oversubscribes these relatively small tracking jobs. A bounded half-CPU pool
// (up to eight workers) benchmarks faster while leaving capacity for prefetch/UI.
const int g_openCvThreadCount = [] {
    const int threads = std::clamp(cv::getNumberOfCPUs() / 2, 1, 8);
    cv::setNumThreads(threads);
    return threads;
}();

void setError(std::string message) { g_lastError = std::move(message); }

std::filesystem::path pathFromUtf8(const char* path) {
    if (!path || !*path) throw std::runtime_error("Image path is empty.");
#if defined(_WIN32)
    const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, nullptr, 0);
    if (length <= 0) throw std::runtime_error("Image path is not valid UTF-8.");
    std::wstring wide(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide.data(), length);
    wide.pop_back();
    return std::filesystem::path(wide);
#else
    return std::filesystem::path(path);
#endif
}

cv::Mat readImage(const char* path) {
    const std::filesystem::path imagePath = pathFromUtf8(path);
    constexpr int maxAttempts = 3;
    std::string lastFailure = "Unknown image read failure.";
    for (int attempt = 0; attempt < maxAttempts; ++attempt) {
        try {
            std::ifstream stream(imagePath, std::ios::binary);
            if (!stream) throw std::runtime_error("Unable to open image file.");
            stream.seekg(0, std::ios::end);
            const std::streamoff size = stream.tellg();
            if (size <= 0) throw std::runtime_error("Image file is empty.");
            if (static_cast<std::uintmax_t>(size) >
                static_cast<std::uintmax_t>(std::numeric_limits<std::size_t>::max())) {
                throw std::runtime_error("Image file is too large to read.");
            }
            stream.seekg(0, std::ios::beg);
            const auto byteCount = static_cast<std::size_t>(size);
            std::vector<unsigned char> bytes(byteCount);
            stream.read(
                reinterpret_cast<char*>(bytes.data()),
                static_cast<std::streamsize>(byteCount));
            const auto bytesRead = static_cast<std::size_t>(stream.gcount());
            if (bytesRead != byteCount) {
                std::ostringstream message;
                message << "Incomplete image read: expected " << byteCount
                        << " bytes, received " << bytesRead << '.';
                throw std::runtime_error(message.str());
            }
            cv::Mat image = cv::imdecode(bytes, cv::IMREAD_COLOR);
            if (image.empty()) throw std::runtime_error("OpenCV decoder returned an empty image.");
            return image;
        } catch (const cv::Exception& error) {
            lastFailure = std::string("OpenCV decoder error: ") + error.what();
        } catch (const std::exception& error) {
            lastFailure = error.what();
        }
        if (attempt + 1 < maxAttempts) {
            std::this_thread::sleep_for(std::chrono::milliseconds(75 * (attempt + 1)));
        }
    }
    throw std::runtime_error(lastFailure + " Image read failed after 3 attempts.");
}

cv::Mat percentileStretch(const cv::Mat& gray, double clipPercent) {
    clipPercent = std::clamp(clipPercent, 0.0, 20.0);
    std::array<int, 256> histogram{};
    for (int y = 0; y < gray.rows; ++y) {
        const auto* row = gray.ptr<unsigned char>(y);
        for (int x = 0; x < gray.cols; ++x) ++histogram[row[x]];
    }
    const int target = static_cast<int>(gray.total() * clipPercent / 100.0);
    int low = 0, high = 255, cumulative = 0;
    while (low < 255 && cumulative + histogram[low] <= target) cumulative += histogram[low++];
    cumulative = 0;
    while (high > 0 && cumulative + histogram[high] <= target) cumulative += histogram[high--];
    if (high <= low) return gray.clone();
    cv::Mat output;
    gray.convertTo(output, CV_8U, 255.0 / (high - low), -low * 255.0 / (high - low));
    return output;
}

cv::Mat retinex(const cv::Mat& gray, double sigma) {
    cv::Mat source;
    gray.convertTo(source, CV_32F, 1.0 / 255.0);
    cv::Mat illumination;
    cv::GaussianBlur(source, illumination, cv::Size(), std::clamp(sigma, 1.0, 250.0));
    cv::Mat logarithmSource, logarithmIllumination;
    cv::log(source + 1.0f / 255.0f, logarithmSource);
    cv::log(illumination + 1.0f / 255.0f, logarithmIllumination);
    cv::Mat result = logarithmSource - logarithmIllumination;
    cv::normalize(result, result, 0.0, 255.0, cv::NORM_MINMAX);
    result.convertTo(result, CV_8U);
    return result;
}

cv::Mat enhance(
    const cv::Mat& bgr, int method, double contrastClip,
    double gamma, double denoiseStrength, double retinexSigma) {
    if (method == DT_ORIGINAL) return bgr.clone();
    cv::Mat gray;
    cv::cvtColor(bgr, gray, cv::COLOR_BGR2GRAY);
    cv::Mat output;
    switch (method) {
    case DT_LINEAR_CONTRAST:
        output = percentileStretch(gray, contrastClip);
        break;
    case DT_HISTOGRAM_EQUALIZATION:
        cv::equalizeHist(gray, output);
        break;
    case DT_CLAHE: {
        auto clahe = cv::createCLAHE(1.6, cv::Size(8, 8));
        clahe->apply(gray, output);
        break;
    }
    case DT_NLM_CLAHE: {
        cv::Mat denoised;
        cv::fastNlMeansDenoising(
            gray, denoised, static_cast<float>(std::clamp(denoiseStrength, 0.0, 30.0)),
            7, 21);
        auto clahe = cv::createCLAHE(1.6, cv::Size(8, 8));
        clahe->apply(denoised, output);
        break;
    }
    case DT_RETINEX:
        output = retinex(gray, retinexSigma);
        break;
    case DT_GAMMA: {
        gamma = std::clamp(gamma, 0.05, 5.0);
        cv::Mat lut(1, 256, CV_8U);
        for (int i = 0; i < 256; ++i) {
            lut.at<unsigned char>(i) = cv::saturate_cast<unsigned char>(
                255.0 * std::pow(i / 255.0, gamma));
        }
        cv::LUT(gray, lut, output);
        break;
    }
    default:
        output = gray;
        break;
    }
    cv::Mat bgrOutput;
    cv::cvtColor(output, bgrOutput, cv::COLOR_GRAY2BGR);
    return bgrOutput;
}

cv::Mat preprocess(const cv::Mat& bgr) {
    cv::Mat gray, normalized;
    cv::cvtColor(bgr, gray, cv::COLOR_BGR2GRAY);
    cv::createCLAHE(2.0, cv::Size(8, 8))->apply(gray, normalized);
    return normalized;
}

bool inside(const cv::Mat& image, cv::Point2f point, int margin) {
    return point.x >= margin && point.y >= margin &&
        point.x < image.cols - margin && point.y < image.rows - margin;
}

cv::Mat patchAt(const cv::Mat& image, cv::Point2f point, int size) {
    if (!inside(image, point, size / 2 + 2)) return {};
    cv::Mat patch;
    cv::getRectSubPix(image, cv::Size(size, size), point, patch);
    return patch;
}

double patchCorrelation(const cv::Mat& a, const cv::Mat& b) {
    if (a.empty() || b.empty() || a.size() != b.size()) return -1.0;
    cv::Mat af, bf;
    a.convertTo(af, CV_32F); b.convertTo(bf, CV_32F);
    af -= cv::mean(af)[0]; bf -= cv::mean(bf)[0];
    const double denominator = cv::norm(af) * cv::norm(bf);
    return denominator > 1.0e-9 ? af.dot(bf) / denominator : -1.0;
}

struct Match {
    cv::Point2f point{};
    double score = -1.0;
    double psr = -1.0;
    bool valid = false;
};

double parabolicOffset(double before, double center, double after) {
    const double denominator = before - 2.0 * center + after;
    return std::abs(denominator) > 1.0e-9
        ? std::clamp(0.5 * (before - after) / denominator, -0.75, 0.75) : 0.0;
}

Match matchTemplate(
    const cv::Mat& templateImage, const cv::Mat& current,
    cv::Point2f predicted, int radius) {
    Match output;
    if (templateImage.empty()) return output;
    cv::Scalar mean, deviation;
    cv::meanStdDev(templateImage, mean, deviation);
    if (deviation[0] < 2.0) return output;
    const int halfX = templateImage.cols / 2;
    const int halfY = templateImage.rows / 2;
    const int x0 = std::max(0, static_cast<int>(std::floor(predicted.x - radius - halfX)));
    const int y0 = std::max(0, static_cast<int>(std::floor(predicted.y - radius - halfY)));
    const int x1 = std::min(current.cols, static_cast<int>(std::ceil(predicted.x + radius + halfX + 1)));
    const int y1 = std::min(current.rows, static_cast<int>(std::ceil(predicted.y + radius + halfY + 1)));
    if (x1 - x0 < templateImage.cols || y1 - y0 < templateImage.rows) return output;
    const cv::Mat search = current(cv::Rect(x0, y0, x1 - x0, y1 - y0));
    cv::Mat response;
    cv::matchTemplate(search, templateImage, response, cv::TM_CCOEFF_NORMED);
    double bestScore = -1.0;
    cv::Point peak;
    cv::minMaxLoc(response, nullptr, &bestScore, nullptr, &peak);
    double dx = 0.0, dy = 0.0;
    if (peak.x > 0 && peak.x + 1 < response.cols) {
        dx = parabolicOffset(
            response.at<float>(peak.y, peak.x - 1), response.at<float>(peak.y, peak.x),
            response.at<float>(peak.y, peak.x + 1));
    }
    if (peak.y > 0 && peak.y + 1 < response.rows) {
        dy = parabolicOffset(
            response.at<float>(peak.y - 1, peak.x), response.at<float>(peak.y, peak.x),
            response.at<float>(peak.y + 1, peak.x));
    }
    cv::Mat sideMask(response.size(), CV_8U, cv::Scalar(255));
    const cv::Rect guard(
        std::max(0, peak.x - 3), std::max(0, peak.y - 3),
        std::min(response.cols - std::max(0, peak.x - 3), 7),
        std::min(response.rows - std::max(0, peak.y - 3), 7));
    sideMask(guard).setTo(0);
    cv::Scalar sideMean, sideStd;
    cv::meanStdDev(response, sideMean, sideStd, sideMask);
    output.point = cv::Point2f(
        static_cast<float>(x0 + peak.x + halfX + dx),
        static_cast<float>(y0 + peak.y + halfY + dy));
    output.score = bestScore;
    output.psr = (bestScore - sideMean[0]) / std::max(sideStd[0], 1.0e-6);
    output.valid = true;
    return output;
}

class Tracker {
public:
    Tracker(const char* path, const DtPoint* points, const DtTrackerSettings& settings) {
        previousBgr_ = readImage(path);
        previousGray_ = preprocess(previousBgr_);
        points_[0] = cv::Point2f(static_cast<float>(points[0].x), static_cast<float>(points[0].y));
        points_[1] = cv::Point2f(static_cast<float>(points[1].x), static_cast<float>(points[1].y));
        referenceGray_ = previousGray_.clone();
        referencePoints_ = points_;
        updateSettings(settings);
        rebuildTemplates();
    }

    void updateSettings(const DtTrackerSettings& value) {
        settings_ = value;
        settings_.windowSize = std::max(15, settings_.windowSize | 1);
        settings_.searchRadius = std::max(10, settings_.searchRadius);
        settings_.referenceThreshold = std::clamp(settings_.referenceThreshold, 0.1, 0.99);
        settings_.forwardBackwardThreshold = std::clamp(settings_.forwardBackwardThreshold, 0.1, 20.0);
        templateSize_ = std::min(settings_.windowSize, 41) | 1;
        referenceSize_ = std::clamp(settings_.windowSize, 31, 61) | 1;
    }

    void rebuildTemplates() {
        for (int i = 0; i < 2; ++i) {
            referenceTemplates_[i] = patchAt(previousGray_, points_[i], referenceSize_);
            keyframeTemplates_[i] = referenceTemplates_[i].clone();
        }
    }

    int step(const char* path, const DtTrackerSettings& settings, DtTrackResult& output) {
        cv::Mat bgr = readImage(path);
        cv::Mat gray = preprocess(bgr);
        return stepPrepared(bgr, gray, settings, output);
    }

    int stepPrepared(
        const cv::Mat& bgr, const cv::Mat& gray,
        const DtTrackerSettings& settings, DtTrackResult& output) {
        updateSettings(settings);
        if (bgr.empty() || gray.empty() || bgr.size() != previousBgr_.size() || gray.size() != previousGray_.size())
            throw std::runtime_error("Prepared frame size does not match the tracking sequence.");
        currentBgr_ = bgr;
        currentGray_ = gray;
        const std::vector<cv::Point2f> oldPoints{points_[0], points_[1]};
        std::vector<cv::Point2f> predicted{
            points_[0] + velocities_[0], points_[1] + velocities_[1]};
        std::vector<cv::Point2f> forward = predicted;
        std::vector<unsigned char> forwardStatus;
        std::vector<float> forwardError;
        cv::calcOpticalFlowPyrLK(
            previousGray_, currentGray_, oldPoints, forward,
            forwardStatus, forwardError, cv::Size(settings_.windowSize, settings_.windowSize),
            4, cv::TermCriteria(cv::TermCriteria::COUNT | cv::TermCriteria::EPS, 50, 0.0005),
            cv::OPTFLOW_USE_INITIAL_FLOW, 1.0e-5);
        std::vector<cv::Point2f> backward = oldPoints;
        std::vector<unsigned char> backwardStatus;
        std::vector<float> backwardError;
        cv::calcOpticalFlowPyrLK(
            currentGray_, previousGray_, forward, backward,
            backwardStatus, backwardError, cv::Size(settings_.windowSize, settings_.windowSize),
            4, cv::TermCriteria(cv::TermCriteria::COUNT | cv::TermCriteria::EPS, 50, 0.0005),
            cv::OPTFLOW_USE_INITIAL_FLOW, 1.0e-5);
        const std::vector<cv::Point2f> referencePoints{
            referencePoints_[0], referencePoints_[1]};
        std::vector<cv::Point2f> referenceForward = predicted;
        std::vector<unsigned char> referenceForwardStatus;
        std::vector<float> referenceForwardError;
        cv::calcOpticalFlowPyrLK(
            referenceGray_, currentGray_, referencePoints, referenceForward,
            referenceForwardStatus, referenceForwardError,
            cv::Size(settings_.windowSize, settings_.windowSize), 4,
            cv::TermCriteria(cv::TermCriteria::COUNT | cv::TermCriteria::EPS, 50, 0.0005),
            cv::OPTFLOW_USE_INITIAL_FLOW, 1.0e-5);
        std::vector<cv::Point2f> referenceBackward = referencePoints;
        std::vector<unsigned char> referenceBackwardStatus;
        std::vector<float> referenceBackwardError;
        cv::calcOpticalFlowPyrLK(
            currentGray_, referenceGray_, referenceForward, referenceBackward,
            referenceBackwardStatus, referenceBackwardError,
            cv::Size(settings_.windowSize, settings_.windowSize), 4,
            cv::TermCriteria(cv::TermCriteria::COUNT | cv::TermCriteria::EPS, 50, 0.0005),
            cv::OPTFLOW_USE_INITIAL_FLOW, 1.0e-5);

        struct PointOutcome {
            cv::Point2f candidate{};
            double quality = -1.0;
            int status = DT_LOST;
            bool valid = false;
            bool continuous = false;
            bool replaceKeyframe = false;
            cv::Mat replacementKeyframe;
        };
        std::array<PointOutcome, 2> outcomes{};
        cv::parallel_for_(cv::Range(0, 2), [&](const cv::Range& range) {
            for (int i = range.start; i < range.end; ++i) {
                PointOutcome outcome;
                outcome.candidate = forward[i];
                outcome.quality = patchCorrelation(
                    patchAt(previousGray_, points_[i], templateSize_),
                    patchAt(currentGray_, outcome.candidate, templateSize_));
                outcome.valid = i < static_cast<int>(forwardStatus.size()) && forwardStatus[i] &&
                    i < static_cast<int>(backwardStatus.size()) && backwardStatus[i] &&
                    cv::norm(backward[i] - points_[i]) <= settings_.forwardBackwardThreshold &&
                    outcome.quality >= 0.30 &&
                    inside(currentGray_, outcome.candidate, templateSize_ / 2 + 2);
                outcome.continuous = outcome.valid;
                outcome.status = outcome.valid ? DT_LK : DT_LOST;
                const double referenceQuality = patchCorrelation(
                    patchAt(referenceGray_, referencePoints_[i], templateSize_),
                    patchAt(currentGray_, referenceForward[i], templateSize_));
                const bool referenceValid =
                    i < static_cast<int>(referenceForwardStatus.size()) &&
                    referenceForwardStatus[i] &&
                    i < static_cast<int>(referenceBackwardStatus.size()) &&
                    referenceBackwardStatus[i] &&
                    cv::norm(referenceBackward[i] - referencePoints_[i]) <=
                        settings_.forwardBackwardThreshold &&
                    referenceQuality >= 0.30 &&
                    inside(currentGray_, referenceForward[i], templateSize_ / 2 + 2);
                if (referenceValid) {
                    if (!outcome.valid) {
                        outcome.candidate = referenceForward[i];
                        outcome.quality = referenceQuality;
                        outcome.valid = true;
                        outcome.status = DT_REFERENCE;
                    } else if (cv::norm(referenceForward[i] - outcome.candidate) <=
                               std::max(0.75, settings_.forwardBackwardThreshold)) {
                        constexpr float referenceWeight = 0.65f;
                        outcome.candidate =
                            referenceForward[i] * referenceWeight +
                            outcome.candidate * (1.0f - referenceWeight);
                        outcome.quality =
                            referenceQuality * referenceWeight +
                            outcome.quality * (1.0 - referenceWeight);
                        outcome.status = DT_REFERENCE;
                    }
                }
                if (!outcome.valid) {
                    const int localRadius = std::min(
                        settings_.searchRadius, std::max(12, settings_.windowSize / 2));
                    const Match local = matchTemplate(
                        patchAt(previousGray_, points_[i], templateSize_), currentGray_,
                        predicted[i], localRadius);
                    if (local.valid && local.score >= 0.45 && local.psr >= 4.0) {
                        outcome.candidate = local.point;
                        outcome.quality = local.score;
                        outcome.valid = true;
                        outcome.status = DT_TEMPLATE;
                    } else {
                        outcome.candidate = predicted[i];
                        outcome.quality = -1.0;
                    }
                }
                if (!outcome.valid) {
                    const Match anchored = matchTemplate(
                        referenceTemplates_[i], currentGray_, predicted[i],
                        settings_.searchRadius);
                    if (anchored.valid &&
                        anchored.score >= settings_.referenceThreshold && anchored.psr >= 4.5) {
                        outcome.candidate = anchored.point;
                        outcome.quality = anchored.score;
                        outcome.valid = true;
                        outcome.status = DT_REFERENCE;
                        outcome.replaceKeyframe = true;
                        outcome.replacementKeyframe =
                            patchAt(currentGray_, outcome.candidate, referenceSize_);
                    }
                }
                if (!outcome.valid) {
                    const Match keyframed = matchTemplate(
                        keyframeTemplates_[i], currentGray_, predicted[i],
                        settings_.searchRadius);
                    if (keyframed.valid &&
                        keyframed.score >=
                            std::max(0.40, settings_.referenceThreshold - 0.08) &&
                        keyframed.psr >= 4.2) {
                        outcome.candidate = keyframed.point;
                        outcome.quality = keyframed.score;
                        outcome.valid = true;
                        outcome.status = DT_KEYFRAME;
                    }
                }
                outcomes[i] = std::move(outcome);
            }
        }, 2.0);
        std::array<cv::Point2f, 2> nextVelocities{};
        for (int i = 0; i < 2; ++i) {
            const PointOutcome& outcome = outcomes[i];
            cv::Point2f accepted = outcome.candidate;
            if (outcome.valid && outcome.continuous) {
                constexpr float positionGain = 0.58f;
                constexpr float velocityGain = 0.06f;
                const cv::Point2f innovation = outcome.candidate - predicted[i];
                accepted = predicted[i] + positionGain * innovation;
                nextVelocities[i] = velocities_[i] + velocityGain * innovation;
            } else if (outcome.valid) {
                nextVelocities[i] = outcome.candidate - points_[i];
            } else {
                nextVelocities[i] = velocities_[i] * 0.5f;
            }
            newPoints_[i] = accepted;
            output.points[i] = {accepted.x, accepted.y};
            output.qualities[i] = outcome.quality;
            output.statuses[i] = outcome.status;
            output.valid[i] = outcome.valid ? 1 : 0;
            if (outcome.replaceKeyframe)
                keyframeTemplates_[i] = outcome.replacementKeyframe;
        }
        for (int i = 0; i < 2; ++i) {
            velocities_[i] = nextVelocities[i];
            points_[i] = newPoints_[i];
        }
        previousBgr_ = currentBgr_;
        previousGray_ = currentGray_;
        return 1;
    }

    bool reselect(int index, DtPoint point) {
        if (index < 0 || index > 1 || previousGray_.empty()) return false;
        const cv::Point2f selected(static_cast<float>(point.x), static_cast<float>(point.y));
        cv::Mat patch = patchAt(previousGray_, selected, referenceSize_);
        if (patch.empty()) return false;
        points_[index] = selected;
        velocities_[0] = velocities_[1] = {};
        referenceGray_ = previousGray_.clone();
        referencePoints_ = points_;
        rebuildTemplates();
        return true;
    }

    std::pair<cv::Mat, cv::Mat> snapshot() const {
        return {previousBgr_, previousGray_};
    }

private:
    DtTrackerSettings settings_{};
    int templateSize_ = 41;
    int referenceSize_ = 41;
    cv::Mat previousBgr_, previousGray_, currentBgr_, currentGray_, referenceGray_;
    std::array<cv::Point2f, 2> points_{};
    std::array<cv::Point2f, 2> referencePoints_{};
    std::array<cv::Point2f, 2> newPoints_{};
    std::array<cv::Point2f, 2> velocities_{};
    std::array<cv::Mat, 2> referenceTemplates_{};
    std::array<cv::Mat, 2> keyframeTemplates_{};
};

struct ImageHandle { cv::Mat image; };
struct PreparedFrame { cv::Mat bgr; cv::Mat gray; };
struct TrackerSnapshot { cv::Mat bgr; cv::Mat gray; };

template <typename Function>
int protect(Function&& function) {
    try {
        g_lastError.clear();
        function();
        return 1;
    } catch (const cv::Exception& error) {
        setError(std::string("OpenCV: ") + error.what());
    } catch (const std::exception& error) {
        setError(error.what());
    } catch (...) {
        setError("Unknown native error.");
    }
    return 0;
}

} // namespace

int dt_abi_version() { return 1; }

const char* dt_last_error() { return g_lastError.c_str(); }

void* dt_image_open(const char* path, int* width, int* height) {
    try {
        g_lastError.clear();
        auto image = std::make_unique<ImageHandle>();
        image->image = readImage(path);
        if (width) *width = image->image.cols;
        if (height) *height = image->image.rows;
        return image.release();
    } catch (const std::exception& error) {
        setError(error.what());
        return nullptr;
    }
}

void dt_image_close(void* image) { delete static_cast<ImageHandle*>(image); }

int dt_image_render(
    void* image, int method, double contrastClip, double gamma,
    double denoiseStrength, double retinexSigma,
    std::uint8_t* destination, int destinationStride) {
    return protect([&] {
        if (!image || !destination) throw std::runtime_error("Invalid image render argument.");
        const cv::Mat rendered = enhance(
            static_cast<ImageHandle*>(image)->image, method, contrastClip,
            gamma, denoiseStrength, retinexSigma);
        cv::Mat bgra;
        cv::cvtColor(rendered, bgra, cv::COLOR_BGR2BGRA);
        if (destinationStride < bgra.cols * 4) throw std::runtime_error("Destination stride is too small.");
        for (int y = 0; y < bgra.rows; ++y) {
            std::memcpy(destination + static_cast<std::size_t>(y) * destinationStride,
                        bgra.ptr(y), static_cast<std::size_t>(bgra.cols) * 4);
        }
    });
}

int dt_image_render_tracking_input(
    void* image, std::uint8_t* destination, int destinationStride) {
    return protect([&] {
        if (!image || !destination) throw std::runtime_error("Invalid tracking input render argument.");
        const cv::Mat trackingInput = preprocess(static_cast<ImageHandle*>(image)->image);
        if (destinationStride < trackingInput.cols) throw std::runtime_error("Destination stride is too small.");
        for (int y = 0; y < trackingInput.rows; ++y) {
            std::memcpy(destination + static_cast<std::size_t>(y) * destinationStride,
                        trackingInput.ptr(y), static_cast<std::size_t>(trackingInput.cols));
        }
    });
}

void* dt_tracker_create(
    const char* path, const DtPoint* points, const DtTrackerSettings* settings) {
    try {
        g_lastError.clear();
        if (!points || !settings) throw std::runtime_error("Invalid tracker arguments.");
        return new Tracker(path, points, *settings);
    } catch (const cv::Exception& error) {
        setError(std::string("OpenCV: ") + error.what());
    } catch (const std::exception& error) {
        setError(error.what());
    }
    return nullptr;
}

void dt_tracker_destroy(void* tracker) { delete static_cast<Tracker*>(tracker); }

int dt_tracker_step(
    void* tracker, const char* path,
    const DtTrackerSettings* settings, DtTrackResult* result) {
    return protect([&] {
        if (!tracker || !settings || !result) throw std::runtime_error("Invalid tracker step arguments.");
        static_cast<Tracker*>(tracker)->step(path, *settings, *result);
    });
}

void* dt_frame_prepare(const char* path, int* width, int* height) {
    try {
        g_lastError.clear();
        auto prepared = std::make_unique<PreparedFrame>();
        prepared->bgr = readImage(path);
        prepared->gray = preprocess(prepared->bgr);
        if (width) *width = prepared->bgr.cols;
        if (height) *height = prepared->bgr.rows;
        return prepared.release();
    } catch (const cv::Exception& error) {
        setError(std::string("OpenCV: ") + error.what());
    } catch (const std::exception& error) {
        setError(error.what());
    }
    return nullptr;
}

void dt_frame_prepare_destroy(void* preparedFrame) {
    delete static_cast<PreparedFrame*>(preparedFrame);
}

int dt_tracker_step_prepared(
    void* tracker, void* preparedFrame,
    const DtTrackerSettings* settings, DtTrackResult* result) {
    return protect([&] {
        if (!tracker || !preparedFrame || !settings || !result)
            throw std::runtime_error("Invalid prepared tracker step arguments.");
        const auto* prepared = static_cast<PreparedFrame*>(preparedFrame);
        static_cast<Tracker*>(tracker)->stepPrepared(
            prepared->bgr, prepared->gray, *settings, *result);
    });
}

int dt_tracker_reselect(void* tracker, int index, DtPoint point) {
    return protect([&] {
        if (!tracker || !static_cast<Tracker*>(tracker)->reselect(index, point)) {
            throw std::runtime_error("The selected point is too close to the image border.");
        }
    });
}

void* dt_tracker_snapshot_create(void* tracker, int* width, int* height) {
    try {
        g_lastError.clear();
        if (!tracker) throw std::runtime_error("Invalid tracker snapshot argument.");
        auto [bgr, gray] = static_cast<Tracker*>(tracker)->snapshot();
        if (bgr.empty() || gray.empty()) throw std::runtime_error("The tracker has no current frame.");
        auto snapshot = std::make_unique<TrackerSnapshot>();
        // cv::Mat copies are immutable, reference-counted snapshots. The next tracker
        // step can replace its matrices without invalidating these frame buffers.
        snapshot->bgr = bgr;
        snapshot->gray = gray;
        if (width) *width = bgr.cols;
        if (height) *height = bgr.rows;
        return snapshot.release();
    } catch (const cv::Exception& error) {
        setError(std::string("OpenCV: ") + error.what());
    } catch (const std::exception& error) {
        setError(error.what());
    }
    return nullptr;
}

void dt_tracker_snapshot_destroy(void* snapshot) {
    delete static_cast<TrackerSnapshot*>(snapshot);
}

int dt_tracker_snapshot_render(
    void* snapshot, int method, double contrastClip, double gamma,
    double denoiseStrength, double retinexSigma,
    std::uint8_t* preview, int previewStride,
    std::uint8_t* tracking, int trackingStride) {
    return protect([&] {
        if (!snapshot || !preview || !tracking)
            throw std::runtime_error("Invalid tracker snapshot render argument.");
        const auto* frame = static_cast<TrackerSnapshot*>(snapshot);
        const cv::Mat rendered = enhance(
            frame->bgr, method, contrastClip, gamma, denoiseStrength, retinexSigma);
        cv::Mat bgra;
        cv::cvtColor(rendered, bgra, cv::COLOR_BGR2BGRA);
        if (previewStride < bgra.cols * 4 || trackingStride < frame->gray.cols)
            throw std::runtime_error("Snapshot destination stride is too small.");
        for (int y = 0; y < bgra.rows; ++y) {
            std::memcpy(preview + static_cast<std::size_t>(y) * previewStride,
                        bgra.ptr(y), static_cast<std::size_t>(bgra.cols) * 4);
            std::memcpy(tracking + static_cast<std::size_t>(y) * trackingStride,
                        frame->gray.ptr(y), static_cast<std::size_t>(frame->gray.cols));
        }
    });
}

int dt_tracker_snapshot_render_patches(
    void* snapshot, const DtPoint* points, int patchSize,
    std::uint8_t* point1, int point1Stride,
    std::uint8_t* point2, int point2Stride) {
    return protect([&] {
        if (!snapshot || !points || !point1 || !point2)
            throw std::runtime_error("Invalid recognition patch render argument.");
        if (patchSize < 1 || point1Stride < patchSize || point2Stride < patchSize)
            throw std::runtime_error("Recognition patch dimensions are invalid.");
        const auto* frame = static_cast<TrackerSnapshot*>(snapshot);
        std::array<std::uint8_t*, 2> destinations{point1, point2};
        std::array<int, 2> strides{point1Stride, point2Stride};
        for (int index = 0; index < 2; ++index) {
            const cv::Point2f center(
                static_cast<float>(points[index].x),
                static_cast<float>(points[index].y));
            if (!inside(frame->gray, center, 1))
                throw std::runtime_error("Recognition point is outside the current frame.");
            cv::Mat patch;
            cv::getRectSubPix(
                frame->gray, cv::Size(patchSize, patchSize), center, patch, CV_8U);
            for (int y = 0; y < patch.rows; ++y) {
                std::memcpy(
                    destinations[index] + static_cast<std::size_t>(y) * strides[index],
                    patch.ptr(y), static_cast<std::size_t>(patch.cols));
            }
        }
    });
}

int dt_tracker_self_test() {
    return protect([] {
        cv::Mat source(160, 220, CV_8U);
        cv::RNG random(20260716);
        random.fill(source, cv::RNG::UNIFORM, 10, 245);
        const cv::Point2f point(110.0f, 80.0f);
        const cv::Point2f shift(2.35f, -1.65f);
        cv::Mat transform = (cv::Mat_<double>(2, 3) << 1, 0, shift.x, 0, 1, shift.y);
        cv::Mat moved;
        cv::warpAffine(source, moved, transform, source.size(), cv::INTER_CUBIC, cv::BORDER_REFLECT);
        moved.convertTo(moved, CV_8U, 0.62, 48.0);
        const Match match = matchTemplate(patchAt(source, point, 41), moved, point, 12);
        const double error = cv::norm(match.point - (point + shift));
        if (!match.valid || error > 0.8 || match.score < 0.75 || match.psr < 8.0) {
            std::ostringstream message;
            message << "Native OpenCV subpixel self-test failed: valid=" << match.valid
                    << ", error=" << error << ", score=" << match.score
                    << ", psr=" << match.psr << '.';
            throw std::runtime_error(message.str());
        }
    });
}
