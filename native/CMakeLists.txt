# VoiceChanger native code: the VST3 host bridge (vcplugin) and a small VST3 effect used by the tests.
#
#   cmake -S native -B native/build -DCMAKE_BUILD_TYPE=Release
#   cmake --build native/build --config Release
#
# The VST3 SDK (MIT licence since 3.8) is downloaded at the pinned tag below, or taken from
# -DVC_VST3SDK_DIR=<path to a checkout with the base, cmake, pluginterfaces and public.sdk submodules>.
# Outputs go to native/build/out: the bridge library, and the test plug-in bundle under VST3/.

cmake_minimum_required(VERSION 3.25)
project(VoiceChangerNative VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(VC_VST3SDK_TAG "v3.8.1_build_84" CACHE STRING "VST3 SDK tag to download")
set(VC_VST3SDK_DIR "" CACHE PATH "Use this VST3 SDK checkout instead of downloading one")

if(VC_VST3SDK_DIR)
    set(vst3sdk_SOURCE_DIR "${VC_VST3SDK_DIR}")
else()
    include(FetchContent)
    FetchContent_Declare(vst3sdk
        GIT_REPOSITORY https://github.com/steinbergmedia/vst3sdk.git
        GIT_TAG ${VC_VST3SDK_TAG}
        GIT_SHALLOW TRUE
        GIT_SUBMODULES base cmake pluginterfaces public.sdk
        # Only download: the SDK's own CMakeLists builds samples we don't need.
        SOURCE_SUBDIR do-not-add
    )
    FetchContent_MakeAvailable(vst3sdk)
endif()

# The bridge lands in out/, the plug-in bundle under out/VST3/ (the SDK's tools stay in bin/).
set(VC_OUTPUT_DIR "${CMAKE_BINARY_DIR}/out")

# The SDK's libraries, set up with its own CMake modules (no samples, no VSTGUI, no validator,
# no links into the system's plug-in folder).
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_IOS_TARGETS OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_MODULE_INFO ON CACHE BOOL "" FORCE)
set(SMTG_CREATE_BUNDLE_FOR_WINDOWS ON CACHE BOOL "" FORCE)
set(SMTG_CUSTOM_BINARY_LOCATION "${VC_OUTPUT_DIR}")
list(APPEND CMAKE_MODULE_PATH "${vst3sdk_SOURCE_DIR}/cmake/modules")
include(SMTG_VST3_SDK)
smtg_setup_platform_toolset()
smtg_setup_symbol_visibility()

set(ROOT "${vst3sdk_SOURCE_DIR}")
set(SDK_ROOT "${vst3sdk_SOURCE_DIR}")
set(public_sdk_SOURCE_DIR "${SDK_ROOT}/public.sdk")
set(pluginterfaces_SOURCE_DIR "${SDK_ROOT}/pluginterfaces")
include_directories(${SDK_ROOT})

smtg_create_lib_base_target()
smtg_create_pluginterfaces_target()
smtg_create_public_sdk_common_target()
smtg_create_public_sdk_target()
smtg_create_public_sdk_hosting_target()

# The SDK's tool that writes moduleinfo.json into plug-in bundles.
add_subdirectory("${SDK_ROOT}/public.sdk/samples/vst-utilities/moduleinfotool" moduleinfotool)

add_subdirectory(bridge)
add_subdirectory(testplugin)
