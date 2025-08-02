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
#include "TipIpcServer.h"
#include "../Tip/TextService.h"

CTipIpcServer::CTipIpcServer() :
    _hServerThread(nullptr),
    _hStopEvent(nullptr),
    _isRunning(FALSE),
    _pTextService(nullptr)
{
}

CTipIpcServer::~CTipIpcServer()
{
    Stop();
}

HRESULT CTipIpcServer::Start()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (_isRunning)
        return S_OK;

    _hStopEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
    if (!_hStopEvent)
        return HRESULT_FROM_WIN32(GetLastError());

    _hServerThread = CreateThread(NULL, 0, _ServerThreadProc, this, 0, NULL);
    if (!_hServerThread)
    {
        CloseHandle(_hStopEvent);
        _hStopEvent = nullptr;
        return HRESULT_FROM_WIN32(GetLastError());
    }

    _isRunning = TRUE;
    return S_OK;
}

void CTipIpcServer::Stop()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (!_isRunning)
        return;

    _isRunning = FALSE;

    if (_hStopEvent)
    {
        SetEvent(_hStopEvent);
    }

    if (_hServerThread)
    {
        WaitForSingleObject(_hServerThread, 5000);
        CloseHandle(_hServerThread);
        _hServerThread = nullptr;
    }

    if (_hStopEvent)
    {
        CloseHandle(_hStopEvent);
        _hStopEvent = nullptr;
    }
}

void CTipIpcServer::SetTextService(CTextService* pTextService)
{
    std::lock_guard<std::mutex> lock(_mutex);
    _pTextService = pTextService;
}

DWORD WINAPI CTipIpcServer::_ServerThreadProc(LPVOID lpParameter)
{
    CTipIpcServer* pThis = static_cast<CTipIpcServer*>(lpParameter);
    return pThis->_ServerThread();
}

DWORD CTipIpcServer::_ServerThread()
{
    while (_isRunning)
    {
        HANDLE hPipe = CreateNamedPipe(
            PIPE_NAME,
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            1,
            BUFFER_SIZE,
            BUFFER_SIZE,
            0,
            NULL);

        if (hPipe == INVALID_HANDLE_VALUE)
        {
            Sleep(1000);
            continue;
        }

        HANDLE waitHandles[] = { _hStopEvent, hPipe };
        DWORD waitResult = WaitForMultipleObjects(2, waitHandles, FALSE, INFINITE);

        if (waitResult == WAIT_OBJECT_0) // Stop event
        {
            CloseHandle(hPipe);
            break;
        }

        BOOL connected = ConnectNamedPipe(hPipe, NULL) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);

        if (connected)
        {
            wchar_t buffer[BUFFER_SIZE];
            DWORD bytesRead;

            if (ReadFile(hPipe, buffer, sizeof(buffer) - sizeof(wchar_t), &bytesRead, NULL))
            {
                buffer[bytesRead / sizeof(wchar_t)] = L'\0';
                std::wstring message(buffer);
                _ProcessMessage(message);
            }
        }

        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }

    return 0;
}

HRESULT CTipIpcServer::_ProcessMessage(const std::wstring& message)
{
    if (message.empty())
        return E_INVALIDARG;

    auto parts = _SplitString(message, L'|');
    if (parts.empty())
        return E_INVALIDARG;

    const std::wstring& command = parts[0];

    if (command == L"SendText" && parts.size() >= 2)
    {
        if (_pTextService)
        {
            return _pTextService->SendText(parts[1]);
        }
    }
    else if (command == L"CanInsert")
    {
        if (_pTextService)
        {
            _pTextService->CanInsert();
        }
    }
    else if (command == L"SetMode" && parts.size() >= 2)
    {
        if (_pTextService)
        {
            _pTextService->SetMode(parts[1]);
        }
    }

    return S_OK;
}

std::vector<std::wstring> CTipIpcServer::_SplitString(const std::wstring& str, wchar_t delimiter)
{
    std::vector<std::wstring> result;
    std::wstring current;

    for (wchar_t ch : str)
    {
        if (ch == delimiter)
        {
            if (!current.empty())
            {
                result.push_back(current);
                current.clear();
            }
        }
        else
        {
            current += ch;
        }
    }

    if (!current.empty())
    {
        result.push_back(current);
    }

    return result;
}

