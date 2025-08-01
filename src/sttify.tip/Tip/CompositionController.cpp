#include "../pch.h"
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

    HRESULT hr = _pComposition->EndComposition(nullptr);
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

    hr = pContextComposition->StartComposition(nullptr, pRange, nullptr, &_pComposition);
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

    TfEditCookie ec;
    hr = _pContext->RequestEditSession(_clientId, nullptr, TF_ES_READWRITE | TF_ES_SYNC, &ec);
    if (FAILED(hr))
        return hr;

    // Set the composition text
    hr = pRange->SetText(ec, 0, text.c_str(), (LONG)text.length());
    if (FAILED(hr))
        return hr;

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

    // Set display attributes for composition
    CComPtr<ITfProperty> pDisplayAttributeProperty;
    hr = _pContext->GetProperty(GUID_PROP_ATTRIBUTE, &pDisplayAttributeProperty);
    if (FAILED(hr))
        return hr;

    VARIANT var;
    VariantInit(&var);
    var.vt = VT_I4;
    var.lVal = TF_ATTR_INPUT; // Standard input composition attribute

    TfEditCookie ec;
    hr = _pContext->RequestEditSession(_clientId, nullptr, TF_ES_READWRITE | TF_ES_SYNC, &ec);
    if (SUCCEEDED(hr))
    {
        pDisplayAttributeProperty->SetValue(ec, pRange, &var);
    }

    VariantClear(&var);
    return hr;
}