cmake_minimum_required(VERSION 3.24)
project(SonyCppWrapper LANGUAGES C CXX)
if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "Windows x64 required")
endif()
set(SONY_SIMPLECLI "$ENV{SONY_SIMPLECLI}" CACHE PATH "Path to an official extracted SimpleCli; external dependency, never committed here")
if(NOT SONY_SIMPLECLI)
  message(FATAL_ERROR "Supply -DSONY_SIMPLECLI=<official SimpleCli directory> or set SONY_SIMPLECLI. No developer-machine default is used.")
endif()
get_filename_component(SONY_SIMPLECLI "${SONY_SIMPLECLI}" ABSOLUTE BASE_DIR "${CMAKE_SOURCE_DIR}")
set(SDK_HEADERS "${SONY_SIMPLECLI}/app/CRSDK")
set(SDK_RUNTIME "${SONY_SIMPLECLI}/external/crsdk")
if(NOT EXISTS "${SDK_RUNTIME}/Cr_Core.lib" OR NOT EXISTS "${SDK_HEADERS}/CameraRemote_SDK.h")
  message(FATAL_ERROR "Set SONY_SIMPLECLI to extracted official SimpleCli")
endif()
add_library(sony_wrapper SHARED src/sony_wrapper.cpp)
target_compile_features(sony_wrapper PRIVATE cxx_std_17)
target_compile_definitions(sony_wrapper PRIVATE SW_BUILD UNICODE _UNICODE)
target_include_directories(sony_wrapper PUBLIC include)
target_include_directories(sony_wrapper SYSTEM PRIVATE "${SDK_HEADERS}")
target_link_libraries(sony_wrapper PRIVATE "${SDK_RUNTIME}/Cr_Core.lib")
target_compile_options(sony_wrapper PRIVATE /W4 /EHsc /utf-8)
add_executable(wrapper_test test/main.cpp)
target_link_libraries(wrapper_test PRIVATE sony_wrapper)
target_compile_options(wrapper_test PRIVATE /W4 /EHsc /utf-8)
# Runtime-only deployment in ignored build tree; never copy SDK headers/lib into source.

# Compile the public header as plain C too; no Sony headers visible to either client.
add_library(c_header_check OBJECT test/header_check.c)
target_include_directories(c_header_check PRIVATE include)


target_compile_features(c_header_check PRIVATE c_std_11)


# Step two adds an independent client; original wrapper_test target/source remain intact.
add_executable(wrapper_parameters_test test/parameters_main.cpp)
target_compile_features(wrapper_parameters_test PRIVATE cxx_std_17)
target_link_libraries(wrapper_parameters_test PRIVATE sony_wrapper)
target_compile_options(wrapper_parameters_test PRIVATE /W4 /EHsc /utf-8)


# Step three: original clients remain intact, no Sony dependency exposed to this client.
add_executable(wrapper_capture_test test/capture_main.cpp)
target_compile_features(wrapper_capture_test PRIVATE cxx_std_17)
target_link_libraries(wrapper_capture_test PRIVATE sony_wrapper)
target_compile_options(wrapper_capture_test PRIVATE /W4 /EHsc /utf-8)

add_library(capture_c_header_check OBJECT test/capture_header_check.c)
target_include_directories(capture_c_header_check PRIVATE include)
target_compile_features(capture_c_header_check PRIVATE c_std_11)

# Deploy SDK once: parallel executable post-build copies raced in the original setup.
add_custom_target(sony_sdk_runtime
  COMMAND "${CMAKE_COMMAND}" -E copy_directory "${SDK_RUNTIME}" "$<TARGET_FILE_DIR:sony_wrapper>")
add_dependencies(wrapper_test sony_sdk_runtime)
add_dependencies(wrapper_parameters_test sony_sdk_runtime)
add_dependencies(wrapper_capture_test sony_sdk_runtime)

# Backend test double: isolated folder, production wrapper binary copied unchanged.
add_library(fake_sdk SHARED test/fake_sdk.cpp)
target_include_directories(fake_sdk SYSTEM PRIVATE "${SDK_HEADERS}")
target_compile_features(fake_sdk PRIVATE cxx_std_17)
target_compile_definitions(fake_sdk PRIVATE CR_SDK_EXPORTS UNICODE _UNICODE)
target_compile_options(fake_sdk PRIVATE /W3 /EHsc /utf-8)
set_target_properties(fake_sdk PROPERTIES OUTPUT_NAME Cr_Core RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/fake" ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/fake-lib")

add_executable(wrapper_closeout_test test/closeout_main.cpp)
target_link_libraries(wrapper_closeout_test PRIVATE sony_wrapper)
target_compile_features(wrapper_closeout_test PRIVATE cxx_std_17)
target_compile_options(wrapper_closeout_test PRIVATE /W4 /EHsc /utf-8)
add_dependencies(wrapper_closeout_test sony_sdk_runtime fake_sdk)
add_custom_command(TARGET wrapper_closeout_test POST_BUILD
 COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_FILE:sony_wrapper>" "${CMAKE_BINARY_DIR}/fake/sony_wrapper.dll"
 COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_FILE:wrapper_closeout_test>" "${CMAKE_BINARY_DIR}/fake/wrapper_closeout_test.exe")
add_executable(mailbox_test test/mailbox_main.cpp)
target_compile_features(mailbox_test PRIVATE cxx_std_17)
target_compile_options(mailbox_test PRIVATE /W4 /EHsc /utf-8)

# Extended public ABI tests have no SDK headers. Runtime fake remains isolated.
add_executable(wrapper_features_test test/features_main.cpp)
target_link_libraries(wrapper_features_test PRIVATE sony_wrapper)
target_compile_features(wrapper_features_test PRIVATE cxx_std_17)
target_compile_options(wrapper_features_test PRIVATE /W4 /EHsc /utf-8)
add_custom_target(feature_fake_runtime ALL
 COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_FILE:sony_wrapper>" "${CMAKE_BINARY_DIR}/fake/sony_wrapper.dll"
 COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_FILE:wrapper_features_test>" "${CMAKE_BINARY_DIR}/fake/wrapper_features_test.exe"
 COMMAND "${CMAKE_COMMAND}" -E copy "$<TARGET_FILE:wrapper_closeout_test>" "${CMAKE_BINARY_DIR}/fake/wrapper_closeout_test.exe"
 DEPENDS sony_wrapper fake_sdk wrapper_features_test wrapper_closeout_test)
