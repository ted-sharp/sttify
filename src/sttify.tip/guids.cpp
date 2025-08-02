#include <windows.h>
#include <initguid.h>

// TSF GUIDs - actual definitions
DEFINE_GUID(GUID_STTIFY_TIP_TEXTSERVICE, 0x12345678, 0x1234, 0x5678, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0);
DEFINE_GUID(GUID_STTIFY_TIP_LANGPROFILE, 0x87654321, 0x4321, 0x8765, 0x21, 0x43, 0x65, 0x87, 0xA9, 0xCB, 0xED, 0x0F);

// Constants definitions
extern "C" const LANGID STTIFY_TIP_LANGID = MAKELANGID(LANG_JAPANESE, SUBLANG_JAPANESE_JAPAN);
extern "C" const wchar_t STTIFY_TIP_DESC[] = L"Sttify Text Input Processor";
extern "C" const wchar_t STTIFY_TIP_MODEL[] = L"Apartment";