#include "pch.h"

CComModule _Module;

BEGIN_OBJECT_MAP(ObjectMap)
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

STDAPI DllGetActivationFactory(HSTRING activatableClassId, IActivationFactory** factory)
{
    return E_NOTIMPL;
}

STDAPI DllRegisterServer()
{
    return _Module.RegisterServer(TRUE);
}

STDAPI DllUnregisterServer()
{
    return _Module.UnregisterServer(TRUE);
}