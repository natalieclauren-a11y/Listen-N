#include "listen_n/protocol_decoder.hpp"

#include <array>
#include <stdexcept>

namespace listen_n {

DetectionEvent ProtocolDecoder::decode_word(std::span<const std::byte, 8> bytes) {
    std::uint64_t word = 0;
    for (std::size_t i = 0; i < bytes.size(); ++i) {
        word |= std::uint64_t{std::to_integer<std::uint8_t>(bytes[i])} << (i * 8U);
    }
    return {static_cast<std::int64_t>((word >> 5U) / 100U),
            static_cast<std::uint8_t>(word & 0x1FU)};
}

std::vector<DetectionEvent> ProtocolDecoder::push(std::span<const std::byte> bytes) {
    pending_.insert(pending_.end(), bytes.begin(), bytes.end());
    std::vector<DetectionEvent> result;
    const auto words = pending_.size() / 8U;
    result.reserve(words);
    for (std::size_t i = 0; i < words; ++i) {
        const auto* begin = pending_.data() + i * 8U;
        result.push_back(decode_word(std::span<const std::byte, 8>{begin, 8U}));
    }
    pending_.erase(pending_.begin(), pending_.begin() + static_cast<std::ptrdiff_t>(words * 8U));
    return result;
}

} // namespace listen_n
