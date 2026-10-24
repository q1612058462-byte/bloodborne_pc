// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

#include <cstdint>

// Direct Bloodborne 1.09 camera input. The caller installs the hook before guest code starts.
// Other game builds and non-Windows hosts leave the existing pad camera untouched.
extern "C" int bbgpu_camera_install(void* image, uint64_t image_size);
extern "C" int bbgpu_camera_direct(void);
extern "C" int bbgpu_camera_locked(void);
extern "C" void bbgpu_camera_motion(int32_t dx, int32_t dy);
extern "C" void bbgpu_camera_clear_motion(void);
