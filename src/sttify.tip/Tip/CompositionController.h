#pragma once
#include "../pch.h"

class CCompositionController
{
public:
    CCompositionController();
    ~CCompositionController();

    HRESULT Initialize(ITfContext* pContext, TfClientId clientId);
    void Uninitialize();

    HRESULT StartComposition(ITfRange* pRange);
    HRESULT EndComposition();
    HRESULT UpdateComposition(const std::wstring& text);

    BOOL IsCompositionActive() const { return _pComposition != nullptr; }
    BOOL IsCompositionInProgress();

private:
    HRESULT _CreateComposition(ITfRange* pRange);
    HRESULT _UpdateCompositionString(const std::wstring& text);
    HRESULT _SetCompositionDisplay();

    CComPtr<ITfContext> _pContext;
    CComPtr<ITfComposition> _pComposition;
    TfClientId _clientId;

    std::wstring _currentCompositionText;
    mutable std::mutex _mutex;
};

