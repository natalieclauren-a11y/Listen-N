cmake_minimum_required(VERSION 3.20)
project(ListenNCore VERSION 0.1.0 LANGUAGES CXX)

option(LISTENN_BUILD_TESTS "Build the dependency-free test executable" ON)

add_library(listen_n_core
    src/analyzer.cpp
    src/c_api.cpp
    src/protocol_decoder.cpp)
add_library(ListenN::Core ALIAS listen_n_core)
target_compile_features(listen_n_core PUBLIC cxx_std_20)
target_include_directories(listen_n_core PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    $<INSTALL_INTERFACE:include>)
set_target_properties(listen_n_core PROPERTIES
    CXX_EXTENSIONS OFF
    POSITION_INDEPENDENT_CODE ON)

if(MSVC)
  target_compile_options(listen_n_core PRIVATE /W4 /permissive-)
else()
  target_compile_options(listen_n_core PRIVATE -Wall -Wextra -Wpedantic -Wconversion)
endif()

add_executable(listen-n-cli src/main.cpp)
target_link_libraries(listen-n-cli PRIVATE ListenN::Core)

if(LISTENN_BUILD_TESTS)
  enable_testing()
  add_executable(listen_n_core_tests tests/core_tests.cpp)
  target_link_libraries(listen_n_core_tests PRIVATE ListenN::Core)
  add_test(NAME listen_n_core_tests COMMAND listen_n_core_tests)
endif()
