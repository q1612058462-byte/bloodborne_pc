// SPDX-License-Identifier: GPL-2.0-or-later
// Direct camera hook adapted from imedved's Bloodborne camera and the
// Supermedo/mcrib884 bbport port
// (https://github.com/Supermedo/bloodborne_pc/blob/windows/gpu/shim/bloodborne_cam.cpp).
// The hook is deliberately installed only after
// every affected Bloodborne 1.09 instruction has been checked byte for byte.
#include "bloodborne_cam.h"

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cstdlib>

#ifdef _WIN32
#include <windows.h>

namespace {
constexpr size_t kMain = 0x143CEAA;
constexpr size_t kNops[] = {0x143C6E8, 0x143C984, 0x143DDE6, 0x143C870};
constexpr size_t kMonocular = 0x553E8D0;
constexpr unsigned char kMainBytes[18] = {
    0xC4,0xC1,0x7A,0x10,0x85,0x40,0x01,0x00,0x00,
    0xC4,0xC1,0x7A,0x10,0x8D,0x50,0x01,0x00,0x00};
constexpr unsigned char kNopBytes[4][9] = {
    {0xC4,0xC1,0x7A,0x11,0x85,0x44,0x01,0x00,0x00},
    {0xC4,0xC1,0x7A,0x11,0x8D,0x30,0x01,0x00,0x00},
    {0xC4,0xC1,0x7A,0x11,0x85,0x94,0x02,0x00,0x00},
    {0xC4,0xC1,0x7A,0x11,0xAD,0x44,0x01,0x00,0x00}};

std::atomic<bool> active{false};
std::atomic<bool> locked{false};
std::atomic<float> pending_x{0}, pending_y{0};
std::atomic<unsigned long long> last_frame_ms{0};
float sensitivity = 1.0f;
float invert_y = 1.0f;
uintptr_t monocular_base = 0;

void add(std::atomic<float>& dst, float value) {
    float old = dst.load(std::memory_order_relaxed);
    while (!dst.compare_exchange_weak(old, old + value, std::memory_order_relaxed)) {}
}

bool patch(uintptr_t address, const unsigned char* bytes, size_t length) {
    DWORD old = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), length, PAGE_EXECUTE_READWRITE, &old)) return false;
    std::memcpy(reinterpret_cast<void*>(address), bytes, length);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), length);
    DWORD ignored = 0;
    VirtualProtect(reinterpret_cast<void*>(address), length, old, &ignored);
    return true;
}

float readf(uintptr_t ptr, size_t offset) { return *reinterpret_cast<volatile float*>(ptr + offset); }
void writef(uintptr_t ptr, size_t offset, float value) {
    *reinterpret_cast<volatile float*>(ptr + offset) = value;
}

void turn_drawn_position(uintptr_t camera, float dp, float dy) {
    const float px = readf(camera, 0xD0), py = readf(camera, 0xD4), pz = readf(camera, 0xD8);
    const double ox = readf(camera, 0x100) - px, oy = readf(camera, 0x104) - py;
    const double oz = readf(camera, 0x108) - pz;
    const double radius = std::sqrt(ox*ox + oy*oy + oz*oz);
    if (!(radius > 1e-3 && radius < 100.0)) return; // cutscene or a transition
    const double pitch = std::asin(std::fmax(-1.0, std::fmin(1.0, oy/radius))) + dp;
    const double yaw = std::atan2(-ox, -oz) + dy;
    writef(camera, 0x100, static_cast<float>(px - std::sin(yaw)*std::cos(pitch)*radius));
    writef(camera, 0x104, static_cast<float>(py + std::sin(pitch)*radius));
    writef(camera, 0x108, static_cast<float>(pz - std::cos(yaw)*std::cos(pitch)*radius));
}

bool accessible(uintptr_t ptr, size_t length, bool write = false) {
    struct CachedPage { uintptr_t start, end; DWORD protection; unsigned long long checked; };
    static thread_local CachedPage cache[4]{};
    static thread_local unsigned next = 0;
    const auto now = GetTickCount64();
    if (!ptr || !length || ptr > UINTPTR_MAX - length) return false;
    for (const auto& page : cache) {
        if (ptr >= page.start && ptr + length <= page.end && now - page.checked < 1000) {
            const DWORD protection = page.protection & 0xff;
            return !write || protection == PAGE_READWRITE || protection == PAGE_WRITECOPY ||
                protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
        }
    }
    MEMORY_BASIC_INFORMATION info{};
    if (VirtualQuery(reinterpret_cast<void*>(ptr), &info, sizeof(info)) != sizeof(info) ||
        info.State != MEM_COMMIT || (info.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    const uintptr_t start = reinterpret_cast<uintptr_t>(info.BaseAddress);
    if (!(ptr >= start && length <= info.RegionSize && ptr-start <= info.RegionSize-length)) return false;
    cache[next] = {start, start + info.RegionSize, info.Protect, now};
    next = (next + 1) % 4;
    const DWORD protection = info.Protect & 0xff;
    return !write || protection == PAGE_READWRITE || protection == PAGE_WRITECOPY ||
        protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
}
} // namespace

extern "C" {
uintptr_t bbcam_return = 0;
void bbcam_apply(uintptr_t camera);
extern char bbcam_stub[];
}

// Save all Win64 caller-saved state, including SIMD values and flags, before
// applying motion. r13 is the game's camera pointer at this exact instruction.
__asm__(".text\n"
        ".globl bbcam_stub\nbbcam_stub:\n"
        "  test %r13, %r13\n  jz 1f\n"
        "  movl $0x3F333333, 0x130(%r13)\n"
        "  lea -128(%rsp), %rsp\n  pushfq\n"
        "  push %rax\n  push %rcx\n  push %rdx\n  push %r8\n  push %r9\n  push %r10\n"
        "  push %r11\n  push %rbx\n"
        "  mov %rsp, %rbx\n  and $-16, %rsp\n  sub $0x80, %rsp\n"
        "  movdqu %xmm0, 0x20(%rsp)\n  movdqu %xmm1, 0x30(%rsp)\n  movdqu %xmm2, 0x40(%rsp)\n"
        "  movdqu %xmm3, 0x50(%rsp)\n  movdqu %xmm4, 0x60(%rsp)\n  movdqu %xmm5, 0x70(%rsp)\n"
        "  mov %r13, %rcx\n  call bbcam_apply\n"
        "  movdqu 0x20(%rsp), %xmm0\n  movdqu 0x30(%rsp), %xmm1\n  movdqu 0x40(%rsp), %xmm2\n"
        "  movdqu 0x50(%rsp), %xmm3\n  movdqu 0x60(%rsp), %xmm4\n  movdqu 0x70(%rsp), %xmm5\n"
        "  mov %rbx, %rsp\n"
        "  pop %rbx\n  pop %r11\n  pop %r10\n  pop %r9\n  pop %r8\n  pop %rdx\n  pop %rcx\n"
        "  pop %rax\n  popfq\n  lea 128(%rsp), %rsp\n"
        "  vmovss 0x140(%r13), %xmm0\n  vmovss 0x150(%r13), %xmm1\n"
        "1:\n  jmp *bbcam_return(%rip)\n");

extern "C" void bbcam_apply(uintptr_t camera) {
    last_frame_ms.store(GetTickCount64(), std::memory_order_relaxed);
    const bool is_locked = readf(camera, 0x154) == 1.0f;
    locked.store(is_locked, std::memory_order_relaxed);
    const float x = pending_x.exchange(0.0f, std::memory_order_relaxed);
    const float y = pending_y.exchange(0.0f, std::memory_order_relaxed);
    if (!active.load(std::memory_order_acquire) || is_locked || (x == 0 && y == 0)) return;

    const float delta_pitch = y * sensitivity * invert_y / 900.0f;
    const float delta_yaw = x * sensitivity / 900.0f;
    if (accessible(monocular_base, sizeof(uintptr_t))) {
        const uintptr_t p1 = *reinterpret_cast<volatile uintptr_t*>(monocular_base);
        if (p1 < UINTPTR_MAX - 0x68 && accessible(p1 + 0x68, sizeof(uintptr_t))) {
            const uintptr_t p2 = *reinterpret_cast<volatile uintptr_t*>(p1 + 0x68);
            if (p2 < UINTPTR_MAX - 0x84 && accessible(p2 + 0x68, sizeof(uintptr_t)) &&
                accessible(p2 + 0x84, sizeof(int)) &&
                *reinterpret_cast<volatile int*>(p2 + 0x84) != 0) {
                const uintptr_t p3 = *reinterpret_cast<volatile uintptr_t*>(p2 + 0x68);
                if (p3 < UINTPTR_MAX - 0x14C && accessible(p3 + 0x148, 8, true)) {
                    writef(p3, 0x148, std::fmax(-1.94f, std::fmin(1.71f,
                        readf(p3, 0x148) + delta_pitch * 0.5f)));
                    writef(p3, 0x14C, std::remainder(readf(p3, 0x14C) + delta_yaw * 0.5f,
                        6.28318530718f));
                    return;
                }
            }
        }
    }

    const float before = readf(camera, 0x140);
    float lo = readf(camera, 0x1F0), hi = readf(camera, 0x1EC);
    if (!(lo < hi && lo >= -3.0f && hi <= 3.0f)) { lo = -1.94f; hi = 1.71f; }
    const float pitch = std::fmax(lo, std::fmin(hi, before + delta_pitch));
    const float yaw = std::remainder(readf(camera, 0x144) + delta_yaw, 6.28318530718f);
    writef(camera, 0x140, pitch);
    writef(camera, 0x150, pitch);
    writef(camera, 0x26C, pitch);
    writef(camera, 0x144, yaw);
    writef(camera, 0x270, yaw);
    turn_drawn_position(camera, pitch - before, delta_yaw);
}

extern "C" int bbgpu_camera_install(void* image, uint64_t image_size) {
    const char* mode = std::getenv("BB_MOUSE_CAMERA");
    if (!mode || std::strcmp(mode, "direct") != 0 || !image ||
        image_size <= kMonocular + sizeof(uintptr_t)) return false;
    const char* mouse = std::getenv("BB_MOUSE_ENABLED");
    if (mouse && mouse[0] == '0') return false;
    auto* base = static_cast<unsigned char*>(image);
    if (std::memcmp(base + kMain, kMainBytes, sizeof(kMainBytes))) {
        std::puts("Mouse camera: 1.09 signature mismatch; retaining stick mode");
        return false;
    }
    for (size_t i = 0; i < 4; ++i) {
        if (std::memcmp(base + kNops[i], kNopBytes[i], 9)) {
            std::puts("Mouse camera: camera-write signature mismatch; retaining stick mode");
            return false;
        }
    }
    const char* scale = std::getenv("BB_MOUSE_DIRECT_SENSITIVITY");
    if (scale) {
        const float parsed = std::strtof(scale, nullptr);
        if (std::isfinite(parsed) && parsed >= 0.1f && parsed <= 10.0f) sensitivity = parsed;
    }
    const char* invert = std::getenv("BB_MOUSE_INVERT_Y");
    invert_y = invert && invert[0] == '1' ? -1.0f : 1.0f;
    bbcam_return = reinterpret_cast<uintptr_t>(base + kMain + sizeof(kMainBytes));
    monocular_base = reinterpret_cast<uintptr_t>(base + kMonocular);
    unsigned char jump[sizeof(kMainBytes)] = {0xFF, 0x25};
    const uintptr_t target = reinterpret_cast<uintptr_t>(bbcam_stub);
    std::memcpy(jump + 6, &target, sizeof(target));
    for (size_t i = 14; i < sizeof(jump); ++i) jump[i] = 0x90;
    unsigned char nops[9];
    std::memset(nops, 0x90, sizeof(nops));
    // All sites are validated before changing the first byte. Patching occurs
    // before the guest entry point, so no game thread can execute half a hook.
    for (size_t i = 0; i < 4; ++i) {
        if (!patch(reinterpret_cast<uintptr_t>(base + kNops[i]), nops, sizeof(nops))) {
            for (size_t j = 0; j < i; ++j)
                patch(reinterpret_cast<uintptr_t>(base + kNops[j]), kNopBytes[j], 9);
            std::puts("Mouse camera: protection change failed; retaining stick mode");
            return false;
        }
    }
    if (!patch(reinterpret_cast<uintptr_t>(base + kMain), jump, sizeof(jump))) {
        for (size_t i = 0; i < 4; ++i)
            patch(reinterpret_cast<uintptr_t>(base + kNops[i]), kNopBytes[i], 9);
        std::puts("Mouse camera: hook write failed; retaining stick mode");
        return false;
    }
    active.store(true, std::memory_order_release);
    std::printf("Mouse camera: native direct mode active (1.09, sensitivity %.1f)\n", sensitivity);
    return true;
}

extern "C" int bbgpu_camera_direct(void) { return active.load(std::memory_order_acquire); }
extern "C" int bbgpu_camera_locked(void) {
    const auto last = last_frame_ms.load(std::memory_order_relaxed);
    return active.load(std::memory_order_acquire) && last &&
        (locked.load(std::memory_order_relaxed) || GetTickCount64() - last > 100);
}
extern "C" void bbgpu_camera_motion(int32_t dx, int32_t dy) {
    if (!active.load(std::memory_order_acquire)) return;
    const auto last = last_frame_ms.load(std::memory_order_relaxed);
    if (!last || GetTickCount64() - last > 100) {
        pending_x.store(static_cast<float>(dx), std::memory_order_relaxed);
        pending_y.store(static_cast<float>(dy), std::memory_order_relaxed);
    } else {
        add(pending_x, static_cast<float>(dx));
        add(pending_y, static_cast<float>(dy));
    }
}
extern "C" void bbgpu_camera_clear_motion(void) {
    pending_x.store(0, std::memory_order_relaxed);
    pending_y.store(0, std::memory_order_relaxed);
}
#else
extern "C" int bbgpu_camera_install(void*, uint64_t) { return false; }
extern "C" int bbgpu_camera_direct(void) { return false; }
extern "C" int bbgpu_camera_locked(void) { return false; }
extern "C" void bbgpu_camera_motion(int32_t, int32_t) {}
extern "C" void bbgpu_camera_clear_motion(void) {}
#endif
