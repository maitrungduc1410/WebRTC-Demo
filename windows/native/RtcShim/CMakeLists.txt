cmake_minimum_required(VERSION 3.21)

# rtc_shim: a flat C API over the prebuilt webrtc-sdk/libwebrtc DLL, consumed by the C# app.
# Windows is the product target; the Linux build exists to run the loopback test on CI/Linux.
project(RtcShim LANGUAGES CXX)

# libwebrtc.dll links the release C runtime statically (/MT), and its API passes heap-owning
# portable::vector / portable::string both ways (codecs(), transceivers(), senders(), Members(),
# thumbnail() out; SetCodecPreferences(), SetSharedKey() in by value). The shim must therefore use
# the same release runtime in every configuration: a /MTd shim would free libwebrtc's blocks on
# the debug heap and vice versa. Debug still means /Od /Zi, just without the debug CRT or _DEBUG.
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/libwebrtc.lock.json" RTC_SHIM_LOCK)
string(JSON LIBWEBRTC_TAG GET "${RTC_SHIM_LOCK}" tag)

if(WIN32)
  if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|arm64|aarch64" OR CMAKE_GENERATOR_PLATFORM MATCHES "ARM64")
    set(_rtc_arch arm64)
  else()
    set(_rtc_arch x64)
  endif()
  set(_rtc_default_dir "${CMAKE_CURRENT_SOURCE_DIR}/third_party/libwebrtc/${_rtc_arch}/libwebrtc-${_rtc_arch}-release")
else()
  set(_rtc_default_dir "${CMAKE_CURRENT_SOURCE_DIR}/third_party/libwebrtc/linux-x64/linux-x64-release")
endif()
set(LIBWEBRTC_DIR "${_rtc_default_dir}" CACHE PATH "Extracted libwebrtc release (contains include/ and lib/)")

if(NOT EXISTS "${LIBWEBRTC_DIR}/include/libwebrtc.h")
  message(FATAL_ERROR "libwebrtc not found in ${LIBWEBRTC_DIR}. Run scripts/fetch-libwebrtc.ps1 (or .sh) first, or set -DLIBWEBRTC_DIR=...")
endif()

add_library(libwebrtc SHARED IMPORTED)
if(WIN32)
  set_target_properties(libwebrtc PROPERTIES
    IMPORTED_LOCATION "${LIBWEBRTC_DIR}/lib/libwebrtc.dll"
    IMPORTED_IMPLIB "${LIBWEBRTC_DIR}/lib/libwebrtc.dll.lib")
else()
  set_target_properties(libwebrtc PROPERTIES
    IMPORTED_LOCATION "${LIBWEBRTC_DIR}/lib/libwebrtc.so"
    IMPORTED_NO_SONAME ON)
endif()
# The release is built with desktop capture, which adds methods to the factory's vtable: without
# RTC_DESKTOP_DEVICE every later virtual call would land on the wrong method.
target_include_directories(libwebrtc INTERFACE "${LIBWEBRTC_DIR}/include")
target_compile_definitions(libwebrtc INTERFACE RTC_DESKTOP_DEVICE)
if(WIN32)
  target_compile_definitions(libwebrtc INTERFACE LIB_WEBRTC_API_DLL WIN32)
endif()

set(_rtc_sources
  src/shim_core.cpp
  src/shim_media.cpp
  src/shim_peer.cpp)
if(WIN32)
  list(APPEND _rtc_sources src/shim_audio_win.cpp src/shim_camera_win.cpp src/shim_file_source_win.cpp)
else()
  list(APPEND _rtc_sources src/shim_audio_stub.cpp src/shim_camera_stub.cpp src/shim_file_source_stub.cpp)
endif()

add_library(rtc_shim SHARED ${_rtc_sources})
target_include_directories(rtc_shim PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(rtc_shim PRIVATE
  RTC_SHIM_BUILD
  RTC_SHIM_LIBWEBRTC_VERSION="${LIBWEBRTC_TAG}")
target_link_libraries(rtc_shim PRIVATE libwebrtc)

if(MSVC)
  target_compile_definitions(rtc_shim PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE _WIN32_WINNT=0x0A00)
  target_compile_options(rtc_shim PRIVATE /W4 /permissive- /utf-8 /EHsc /wd4100)
  target_link_libraries(rtc_shim PRIVATE mf mfplat mfreadwrite mfuuid ole32 propsys wmcodecdspuuid)
  # Strip debug info into a PDB but keep the DLL small.
  target_link_options(rtc_shim PRIVATE /DEBUG /OPT:REF /OPT:ICF)
else()
  target_compile_options(rtc_shim PRIVATE -Wall -Wextra -Wno-unused-parameter)
  set_target_properties(rtc_shim PROPERTIES BUILD_RPATH "$ORIGIN;${LIBWEBRTC_DIR}/lib")
endif()

# The C# app loads rtc_shim and libwebrtc from its own folder.
add_custom_command(TARGET rtc_shim POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:libwebrtc>" "$<TARGET_FILE_DIR:rtc_shim>")

install(TARGETS rtc_shim RUNTIME DESTINATION . LIBRARY DESTINATION .)
install(FILES "$<TARGET_FILE:libwebrtc>" DESTINATION .)
install(FILES "${LIBWEBRTC_DIR}/LICENSE" DESTINATION . RENAME libwebrtc-LICENSE.txt)

option(RTC_SHIM_BUILD_TESTS "Build the in-process loopback test" OFF)
if(RTC_SHIM_BUILD_TESTS)
  add_executable(rtc_shim_loopback tests/loopback.cpp)
  target_link_libraries(rtc_shim_loopback PRIVATE rtc_shim)
  if(NOT WIN32)
    target_link_libraries(rtc_shim_loopback PRIVATE pthread)
    set_target_properties(rtc_shim_loopback PROPERTIES BUILD_RPATH "$ORIGIN;${LIBWEBRTC_DIR}/lib")
  endif()
endif()
