#pragma once

#include "listen_n/types.hpp"

#include <optional>
#include <vector>

namespace listen_n {

// Deterministic, synchronous domain core. It deliberately owns no transport,
// filesystem, GUI, logging, or worker-thread concerns.
class Analyzer final {
public:
    explicit Analyzer(AnalyzerConfig config);

    void push(DetectionEvent event);
    [[nodiscard]] std::optional<WindowSummary> force_step(std::int64_t end_us);
    [[nodiscard]] std::vector<WindowSummary> drain();
    void reset() noexcept;

private:
    [[nodiscard]] WindowSummary summarize(std::int64_t end_us);

    AnalyzerConfig config_;
    std::optional<std::int64_t> window_start_;
    std::vector<DetectionEvent> events_;
    std::vector<WindowSummary> ready_;
};

} // namespace listen_n
