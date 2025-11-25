#pragma once

#ifdef CONFIGURE_EXPORTS
#define CONFIGURE_API extern "C" __declspec(dllexport)
#else
#define CONFIGURE_API extern "C" __declspec(dllimport)
#endif

CONFIGURE_API int GetVs1Count();
CONFIGURE_API const char* GetVs1Name(int index);
CONFIGURE_API int GetVs2Count();
CONFIGURE_API const char* GetVs2Name(int index);