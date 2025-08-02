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

#include "../framework.h"
#include "LanguageProfile.h"

CLanguageProfile::CLanguageProfile() :
    _clientId(TF_CLIENTID_NULL)
{
}

CLanguageProfile::~CLanguageProfile()
{
    if (_clientId != TF_CLIENTID_NULL && _pInputProcessorProfiles)
    {
        UnregisterProfile(_clientId);
    }
}

HRESULT CLanguageProfile::RegisterProfile(TfClientId clientId)
{
    std::lock_guard<std::mutex> lock(_mutex);

    _clientId = clientId;

    HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles,
        (void**)&_pInputProcessorProfiles);

    if (FAILED(hr))
        return hr;

    hr = _RegisterLanguageProfile(clientId);
    if (FAILED(hr))
        return hr;

    return _RegisterCategory();
}

HRESULT CLanguageProfile::UnregisterProfile(TfClientId clientId)
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pInputProcessorProfiles)
        return S_OK;

    HRESULT hr = _pInputProcessorProfiles->RemoveLanguageProfile(
        GUID_STTIFY_TIP_TEXTSERVICE,
        STTIFY_TIP_LANGID,
        GUID_STTIFY_TIP_LANGPROFILE);

    // Unregister from categories
    CComPtr<ITfCategoryMgr> pCategoryMgr;
    hr = CoCreateInstance(CLSID_TF_CategoryMgr,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_ITfCategoryMgr,
        (void**)&pCategoryMgr);

    if (SUCCEEDED(hr))
    {
        pCategoryMgr->UnregisterCategory(GUID_STTIFY_TIP_TEXTSERVICE,
            GUID_TFCAT_TIP_KEYBOARD,
            GUID_STTIFY_TIP_TEXTSERVICE);
    }

    _pInputProcessorProfiles.Release();
    _clientId = TF_CLIENTID_NULL;

    return S_OK;
}

HRESULT CLanguageProfile::ActivateProfile()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pInputProcessorProfiles)
        return E_FAIL;

    return _pInputProcessorProfiles->ActivateLanguageProfile(
        GUID_STTIFY_TIP_TEXTSERVICE,
        STTIFY_TIP_LANGID,
        GUID_STTIFY_TIP_LANGPROFILE);
}

HRESULT CLanguageProfile::DeactivateProfile()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pInputProcessorProfiles)
        return E_FAIL;

    // TSF automatically handles deactivation when another profile is activated
    return S_OK;
}

BOOL CLanguageProfile::IsProfileActive()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pInputProcessorProfiles)
        return FALSE;

    LANGID langid;
    GUID guidProfile;

    HRESULT hr = _pInputProcessorProfiles->GetCurrentLanguage(&langid);
    if (FAILED(hr) || langid != STTIFY_TIP_LANGID)
        return FALSE;

    hr = _pInputProcessorProfiles->GetActiveLanguageProfile(
        GUID_STTIFY_TIP_TEXTSERVICE,
        &langid,
        &guidProfile);

    return (SUCCEEDED(hr) && IsEqualGUID(guidProfile, GUID_STTIFY_TIP_LANGPROFILE));
}

HRESULT CLanguageProfile::RegisterTextService()
{
    CComPtr<ITfInputProcessorProfiles> pInputProcessorProfiles;
    HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles,
        (void**)&pInputProcessorProfiles);

    if (FAILED(hr))
        return hr;

    // Register the text service
    hr = pInputProcessorProfiles->Register(GUID_STTIFY_TIP_TEXTSERVICE);
    if (FAILED(hr))
        return hr;

    // Add language profile
    hr = pInputProcessorProfiles->AddLanguageProfile(
        GUID_STTIFY_TIP_TEXTSERVICE,
        STTIFY_TIP_LANGID,
        GUID_STTIFY_TIP_LANGPROFILE,
        STTIFY_TIP_DESC,
        (ULONG)wcslen(STTIFY_TIP_DESC),
        nullptr,
        0,
        0);

    return hr;
}

HRESULT CLanguageProfile::UnregisterTextService()
{
    CComPtr<ITfInputProcessorProfiles> pInputProcessorProfiles;
    HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles,
        (void**)&pInputProcessorProfiles);

    if (FAILED(hr))
        return hr;

    // Remove language profile
    hr = pInputProcessorProfiles->RemoveLanguageProfile(
        GUID_STTIFY_TIP_TEXTSERVICE,
        STTIFY_TIP_LANGID,
        GUID_STTIFY_TIP_LANGPROFILE);

    // Unregister the text service
    hr = pInputProcessorProfiles->Unregister(GUID_STTIFY_TIP_TEXTSERVICE);

    return hr;
}

HRESULT CLanguageProfile::_RegisterLanguageProfile(TfClientId clientId)
{
    if (!_pInputProcessorProfiles)
        return E_FAIL;

    HRESULT hr = _pInputProcessorProfiles->Register(GUID_STTIFY_TIP_TEXTSERVICE);
    if (FAILED(hr))
        return hr;

    return _pInputProcessorProfiles->AddLanguageProfile(
        GUID_STTIFY_TIP_TEXTSERVICE,
        STTIFY_TIP_LANGID,
        GUID_STTIFY_TIP_LANGPROFILE,
        STTIFY_TIP_DESC,
        (ULONG)wcslen(STTIFY_TIP_DESC),
        nullptr,
        0,
        0);
}

HRESULT CLanguageProfile::_RegisterCategory()
{
    CComPtr<ITfCategoryMgr> pCategoryMgr;
    HRESULT hr = CoCreateInstance(CLSID_TF_CategoryMgr,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_ITfCategoryMgr,
        (void**)&pCategoryMgr);

    if (FAILED(hr))
        return hr;

    // Register as a keyboard TIP
    hr = pCategoryMgr->RegisterCategory(GUID_STTIFY_TIP_TEXTSERVICE,
        GUID_TFCAT_TIP_KEYBOARD,
        GUID_STTIFY_TIP_TEXTSERVICE);

    return hr;
}

