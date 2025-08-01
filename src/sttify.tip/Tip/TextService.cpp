#include "../pch.h"
#include "TextService.h"
#include "CompositionController.h"
#include "LanguageProfile.h"

CTextService::CTextService() :
    _tfClientId(TF_CLIENTID_NULL),
    _dwThreadMgrEventSinkCookie(TF_INVALID_COOKIE),
    _currentMode(L"final-only")
{
}

CTextService::~CTextService()
{
}

STDMETHODIMP CTextService::Activate(ITfThreadMgr* pThreadMgr, TfClientId tfClientId)
{
    if (!pThreadMgr)
        return E_INVALIDARG;

    _pThreadMgr = pThreadMgr;
    _tfClientId = tfClientId;

    HRESULT hr = _InitLanguageProfile();
    if (FAILED(hr))
        return hr;

    hr = _InitThreadMgrEventSink();
    if (FAILED(hr))
        return hr;

    return S_OK;
}

STDMETHODIMP CTextService::Deactivate()
{
    _UninitThreadMgrEventSink();

    _pThreadMgr.Release();
    _tfClientId = TF_CLIENTID_NULL;

    return S_OK;
}

STDMETHODIMP CTextService::OnInitDocumentMgr(ITfDocumentMgr* pDocMgr)
{
    return S_OK;
}

STDMETHODIMP CTextService::OnUninitDocumentMgr(ITfDocumentMgr* pDocMgr)
{
    return S_OK;
}

STDMETHODIMP CTextService::OnSetFocus(ITfDocumentMgr* pDocMgrFocus, ITfDocumentMgr* pDocMgrPrevFocus)
{
    return S_OK;
}

STDMETHODIMP CTextService::OnPushContext(ITfContext* pContext)
{
    return S_OK;
}

STDMETHODIMP CTextService::OnPopContext(ITfContext* pContext)
{
    return S_OK;
}

STDMETHODIMP CTextService::OnEndEdit(ITfContext* pContext, TfEditCookie ecReadOnly, ITfEditRecord* pEditRecord)
{
    return S_OK;
}

HRESULT CTextService::SendText(const std::wstring& text)
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pThreadMgr)
        return E_FAIL;

    if (_currentMode == L"final-only" && _IsCompositionActive())
        return S_FALSE; // Suppress insertion when IME is composing

    CComPtr<ITfDocumentMgr> pDocMgr;
    HRESULT hr = _pThreadMgr->GetFocus(&pDocMgr);
    if (FAILED(hr) || !pDocMgr)
        return hr;

    CComPtr<ITfContext> pContext;
    hr = pDocMgr->GetTop(&pContext);
    if (FAILED(hr) || !pContext)
        return hr;

    return _InsertTextAtSelection(pContext, text);
}

BOOL CTextService::CanInsert()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_pThreadMgr)
        return FALSE;

    if (_currentMode == L"final-only" && _IsCompositionActive())
        return FALSE;

    CComPtr<ITfDocumentMgr> pDocMgr;
    HRESULT hr = _pThreadMgr->GetFocus(&pDocMgr);
    if (FAILED(hr) || !pDocMgr)
        return FALSE;

    return TRUE;
}

void CTextService::SetMode(const std::wstring& mode)
{
    std::lock_guard<std::mutex> lock(_mutex);
    _currentMode = mode;
}

HRESULT CTextService::_InitLanguageProfile()
{
    CComPtr<ITfInputProcessorProfiles> pInputProcessorProfiles;
    HRESULT hr = CoCreateInstance(CLSID_TF_InputProcessorProfiles,
        NULL,
        CLSCTX_INPROC_SERVER,
        IID_ITfInputProcessorProfiles,
        (void**)&pInputProcessorProfiles);

    if (FAILED(hr))
        return hr;

    hr = pInputProcessorProfiles->Register(_tfClientId);
    if (FAILED(hr))
        return hr;

    hr = pInputProcessorProfiles->AddLanguageProfile(_tfClientId,
        STTIFY_TIP_LANGID,
        GUID_STTIFY_TIP_LANGPROFILE,
        STTIFY_TIP_DESC,
        (ULONG)wcslen(STTIFY_TIP_DESC),
        NULL,
        0,
        0);

    return hr;
}

HRESULT CTextService::_InitThreadMgrEventSink()
{
    if (!_pThreadMgr)
        return E_FAIL;

    CComPtr<ITfSource> pSource;
    HRESULT hr = _pThreadMgr->QueryInterface(IID_ITfSource, (void**)&pSource);
    if (FAILED(hr))
        return hr;

    hr = pSource->AdviseSink(IID_ITfThreadMgrEventSink, (ITfThreadMgrEventSink*)this, &_dwThreadMgrEventSinkCookie);
    return hr;
}

HRESULT CTextService::_UninitThreadMgrEventSink()
{
    if (!_pThreadMgr || _dwThreadMgrEventSinkCookie == TF_INVALID_COOKIE)
        return S_OK;

    CComPtr<ITfSource> pSource;
    HRESULT hr = _pThreadMgr->QueryInterface(IID_ITfSource, (void**)&pSource);
    if (SUCCEEDED(hr))
    {
        pSource->UnadviseSink(_dwThreadMgrEventSinkCookie);
        _dwThreadMgrEventSinkCookie = TF_INVALID_COOKIE;
    }

    return hr;
}

HRESULT CTextService::_InsertTextAtSelection(ITfContext* pContext, const std::wstring& text)
{
    if (!pContext || text.empty())
        return E_INVALIDARG;

    TfEditCookie ec;
    HRESULT hr = pContext->RequestEditSession(_tfClientId, this, TF_ES_READWRITE | TF_ES_SYNC, &ec);
    if (FAILED(hr))
        return hr;

    CComPtr<ITfInsertAtSelection> pInsertAtSelection;
    hr = pContext->QueryInterface(IID_ITfInsertAtSelection, (void**)&pInsertAtSelection);
    if (FAILED(hr))
        return hr;

    CComPtr<ITfRange> pRange;
    hr = pInsertAtSelection->InsertTextAtSelection(ec, 0, text.c_str(), (LONG)text.length(), &pRange);
    
    return hr;
}

BOOL CTextService::_IsCompositionActive()
{
    if (!_pThreadMgr)
        return FALSE;

    CComPtr<ITfDocumentMgr> pDocMgr;
    HRESULT hr = _pThreadMgr->GetFocus(&pDocMgr);
    if (FAILED(hr) || !pDocMgr)
        return FALSE;

    CComPtr<ITfContext> pContext;
    hr = pDocMgr->GetTop(&pContext);
    if (FAILED(hr) || !pContext)
        return FALSE;

    CComPtr<ITfContextComposition> pContextComposition;
    hr = pContext->QueryInterface(IID_ITfContextComposition, (void**)&pContextComposition);
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