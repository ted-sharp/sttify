#pragma once
#include "../pch.h"

class CLanguageProfile
{
public:
    CLanguageProfile();
    ~CLanguageProfile();

    HRESULT RegisterProfile(TfClientId clientId);
    HRESULT UnregisterProfile(TfClientId clientId);
    
    HRESULT ActivateProfile();
    HRESULT DeactivateProfile();
    
    BOOL IsProfileActive();
    
    static HRESULT RegisterTextService();
    static HRESULT UnregisterTextService();

private:
    HRESULT _RegisterLanguageProfile(TfClientId clientId);
    HRESULT _RegisterCategory();
    
    CComPtr<ITfInputProcessorProfiles> _pInputProcessorProfiles;
    TfClientId _clientId;
    mutable std::mutex _mutex;
};