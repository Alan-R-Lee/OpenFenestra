// SPDX-License-Identifier: MIT
// Runtime/initializer comparison only; the hook still only acknowledges PID/TID.
#include <set>
#include <string>
#include <vector>

struct ProbeCppState {
    std::wstring token;
    std::set<std::wstring> assigned;
    std::vector<unsigned char> hidden;
};
// Intentionally matches the full bridge's process-lifetime C++ initialization.
ProbeCppState& probeCppState = *new ProbeCppState;

#define ProbeDllMain DllMain
#define PROBE_COMCTL
#define PROBE_SHELL
#include "BootstrapProbeImports.c"
