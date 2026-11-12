// MilookDeckBridge.cpp - MilookDeck 用 TVTest プラグイン
// 名前付きパイプ \\.\pipe\MilookDeck_<TVTestのPID> でコマンドを受け、TVTest を操作する。
//   SETSVC <nid> <tsid> <sid>  : チャンネルリストから該当サービスを探して切り替え
//   GETSTATE                   : "OK driver=<BonDriver名> <nid> <tsid> <sid>" を返す
// ビルド: README.md 参照(TVTestPlugin.h は TVTest の SDK から入手。TVTest.exe と同じビット数でビルド)
#define TVTEST_PLUGIN_CLASS_IMPLEMENT
#include <windows.h>
#include <cstdio>
#include <cstring>
#include "TVTestPlugin.h"

class CMilookDeckBridge : public TVTest::CTVTestPlugin
{
    static const UINT WM_BRIDGE_CMD = WM_APP + 1;
    struct Cmd { char in[256]; char out[512]; };

    HANDLE m_hThread = nullptr;
    HWND m_hwnd = nullptr;
    volatile bool m_stop = false;
    WCHAR m_pipeName[64] = {};

public:
    bool GetPluginInfo(TVTest::PluginInfo *pInfo) override
    {
        pInfo->Type = TVTest::PLUGIN_TYPE_NORMAL;
        pInfo->Flags = 0;
        pInfo->pszPluginName = L"MilookDeckBridge";
        pInfo->pszCopyright = L"MilookDeck";
        pInfo->pszDescription = L"MilookDeck との連携(名前付きパイプ)";
        return true;
    }

    bool Initialize() override
    {
        HINSTANCE hInst = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&WndProc), &hInst);
        WNDCLASSW wc = {};
        wc.lpfnWndProc = WndProc;
        wc.hInstance = hInst;
        wc.lpszClassName = L"MilookDeckBridgeWindow";
        RegisterClassW(&wc);
        // メッセージ専用ウィンドウ(TVTest のメインスレッドで作る=API 呼び出しもこのスレッドで行う)
        m_hwnd = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, hInst, nullptr);
        if (!m_hwnd) return false;
        SetWindowLongPtrW(m_hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));

        swprintf_s(m_pipeName, L"\\\\.\\pipe\\MilookDeck_%lu", GetCurrentProcessId());
        m_hThread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
        return m_hThread != nullptr;
    }

    bool Finalize() override
    {
        m_stop = true;
        // accept 待ちのスレッドを起こす
        HANDLE h = CreateFileW(m_pipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
        if (m_hThread) { WaitForSingleObject(m_hThread, 2000); CloseHandle(m_hThread); m_hThread = nullptr; }
        if (m_hwnd) { DestroyWindow(m_hwnd); m_hwnd = nullptr; }
        return true;
    }

private:
    static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        if (msg == WM_BRIDGE_CMD)
        {
            auto *self = reinterpret_cast<CMilookDeckBridge *>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
            if (self) self->Handle(reinterpret_cast<Cmd *>(lp));
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    static DWORD WINAPI ThreadProc(LPVOID p) { return static_cast<CMilookDeckBridge *>(p)->PipeLoop(); }

    DWORD PipeLoop()
    {
        while (!m_stop)
        {
            HANDLE h = CreateNamedPipeW(m_pipeName, PIPE_ACCESS_DUPLEX,
                                        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
            if (h == INVALID_HANDLE_VALUE) break;
            if (ConnectNamedPipe(h, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
            {
                if (!m_stop)
                {
                    Cmd c = {};
                    DWORD n = 0; size_t len = 0;
                    while (len < sizeof(c.in) - 1 && ReadFile(h, c.in + len, 1, &n, nullptr) && n == 1)
                    {
                        if (c.in[len] == '\n') break;
                        len++;
                    }
                    c.in[len] = '\0';
                    if (len > 0 && c.in[len - 1] == '\r') c.in[len - 1] = '\0';
                    SendMessageW(m_hwnd, WM_BRIDGE_CMD, 0, reinterpret_cast<LPARAM>(&c));   // TVTest のスレッドで実行
                    strcat_s(c.out, "\n");
                    WriteFile(h, c.out, static_cast<DWORD>(strlen(c.out)), &n, nullptr);
                    FlushFileBuffers(h);
                }
            }
            DisconnectNamedPipe(h);
            CloseHandle(h);
        }
        return 0;
    }

    // ---- TVTest のメインスレッドで呼ばれる ----
    void Handle(Cmd *c)
    {
        unsigned nid, tsid, sid;
        if (sscanf_s(c->in, "SETSVC %u %u %u", &nid, &tsid, &sid) == 3)
        {
            int numSpaces = 0;
            m_pApp->GetTuningSpace(&numSpaces);
            for (int sp = 0; sp < numSpaces; sp++)
            {
                for (int ch = 0;; ch++)
                {
                    TVTest::ChannelInfo ci = {};
                    ci.Size = sizeof(ci);
                    if (!m_pApp->GetChannelInfo(sp, ch, &ci)) break;
                    if (ci.NetworkID == nid && ci.TransportStreamID == tsid && ci.ServiceID == sid)
                    {
                        bool ok = m_pApp->SetChannel(sp, ch, static_cast<WORD>(sid));
                        strcpy_s(c->out, ok ? "OK" : "ERR SetChannel failed");
                        return;
                    }
                }
            }
            strcpy_s(c->out, "ERR service not found in channel list");
        }
        else if (strcmp(c->in, "GETSTATE") == 0)
        {
            WCHAR drv[MAX_PATH] = {};
            m_pApp->GetDriverName(drv, MAX_PATH);
            const WCHAR *name = wcsrchr(drv, L'\\');
            name = name ? name + 1 : drv;
            char u8[MAX_PATH * 3] = {};
            WideCharToMultiByte(CP_UTF8, 0, name, -1, u8, sizeof(u8), nullptr, nullptr);
            TVTest::ChannelInfo ci = {};
            ci.Size = sizeof(ci);
            if (m_pApp->GetCurrentChannelInfo(&ci))
                sprintf_s(c->out, "OK driver=%s %u %u %u", u8, (unsigned)ci.NetworkID, (unsigned)ci.TransportStreamID, (unsigned)ci.ServiceID);
            else
                sprintf_s(c->out, "OK driver=%s 0 0 0", u8);
        }
        else
            strcpy_s(c->out, "ERR unknown command");
    }
};

TVTest::CTVTestPlugin *CreatePluginClass() { return new CMilookDeckBridge; }
