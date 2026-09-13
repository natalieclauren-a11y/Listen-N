#include "listen_n/analyzer.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace listen_n {

Analyzer::Analyzer(AnalyzerConfig config) : config_(config) {
    if (config_.window_us <= 0 || config_.gate_us <= 0 || config_.gate_us > config_.window_us) {
        throw std::invalid_argument("window_us and gate_us must be positive, with gate_us <= window_us");
    }
}

void Analyzer::push(DetectionEvent event) {
    if (event.timestamp_us < 0) throw std::invalid_argument("timestamps must be non-negative");
    if (!events_.empty() && event.timestamp_us < events_.back().timestamp_us) {
        throw std::invalid_argument("events must be timestamp ordered");
    }
    if (!window_start_) window_start_ = event.timestamp_us;
    while (event.timestamp_us >= *window_start_ + config_.window_us) {
        ready_.push_back(summarize(*window_start_ + config_.window_us));
        *window_start_ += config_.window_us;
    }
    events_.push_back(event);
}

std::optional<WindowSummary> Analyzer::force_step(std::int64_t end_us) {
    if (!window_start_) window_start_ = end_us - config_.window_us;
    if (end_us <= *window_start_) return std::nullopt;
    auto summary = summarize(end_us);
    window_start_ = end_us;
    ready_.push_back(summary);
    return summary;
}

WindowSummary Analyzer::summarize(std::int64_t end_us) {
    WindowSummary out{};
    out.start_us = *window_start_;
    out.end_us = end_us;

    const auto gate_count = static_cast<std::size_t>((end_us - *window_start_ + config_.gate_us - 1) / config_.gate_us);
    std::vector<std::uint64_t> bins(gate_count);
    for (const auto& event : events_) {
        if (event.timestamp_us < *window_start_ || event.timestamp_us >= end_us) continue;
        if (event.channel_id >= 1 && event.channel_id <= channel_count) {
            ++out.counts[event.channel_id - 1U];
            ++out.accepted_events;
            const auto gate = static_cast<std::size_t>((event.timestamp_us - *window_start_) / config_.gate_us);
            if (gate < bins.size()) ++bins[gate];
        } else {
            ++out.ignored_channels;
        }
    }
    if (!bins.empty()) {
        double sum = 0.0;
        double factorial2 = 0.0;
        for (const auto n : bins) {
            const auto value = static_cast<double>(n);
            sum += value;
            factorial2 += value * (value - 1.0);
        }
        out.mean_multiplicity = sum / static_cast<double>(bins.size());
        const auto m2 = factorial2 / static_cast<double>(bins.size());
        out.feynman_y = out.mean_multiplicity > 0.0
            ? (m2 - out.mean_multiplicity * out.mean_multiplicity) / out.mean_multiplicity
            : 0.0;
    }
    std::erase_if(events_, [end_us](const auto& event) { return event.timestamp_us < end_us; });
    return out;
}

std::vector<WindowSummary> Analyzer::drain() {
    auto result = std::move(ready_);
    ready_.clear();
    return result;
}

void Analyzer::reset() noexcept {
    window_start_.reset();
    events_.clear();
    ready_.clear();
}

} // namespace listen_n
