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
#include "CompositionController.h"

CCompositionController::CCompositionController() :
    _clientId(TF_CLIENTID_NULL)
{
}

CCompositionController::~CCompositionController()
{
    Uninitialize();
}

HRESULT CCompositionController::Initialize(ITfContext* pContext, TfClientId clientId)
{
    if (!pContext)
        return E_INVALIDARG;

    std::lock_guard<std::mutex> lock(_mutex);

    _pContext = pContext;
    _clientId = clientId;

    return S_OK;
}

void CCompositionController::Uninitialize()
{
    std::lock_guard<std::mutex> lock(_mutex);

    EndComposition();

    _pContext.Release();
    _clientId = TF_CLIENTID_NULL;
}

HRESULT CCompositionController::StartComposition(ITfRange* pRange)
{
    if (!pRange || !_pContext)
        return E_INVALIDARG;

    std::lock_guard<std::mutex> lock(_mutex);

    if (_pComposition)
    {
        // Composition already active
        return S_FALSE;
    }

    return _CreateComposition(pRange);
}

HRESULT CCompositionController::EndComposition()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pComposition)
        return S_FALSE;

    HRESULT hr = _pComposition->EndComposition(0);
    _pComposition.Release();
    _currentCompositionText.clear();

    return hr;
}

HRESULT CCompositionController::UpdateComposition(const std::wstring& text)
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pComposition)
        return E_FAIL;

    _currentCompositionText = text;
    return _UpdateCompositionString(text);
}

BOOL CCompositionController::IsCompositionInProgress()
{
    if (!_pContext)
        return FALSE;

    CComPtr<ITfContextComposition> pContextComposition;
    HRESULT hr = _pContext->QueryInterface(IID_ITfContextComposition, (void**)&pContextComposition);
    if (FAILED(hr))
        return FALSE;

    CComPtr<IEnumITfCompositionView> pEnumCompositionView;
    hr = pContextComposition->EnumCompositions(&pEnumCompositionView);
    if (FAILED(hr))
        return FALSE;

    CComPtr<ITfCompositionView> pCompositionView;
    ULONG fetched;
    hr = pEnumCompositionView->Next(1, &pCompositionView, &fetched);

    return (SUCCEEDED(hr) && fetched > 0);
}

HRESULT CCompositionController::_CreateComposition(ITfRange* pRange)
{
    if (!_pContext)
        return E_FAIL;

    CComPtr<ITfContextComposition> pContextComposition;
    HRESULT hr = _pContext->QueryInterface(IID_ITfContextComposition, (void**)&pContextComposition);
    if (FAILED(hr))
        return hr;

    hr = pContextComposition->StartComposition(0, pRange, nullptr, &_pComposition);
    if (FAILED(hr))
        return hr;

    return _SetCompositionDisplay();
}

HRESULT CCompositionController::_UpdateCompositionString(const std::wstring& text)
{
    if (!_pComposition)
        return E_FAIL;

    CComPtr<ITfRange> pRange;
    HRESULT hr = _pComposition->GetRange(&pRange);
    if (FAILED(hr))
        return hr;

    // For now, we'll use a simplified approach without edit session
    // In a full implementation, this would need proper edit session handling
    return S_OK;
}

HRESULT CCompositionController::_SetCompositionDisplay()
{
    if (!_pComposition)
        return E_FAIL;

    CComPtr<ITfRange> pRange;
    HRESULT hr = _pComposition->GetRange(&pRange);
    if (FAILED(hr))
        return hr;

    // For now, we'll use a simplified approach without edit session
    // In a full implementation, this would need proper edit session handling
    return S_OK;
}

