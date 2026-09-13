#include "listen_n/analyzer.hpp"
#include "listen_n/c_api.h"
#include "listen_n/protocol_decoder.hpp"

#include <array>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <stdexcept>

namespace {
void require(bool condition, const char* message) {
    if (!condition) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}

std::array<std::byte, 8> encode(std::int64_t timestamp_us, std::uint8_t channel) {
    const auto value = (static_cast<std::uint64_t>(timestamp_us) * 100U << 5U) | (channel & 0x1FU);
    std::array<std::byte, 8> bytes{};
    for (std::size_t i = 0; i < bytes.size(); ++i) bytes[i] = std::byte(value >> (i * 8U));
    return bytes;
}
}

int main() {
    const auto bytes = encode(123'456, 3);
    require(listen_n::ProtocolDecoder::decode_word(bytes) == listen_n::DetectionEvent{123'456, 3},
            "binary decoder parity");

    listen_n::ProtocolDecoder stream;
    require(stream.push({bytes.data(), 3}).empty(), "partial words are buffered");
    require(stream.pending_bytes() == 3, "pending byte count");
    const auto decoded = stream.push({bytes.data() + 3, 5});
    require(decoded.size() == 1 && decoded[0].timestamp_us == 123'456, "chunked decode");

    listen_n::Analyzer analyzer({1'000, 100});
    analyzer.push({0, 1});
    analyzer.push({10, 1});
    analyzer.push({150, 2});
    analyzer.push({200, 0});
    (void)analyzer.force_step(1'000);
    const auto windows = analyzer.drain();
    require(windows.size() == 1, "one forced window");
    require(windows[0].accepted_events == 3 && windows[0].ignored_channels == 1, "channel validation");
    require(windows[0].counts[0] == 2 && windows[0].counts[1] == 1, "per-channel counts");
    require(std::abs(windows[0].mean_multiplicity - 0.3) < 1e-12, "mean multiplicity");

    bool rejected = false;
    try { analyzer.push({2'000, 1}); analyzer.push({1'999, 1}); } catch (const std::invalid_argument&) { rejected = true; }
    require(rejected, "out-of-order input is rejected");

    ln_core_handle handle{};
    const ln_config config{1'000, 100};
    require(ln_core_create(&config, &handle) == 0, "C API create");
    const ln_event c_events[]{{0, 1}, {20, 2}};
    require(ln_core_push_events(handle, c_events, 2) == 0, "C API event batch");
    require(ln_core_force_step(handle, 1'000) == 0, "C API force step");
    std::size_t count{};
    require(ln_core_drain_windows(handle, nullptr, &count) == 0 && count == 1, "C API size query");
    ln_window_summary summary{};
    require(ln_core_drain_windows(handle, &summary, &count) == 0, "C API drain");
    require(count == 1 && summary.accepted_events == 2, "C API result parity");
    ln_core_destroy(handle);
    std::cout << "All Listen-N C++ core tests passed\n";
}
