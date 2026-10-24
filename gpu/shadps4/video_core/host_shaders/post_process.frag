// SPDX-FileCopyrightText: Copyright 2024 shadPS4 Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

#version 450

layout (location = 0) in vec2 uv;
layout (location = 0) out vec4 color;

layout (binding = 0) uniform sampler2D texSampler;

layout (push_constant) uniform settings {
    float gamma;
    uint hdr;
    bool srgb_input;
    float peak_nits;
    float paper_white_nits;
    float highlight_strength;
    float input_aspect;
    float output_aspect;
} pp;

const float cutoff = 0.0031308, a = 1.055, b = 0.055, d = 12.92;
vec3 gamma(vec3 rgb) {
    return mix(
        a * pow(rgb, vec3(1.0 / (2.4 + 1.0 - pp.gamma))) - b,
        d * rgb / pp.gamma,
        lessThan(rgb, vec3(cutoff))
    );
}

// Exact inverse of gamma() at unit gamma, for buffers that are sRGB encoded but must be sampled
// through a UNORM view because Vulkan has no sRGB variant of the 10-bit format.
vec3 degamma(vec3 rgb) {
    return mix(
        pow(max(rgb + b, 0.0) / a, vec3(2.4)),
        rgb / d,
        lessThan(rgb, vec3(d * cutoff))
    );
}

// BT.709 linear RGB -> BT.2020 linear RGB, then ST 2084/PQ.
vec3 hdr10(vec3 srgb) {
    vec3 linear_rgb = pow(max(srgb, vec3(0.0)), vec3(2.2));
    float luminance = dot(linear_rgb, vec3(0.2126, 0.7152, 0.0722));
    float highlight = smoothstep(0.5, 1.0, luminance) * pp.highlight_strength;
    float gain = mix(pp.paper_white_nits, pp.peak_nits, highlight);
    vec3 nits = min(linear_rgb * gain, vec3(pp.peak_nits));
    vec3 bt2020 = vec3(
        dot(nits, vec3(0.627404, 0.329283, 0.043313)),
        dot(nits, vec3(0.069097, 0.919540, 0.011362)),
        dot(nits, vec3(0.016391, 0.088013, 0.895595)));
    vec3 x = pow(clamp(bt2020 / 10000.0, 0.0, 1.0), vec3(0.1593017578125));
    return pow((vec3(0.8359375) + 18.8515625 * x) /
               (vec3(1.0) + 18.6875 * x), vec3(78.84375));
}

void main() {
    vec2 sample_uv = uv;
    if (pp.hdr == 2u) {
        if (pp.input_aspect > pp.output_aspect) {
            float scale = pp.output_aspect / pp.input_aspect;
            sample_uv.y = (uv.y - (1.0 - scale) * 0.5) / scale;
        } else {
            float scale = pp.input_aspect / pp.output_aspect;
            sample_uv.x = (uv.x - (1.0 - scale) * 0.5) / scale;
        }
        if (any(lessThan(sample_uv, vec2(0.0))) ||
            any(greaterThan(sample_uv, vec2(1.0)))) {
            color = vec4(0.0, 0.0, 0.0, 1.0);
            return;
        }
        color = vec4(hdr10(texture(texSampler, sample_uv).rgb), 1.0);
        return;
    }
    vec4 color_linear = texture(texSampler, sample_uv);
    if (pp.hdr == 1u) {
        color = color_linear;
    } else {
        if (pp.srgb_input) color_linear.rgb = degamma(color_linear.rgb);
        color = vec4(gamma(color_linear.rgb), color_linear.a);
    }
}
