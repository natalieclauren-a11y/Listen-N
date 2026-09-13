#pragma once

#include <array>
#include <cstdint>

namespace listen_n {

inline constexpr std::size_t channel_count = 15;

struct DetectionEvent {
    std::int64_t timestamp_us{};
    std::uint8_t channel_id{};

    friend bool operator==(const DetectionEvent&, const DetectionEvent&) = default;
};

struct AnalyzerConfig {
    std::int64_t window_us{1'000'000};
    std::int64_t gate_us{1'000};
};

struct WindowSummary {
    std::int64_t start_us{};
    std::int64_t end_us{};
    std::array<std::uint64_t, channel_count> counts{};
    std::uint64_t accepted_events{};
    std::uint64_t ignored_channels{};
    double mean_multiplicity{};
    double feynman_y{};
};

} // namespace listen_n
