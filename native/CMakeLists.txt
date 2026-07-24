find_package(OpenCV 4 REQUIRED COMPONENTS core imgproc imgcodecs video photo)

add_library(distance_tracker_native SHARED
    src/distance_tracker_native.cpp
    include/distance_tracker_native.h)

target_compile_features(distance_tracker_native PRIVATE cxx_std_20)
target_compile_definitions(distance_tracker_native PRIVATE
    DISTANCE_TRACKER_NATIVE_EXPORTS
    NOMINMAX)
target_include_directories(distance_tracker_native
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${OpenCV_INCLUDE_DIRS})
target_link_libraries(distance_tracker_native PRIVATE ${OpenCV_LIBS})
set_target_properties(distance_tracker_native PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    OUTPUT_NAME distance_tracker_native)

if(MSVC)
    target_compile_options(distance_tracker_native PRIVATE /utf-8 /W4 /permissive-)
else()
    target_compile_options(distance_tracker_native PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(APPLE)
    set_target_properties(distance_tracker_native PROPERTIES
        BUILD_RPATH "${OpenCV_DIR}/../.."
        INSTALL_RPATH "@loader_path")
endif()

install(TARGETS distance_tracker_native
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION lib)

if(DISTANCE_LAB_BUILD_NATIVE_TESTS)
    add_executable(distance_tracker_native_test tests/native_self_test.cpp)
    target_compile_features(distance_tracker_native_test PRIVATE cxx_std_20)
    target_link_libraries(distance_tracker_native_test PRIVATE distance_tracker_native)
    if(MSVC)
        target_compile_options(distance_tracker_native_test PRIVATE /utf-8 /W4 /permissive-)
    else()
        target_compile_options(distance_tracker_native_test PRIVATE -Wall -Wextra -Wpedantic)
    endif()
    add_test(NAME distance_tracker_native_self_test COMMAND distance_tracker_native_test)
endif()
