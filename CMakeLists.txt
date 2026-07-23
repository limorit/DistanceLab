cmake_minimum_required(VERSION 3.24)
project(OpenCVDistanceLab LANGUAGES CXX)

include(CTest)

option(DISTANCE_LAB_BUILD_NATIVE_TESTS "Build native self-test executable" ON)
add_subdirectory(native)
