#include "listen_n/c_api.h"
#include "listen_n/analyzer.hpp"

#include <algorithm>
#include <memory>
#include <string>
#include <vector>

struct ln_core {
    explicit ln_core(const ln_config& config)
        : analyzer({config.window_us, config.gate_us}) {}
    listen_n::Analyzer analyzer;
    std::vector<listen_n::WindowSummary> pending_output;
    std::string error;
};

namespace {
template<class Function>
int guarded(ln_core_handle core, Function&& function) noexcept {
    if (!core) return 1;
    try {
        function();
        core->error.clear();
        return 0;
    } catch (const std::exception& error) {
        core->error = error.what();
        return 2;
    } catch (...) {
        core->error = "unknown C++ exception";
        return 2;
    }
}
}

int ln_core_create(const ln_config* config, ln_core_handle* out) {
    if (!config || !out) return 1;
    try {
        *out = new ln_core(*config);
        return 0;
    } catch (...) {
        *out = nullptr;
        return 2;
    }
}

void ln_core_destroy(ln_core_handle core) { delete core; }
int ln_core_reset(ln_core_handle core) {
    return guarded(core, [&] { core->analyzer.reset(); core->pending_output.clear(); });
}

int ln_core_push_events(ln_core_handle core, const ln_event* events, size_t count) {
    if (count != 0 && !events) return 1;
    return guarded(core, [&] {
        for (std::size_t i = 0; i < count; ++i) {
            core->analyzer.push({events[i].timestamp_us, events[i].channel_id});
        }
    });
}

int ln_core_force_step(ln_core_handle core, int64_t end_us) {
    return guarded(core, [&] { (void)core->analyzer.force_step(end_us); });
}

int ln_core_drain_windows(ln_core_handle core, ln_window_summary* out, size_t* inout_count) {
    if (!core || !inout_count) return 1;
    if (core->pending_output.empty()) core->pending_output = core->analyzer.drain();
    const auto& summaries = core->pending_output;
    if (!out) {
        *inout_count = summaries.size();
        return 0;
    }
    if (*inout_count < summaries.size()) { *inout_count = summaries.size(); return 3; }
    for (std::size_t i = 0; i < summaries.size(); ++i) {
        out[i].start_us = summaries[i].start_us;
        out[i].end_us = summaries[i].end_us;
        std::copy(summaries[i].counts.begin(), summaries[i].counts.end(), out[i].counts);
        out[i].accepted_events = summaries[i].accepted_events;
        out[i].ignored_channels = summaries[i].ignored_channels;
        out[i].mean_multiplicity = summaries[i].mean_multiplicity;
        out[i].feynman_y = summaries[i].feynman_y;
    }
    *inout_count = summaries.size();
    core->pending_output.clear();
    return 0;
}

const char* ln_core_last_error(ln_core_handle core) {
    return core ? core->error.c_str() : "null core handle";
}
