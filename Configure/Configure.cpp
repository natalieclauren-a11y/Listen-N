#include "pch.h"
#include "Configure.h"

static const char* vs1List[] = {
    "U-238",
    "Pu-240",
    "Pu-240 (6%)",
    "Cf-252",
    "alpha-n",
    // Add more if needed
};
static const char* vs2List[] = {
    "U-238",
    "Pu-240",
    "Pu-240 (6%)",
    "Cf-252",
    "U-238 (LLNL)",
    "alpha-n",
    "Pb-TorC"
    // Add more if you want
};

static const int vs1Count = sizeof(vs1List) / sizeof(vs1List[0]);

CONFIGURE_API int GetVs1Count() {
    return vs1Count;
}

CONFIGURE_API const char* GetVs1Name(int index) {
    if (index < 0 || index >= vs1Count) return "";
    return vs1List[index];
}

static const int vs2Count = sizeof(vs2List) / sizeof(vs2List[0]);
extern "C" __declspec(dllexport) int GetVs2Count() {
    return vs2Count;
}

extern "C" __declspec(dllexport) const char* GetVs2Name(int index) {
    if (index < 0 || index >= vs2Count) return "";
    return vs2List[index];
}

