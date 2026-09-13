#pragma once

#include "listen_n/types.hpp"

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace listen_n {

// Stateful because transport reads need not end on an eight-byte event boundary.
class ProtocolDecoder final {
public:
    [[nodiscard]] static DetectionEvent decode_word(std::span<const std::byte, 8> word);
    [[nodiscard]] std::vector<DetectionEvent> push(std::span<const std::byte> bytes);
    [[nodiscard]] std::size_t pending_bytes() const noexcept { return pending_.size(); }
    void reset() noexcept { pending_.clear(); }

private:
    std::vector<std::byte> pending_;
};

} // namespace listen_n
