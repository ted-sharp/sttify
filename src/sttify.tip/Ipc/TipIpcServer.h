#pragma once
#include "../pch.h"

class CTipIpcServer
{
public:
    CTipIpcServer();
    ~CTipIpcServer();

    HRESULT Start();
    void Stop();
    BOOL IsRunning() const { return _isRunning; }

    void SetTextService(class CTextService* pTextService);

private:
    static DWORD WINAPI _ServerThreadProc(LPVOID lpParameter);
    DWORD _ServerThread();
    
    HRESULT _ProcessMessage(const std::wstring& message);
    std::vector<std::wstring> _SplitString(const std::wstring& str, wchar_t delimiter);

    HANDLE _hServerThread;
    HANDLE _hStopEvent;
    std::atomic<BOOL> _isRunning;
    
    class CTextService* _pTextService;
    mutable std::mutex _mutex;

    static constexpr const wchar_t* PIPE_NAME = L"\\\\.\\pipe\\sttify_tip_ipc";
    static constexpr DWORD BUFFER_SIZE = 4096;
};