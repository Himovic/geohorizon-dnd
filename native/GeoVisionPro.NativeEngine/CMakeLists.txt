cmake_minimum_required(VERSION 3.20)
project(GeoVisionProNative VERSION 0.8.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

option(GV_WITH_VTK "Build the VTK rendering bridge (requires VTK 9.x)" ON)

# ---------------------------------------------------------------- compute engine
add_library(gvnative SHARED src/gv_engine.cpp)
target_include_directories(gvnative PUBLIC include)
target_compile_definitions(gvnative PRIVATE GV_BUILDING_DLL)
find_package(OpenMP)
if(OpenMP_CXX_FOUND)
  target_link_libraries(gvnative PRIVATE OpenMP::OpenMP_CXX)
  target_compile_definitions(gvnative PRIVATE GV_HAVE_OPENMP=1)
endif()
if(MSVC)
  target_compile_options(gvnative PRIVATE /O2 /fp:fast /arch:AVX2 /W4)
else()
  target_compile_options(gvnative PRIVATE -O3 -Wall -Wextra)
endif()

# ---------------------------------------------------------------- VTK bridge
if(GV_WITH_VTK)
  # Some Linux VTK packages export a VTK::mpi module linked to MPI::MPI_C even when MPI is not installed.
  # The bridge does not use MPI; provide an empty imported target so the VTK package config loads.
  find_package(MPI QUIET COMPONENTS C)
  if(NOT TARGET MPI::MPI_C)
    add_library(MPI::MPI_C INTERFACE IMPORTED)
  endif()
  find_package(VTK 9 QUIET COMPONENTS
    CommonCore CommonDataModel CommonColor RenderingCore RenderingOpenGL2 RenderingVolume
    RenderingVolumeOpenGL2 InteractionStyle IOImage FiltersCore)
  if(VTK_FOUND)
    message(STATUS "VTK ${VTK_VERSION} found: building gvvtk bridge")
    add_library(gvvtk SHARED src/gv_vtk_bridge.cpp)
    target_include_directories(gvvtk PUBLIC include)
    target_compile_definitions(gvvtk PRIVATE GV_BUILDING_DLL)
    target_link_libraries(gvvtk PRIVATE ${VTK_LIBRARIES})
    vtk_module_autoinit(TARGETS gvvtk MODULES ${VTK_LIBRARIES})
  else()
    message(WARNING "VTK 9 not found: the gvvtk 3D bridge is skipped (the application falls back to HelixToolkit rendering).")
  endif()
endif()

# ---------------------------------------------------------------- self test
add_executable(gvnative_selftest src/selftest.cpp)
target_link_libraries(gvnative_selftest PRIVATE gvnative)
enable_testing()
add_test(NAME gvnative_selftest COMMAND gvnative_selftest)

install(TARGETS gvnative RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
if(TARGET gvvtk)
  install(TARGETS gvvtk RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
endif()
