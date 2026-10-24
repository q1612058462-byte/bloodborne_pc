// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once

#include <cstdint>

namespace VideoCore {

struct VramGcThresholds {
    std::uint64_t trigger;
    std::uint64_t pressure;
    std::uint64_t critical;
    bool low_vram;
};

// The driver budget already accounts for other processes and the desktop. Keep
// a wider idle window on 4-4.5 GiB cards, whose normal working set is close to
// the older fixed pressure mark. This is a GC target, not an allocation limit.
constexpr VramGcThresholds MakeVramGcThresholds(std::uint64_t budget,
                                                std::uint64_t dedicated_vram,
                                                bool integrated) {
    constexpr std::uint64_t low_vram_limit = 4608ULL << 20;
    const bool low_vram = !integrated &&
        ((dedicated_vram != 0 && dedicated_vram <= low_vram_limit) ||
         (dedicated_vram == 0 && budget <= low_vram_limit));
    return {budget / 100 * 70, budget / 100 * (low_vram ? 90 : 85),
            budget / 100 * 95, low_vram};
}

} // namespace VideoCore
