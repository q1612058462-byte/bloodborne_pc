// SPDX-License-Identifier: GPL-2.0-or-later
// Synthetic 1.09 image: signature rejection, patch installation, and direct angle updates.
#include "../gpu/shim/bloodborne_cam.h"
#include <cassert>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

#ifdef _WIN32
#include <windows.h>
extern "C" void bbcam_apply(uintptr_t camera);

int main() {
    constexpr size_t image_size = 0x553E8D0 + 0x1000;
    auto* image = static_cast<unsigned char*>(VirtualAlloc(nullptr, image_size,
        MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    assert(image);
    constexpr unsigned char main_bytes[18] = {
        0xC4,0xC1,0x7A,0x10,0x85,0x40,0x01,0x00,0x00,
        0xC4,0xC1,0x7A,0x10,0x8D,0x50,0x01,0x00,0x00};
    constexpr size_t nop_offsets[] = {0x143C6E8,0x143C984,0x143DDE6,0x143C870};
    constexpr unsigned char nop_bytes[4][9] = {
        {0xC4,0xC1,0x7A,0x11,0x85,0x44,0x01,0x00,0x00},
        {0xC4,0xC1,0x7A,0x11,0x8D,0x30,0x01,0x00,0x00},
        {0xC4,0xC1,0x7A,0x11,0x85,0x94,0x02,0x00,0x00},
        {0xC4,0xC1,0x7A,0x11,0xAD,0x44,0x01,0x00,0x00}};
    std::memcpy(image + 0x143CEAA, main_bytes, 18);
    for (size_t i=0; i<4; ++i) std::memcpy(image + nop_offsets[i], nop_bytes[i], 9);
    assert(_putenv_s("BB_MOUSE_CAMERA", "direct") == 0);
    image[nop_offsets[3]] ^= 1;
    assert(!bbgpu_camera_install(image, image_size));
    assert(!bbgpu_camera_direct());
    assert(std::memcmp(image + 0x143CEAA, main_bytes, 18) == 0);
    image[nop_offsets[3]] ^= 1;
    assert(bbgpu_camera_install(image, image_size));
    assert(bbgpu_camera_direct());
    assert(image[0x143CEAA] == 0xFF && image[0x143CEAB] == 0x25);
    for (size_t offset : nop_offsets)
        for (int i=0; i<9; ++i) assert(image[offset+i] == 0x90);

    alignas(16) unsigned char camera[0x300]{};
    auto put = [&](size_t offset, float value) { std::memcpy(camera+offset, &value, 4); };
    auto get = [&](size_t offset) { float value; std::memcpy(&value, camera+offset, 4); return value; };
    put(0x1F0, -1.5f); put(0x1EC, 1.5f);
    bbgpu_camera_motion(900, 450);
    bbcam_apply(reinterpret_cast<uintptr_t>(camera));
    assert(std::fabs(get(0x144) - 1.0f) < 0.001f);
    assert(std::fabs(get(0x140) - 0.5f) < 0.001f);
    assert(get(0x150) == get(0x140) && get(0x270) == get(0x144));
    put(0x154, 1.0f); // lock-on: game owns the camera
    bbgpu_camera_motion(900, 450);
    bbcam_apply(reinterpret_cast<uintptr_t>(camera));
    assert(get(0x144) == 1.0f && bbgpu_camera_locked());
    std::puts("PASS: native camera signature gate, patch, yaw/pitch and lock-on");
    return 0;
}
#else
int main() { std::puts("SKIP: Windows-only direct camera"); }
#endif
