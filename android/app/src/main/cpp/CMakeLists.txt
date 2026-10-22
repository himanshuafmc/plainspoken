# The offline fallback's speech engine: whisper.cpp (MIT), with a small JNI bridge (whisper_jni.cpp).
cmake_minimum_required(VERSION 3.22.1)
project(plainspoken_whisper C CXX)

include(FetchContent)
# whisper.cpp v1.8.7, pinned by commit.
FetchContent_Declare(
    whisper
    GIT_REPOSITORY https://github.com/ggml-org/whisper.cpp.git
    GIT_TAG 48f628a84833905ee4a0658ee6d4a5c915ce1997
)

set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON) # the static builds below go into a shared library

if (ANDROID_ABI STREQUAL "arm64-v8a")
    # Phones' ARM cores differ a lot (dot product, fp16, int8 matrix multiply). ggml builds a CPU backend for each
    # level and loads the best one this phone supports at run time, so older phones work and newer ones are fast.
    set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
    set(GGML_BACKEND_DL ON CACHE BOOL "" FORCE)
    set(GGML_CPU_ALL_VARIANTS ON CACHE BOOL "" FORCE)
    # Put the backends where the app's other native libraries go, so they are packaged with them.
    set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_LIBRARY_OUTPUT_DIRECTORY})
else ()
    # 32-bit ARM and x86_64 (emulators): one baseline build, linked in.
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
endif ()

FetchContent_MakeAvailable(whisper)

add_library(plainspoken_whisper SHARED whisper_jni.cpp)
target_compile_options(plainspoken_whisper PRIVATE -O3 -Wall)
target_link_libraries(plainspoken_whisper PRIVATE whisper ggml)
