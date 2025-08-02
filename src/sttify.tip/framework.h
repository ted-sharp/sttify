#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msctf.h>
#include <atlbase.h>
#include <atlcom.h>
#include <atlstr.h>

#include <memory>
#include <string>
#include <vector>
#include <mutex>
#include <thread>
#include <atomic>

// TSF GUIDs - use extern references, actual definition in dedicated .cpp
extern "C" const GUID GUID_STTIFY_TIP_TEXTSERVICE;
extern "C" const GUID GUID_STTIFY_TIP_LANGPROFILE;

// Constants (extern declarations)
extern "C" const LANGID STTIFY_TIP_LANGID;
extern "C" const wchar_t STTIFY_TIP_DESC[];
extern "C" const wchar_t STTIFY_TIP_MODEL[];

// Forward declarations
class CTextService;

