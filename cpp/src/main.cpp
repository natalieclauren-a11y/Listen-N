#include "listen_n/analyzer.hpp"

#include <charconv>
#include <algorithm>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

namespace {
void usage() {
    std::cerr << "Usage: listen-n-cli replay INPUT [--window-us N] [--gate-us N]\n"
                 "Input is timestamp_us[,channel_id], one event per line.\n";
}

bool parse_i64(const std::string& value, std::int64_t& result) {
    const auto parsed = std::from_chars(value.data(), value.data() + value.size(), result);
    return parsed.ec == std::errc{} && parsed.ptr == value.data() + value.size();
}
}

int main(int argc, char** argv) {
    if (argc < 3 || std::string_view(argv[1]) != "replay") { usage(); return 2; }
    listen_n::AnalyzerConfig config;
    for (int i = 3; i < argc; ++i) {
        const std::string option = argv[i];
        if (i + 1 >= argc || (option != "--window-us" && option != "--gate-us")) {
            usage(); return 2;
        }
        std::int64_t value{};
        if (!parse_i64(argv[++i], value)) { std::cerr << "Invalid integer option\n"; return 2; }
        (option == "--window-us" ? config.window_us : config.gate_us) = value;
    }

    std::ifstream input(argv[2]);
    if (!input) { std::cerr << "Cannot open " << argv[2] << '\n'; return 1; }
    try {
        listen_n::Analyzer analyzer(config);
        std::string line;
        std::int64_t last{};
        bool found = false;
        while (std::getline(input, line)) {
            if (line.empty() || line[0] == '#') continue;
            std::replace(line.begin(), line.end(), ',', ' ');
            std::istringstream fields(line);
            std::int64_t timestamp{};
            unsigned channel = 1;
            if (!(fields >> timestamp)) { std::cerr << "Malformed input line: " << line << '\n'; return 1; }
            fields >> channel;
            if (channel > 255U) { std::cerr << "Channel out of byte range\n"; return 1; }
            analyzer.push({timestamp, static_cast<std::uint8_t>(channel)});
            last = timestamp;
            found = true;
        }
        if (found) (void)analyzer.force_step(last + 1);
        std::cout << "start_us,end_us,accepted,ignored,mean_multiplicity,feynman_y";
        for (std::size_t i = 1; i <= listen_n::channel_count; ++i) std::cout << ",channel_" << i;
        std::cout << '\n';
        for (const auto& row : analyzer.drain()) {
            std::cout << row.start_us << ',' << row.end_us << ',' << row.accepted_events << ','
                      << row.ignored_channels << ',' << row.mean_multiplicity << ',' << row.feynman_y;
            for (const auto count : row.counts) std::cout << ',' << count;
            std::cout << '\n';
        }
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
