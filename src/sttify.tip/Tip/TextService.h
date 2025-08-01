#pragma once
#include "../pch.h"

class CTextService : 
    public CComObjectRootEx<CComSingleThreadModel>,
    public CComCoClass<CTextService, &GUID_STTIFY_TIP_TEXTSERVICE>,
    public ITfTextInputProcessor,
    public ITfThreadMgrEventSink,
    public ITfTextEditSink
{
public:
    CTextService();
    virtual ~CTextService();

    DECLARE_REGISTRY_RESOURCEID(IDR_TEXTSERVICE)
    DECLARE_NOT_AGGREGATABLE(CTextService)

    BEGIN_COM_MAP(CTextService)
        COM_INTERFACE_ENTRY(ITfTextInputProcessor)
        COM_INTERFACE_ENTRY(ITfThreadMgrEventSink)
        COM_INTERFACE_ENTRY(ITfTextEditSink)
    END_COM_MAP()

    // ITfTextInputProcessor
    STDMETHODIMP Activate(ITfThreadMgr* pThreadMgr, TfClientId tfClientId) override;
    STDMETHODIMP Deactivate() override;

    // ITfThreadMgrEventSink
    STDMETHODIMP OnInitDocumentMgr(ITfDocumentMgr* pDocMgr) override;
    STDMETHODIMP OnUninitDocumentMgr(ITfDocumentMgr* pDocMgr) override;
    STDMETHODIMP OnSetFocus(ITfDocumentMgr* pDocMgrFocus, ITfDocumentMgr* pDocMgrPrevFocus) override;
    STDMETHODIMP OnPushContext(ITfContext* pContext) override;
    STDMETHODIMP OnPopContext(ITfContext* pContext) override;

    // ITfTextEditSink
    STDMETHODIMP OnEndEdit(ITfContext* pContext, TfEditCookie ecReadOnly, ITfEditRecord* pEditRecord) override;

    // Public methods
    HRESULT SendText(const std::wstring& text);
    BOOL CanInsert();
    void SetMode(const std::wstring& mode);

private:
    HRESULT _InitLanguageProfile();
    HRESULT _InitThreadMgrEventSink();
    HRESULT _UninitThreadMgrEventSink();
    HRESULT _InsertTextAtSelection(ITfContext* pContext, const std::wstring& text);
    BOOL _IsCompositionActive();

    CComPtr<ITfThreadMgr> _pThreadMgr;
    TfClientId _tfClientId;
    DWORD _dwThreadMgrEventSinkCookie;
    
    std::wstring _currentMode;
    std::mutex _mutex;
};