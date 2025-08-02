#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atlbase.h>
#include <atlcom.h>
#include "Tip/TextService.h"

// GUIDs and constants defined in guids.cpp

CComModule _Module;

BEGIN_OBJECT_MAP(ObjectMap)
    OBJECT_ENTRY(GUID_STTIFY_TIP_TEXTSERVICE, CTextService)
END_OBJECT_MAP()

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        _Module.Init(ObjectMap, hModule, &LIBID_ATLLib);
        DisableThreadLibraryCalls(hModule);
        break;
    case DLL_PROCESS_DETACH:
        _Module.Term();
        break;
    }
    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return (_Module.GetLockCount() == 0) ? S_OK : S_FALSE;
}


STDAPI DllRegisterServer()
{
    return _Module.RegisterServer(TRUE);
}

STDAPI DllUnregisterServer()
{
    return _Module.UnregisterServer(TRUE);
}

