#include <cassert>
#include <cstdio>
#include "../gpu/shadps4/video_core/texture_cache/vram_gc_policy.h"

int main() {
    constexpr auto four_gb = VideoCore::MakeVramGcThresholds(3800ULL << 20, 4096ULL << 20, false);
    static_assert(four_gb.low_vram);
    static_assert(four_gb.trigger < four_gb.pressure && four_gb.pressure < four_gb.critical);
    static_assert(four_gb.pressure == (3800ULL << 20) / 100 * 90);
    constexpr auto eight_gb = VideoCore::MakeVramGcThresholds(7500ULL << 20, 8192ULL << 20, false);
    static_assert(!eight_gb.low_vram && eight_gb.pressure == (7500ULL << 20) / 100 * 85);
    constexpr auto integrated = VideoCore::MakeVramGcThresholds(3000ULL << 20, 0, true);
    static_assert(!integrated.low_vram);
    constexpr auto missing = VideoCore::MakeVramGcThresholds(0, 0, false);
    static_assert(missing.trigger == 0 && missing.pressure == 0 && missing.critical == 0);
    std::puts("PASS: VRAM GC thresholds for low-VRAM, desktop, and integrated GPUs");
}
