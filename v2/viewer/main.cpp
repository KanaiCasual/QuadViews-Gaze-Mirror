// QuadViews Gaze Mirror 2.0 - dev viewer.
//
// The smallest possible reader of the 2.0 format (v2\protocol\gaze_mirror_protocol.h): registers as a reader, sleeps
// until the producer signals a frame, shows it. No timer anywhere - while no game runs it waits without waking up.
// It exists so that the new layer can be tested before the OBS plugin and the real mirror window speak the format;
// OBS can record it with Window Capture in the meantime.
//
// Keys: Esc quit.   The title bar shows the game, the picture size and the frame rate that arrives.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstdio>

#include "../protocol/gaze_mirror_protocol.h"

using Microsoft::WRL::ComPtr;
using namespace gaze_mirror;

namespace {

    const char ShaderSource[] = R"(
Texture2D picture : register(t0);
SamplerState smooth : register(s0);
struct V { float4 p : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID) {
    V o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.p = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 ps(V i) : SV_Target { return float4(picture.Sample(smooth, i.uv).rgb, 1); }
)";

    HWND g_window = nullptr;
    bool g_running = true;
    bool g_resized = true;
    bool g_repaint = false;

    LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case WM_SIZE: g_resized = true; return 0;
        case WM_PAINT: g_repaint = true; ValidateRect(window, nullptr); return 0;
        case WM_KEYDOWN: if (wParam == VK_ESCAPE) g_running = false; return 0;
        case WM_CLOSE: g_running = false; return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    struct Graphics {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<IDXGISwapChain1> swapchain;
        ComPtr<ID3D11RenderTargetView> target;
        ComPtr<ID3D11VertexShader> vertexShader;
        ComPtr<ID3D11PixelShader> pixelShader;
        ComPtr<ID3D11SamplerState> sampler;
        UINT width = 0, height = 0;
        LUID luid{};
    };

    // A device on the graphics card the producer's textures live on (a shared texture cannot cross cards).
    bool CreateGraphics(Graphics& graphics, LUID wanted) {
        graphics = {};
        ComPtr<IDXGIFactory2> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
        ComPtr<IDXGIAdapter1> adapter, chosen;
        for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
            DXGI_ADAPTER_DESC1 desc;
            adapter->GetDesc1(&desc);
            if (desc.AdapterLuid.LowPart == wanted.LowPart && desc.AdapterLuid.HighPart == wanted.HighPart) chosen = adapter;
        }
        const HRESULT made = D3D11CreateDevice(chosen.Get(), chosen ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                                               nullptr, 0, D3D11_SDK_VERSION, &graphics.device, nullptr, &graphics.context);
        if (FAILED(made)) return false;
        graphics.luid = wanted;

        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        if (FAILED(factory->CreateSwapChainForHwnd(graphics.device.Get(), g_window, &desc, nullptr, nullptr, &graphics.swapchain))) return false;

        ComPtr<ID3DBlob> vertexCode, pixelCode;
        if (FAILED(D3DCompile(ShaderSource, sizeof(ShaderSource) - 1, nullptr, nullptr, nullptr, "vs", "vs_5_0", 0, 0, &vertexCode, nullptr)) ||
            FAILED(D3DCompile(ShaderSource, sizeof(ShaderSource) - 1, nullptr, nullptr, nullptr, "ps", "ps_5_0", 0, 0, &pixelCode, nullptr))) return false;
        graphics.device->CreateVertexShader(vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(), nullptr, &graphics.vertexShader);
        graphics.device->CreatePixelShader(pixelCode->GetBufferPointer(), pixelCode->GetBufferSize(), nullptr, &graphics.pixelShader);
        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        graphics.device->CreateSamplerState(&sampler, &graphics.sampler);
        g_resized = true;
        return true;
    }

    void ResizeTarget(Graphics& graphics) {
        graphics.target.Reset();
        graphics.context->ClearState();
        graphics.swapchain->ResizeBuffers(0, 0, 0, DXGI_FORMAT_UNKNOWN, 0);
        ComPtr<ID3D11Texture2D> backBuffer;
        graphics.swapchain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        D3D11_TEXTURE2D_DESC desc;
        backBuffer->GetDesc(&desc);
        graphics.width = desc.Width;
        graphics.height = desc.Height;
        graphics.device->CreateRenderTargetView(backBuffer.Get(), nullptr, &graphics.target);
    }

    void Draw(Graphics& graphics, ID3D11ShaderResourceView* picture, UINT pictureWidth, UINT pictureHeight) {
        if (!graphics.target || graphics.width == 0 || graphics.height == 0) return;
        const float black[4] = {0.02f, 0.02f, 0.02f, 1};
        graphics.context->ClearRenderTargetView(graphics.target.Get(), black);
        if (picture && pictureWidth && pictureHeight) {
            const float aspect = float(pictureWidth) / float(pictureHeight);
            float width = float(graphics.width), height = float(graphics.height);
            if (width / height > aspect) width = height * aspect;
            else height = width / aspect;
            const D3D11_VIEWPORT viewport{(graphics.width - width) / 2, (graphics.height - height) / 2, width, height, 0, 1};
            graphics.context->OMSetRenderTargets(1, graphics.target.GetAddressOf(), nullptr);
            graphics.context->RSSetViewports(1, &viewport);
            graphics.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            graphics.context->VSSetShader(graphics.vertexShader.Get(), nullptr, 0);
            graphics.context->PSSetShader(graphics.pixelShader.Get(), nullptr, 0);
            graphics.context->PSSetShaderResources(0, 1, &picture);
            graphics.context->PSSetSamplers(0, 1, graphics.sampler.GetAddressOf());
            graphics.context->Draw(3, 0);
            ID3D11ShaderResourceView* none = nullptr;
            graphics.context->PSSetShaderResources(0, 1, &none);
        }
        graphics.swapchain->Present(1, 0);
    }

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Frames), FramesMappingName);
    Frames* frames = mapping ? static_cast<Frames*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Frames))) : nullptr;
    if (!frames) {
        MessageBoxW(nullptr, L"The shared block could not be opened.", L"Gaze Mirror 2.0 dev viewer", MB_OK | MB_ICONWARNING);
        return 1;
    }
    if (frames->magic == 0) { // We are first: stamp it, the producer fills in the rest.
        frames->magic = FramesMagic;
        frames->version = FramesVersion;
        frames->structSize = sizeof(Frames);
        frames->latestSlot = NoSlot;
    }
    if (frames->version != FramesVersion) {
        MessageBoxW(nullptr, L"A component with a different format version is running.", L"Gaze Mirror 2.0 dev viewer", MB_OK | MB_ICONWARNING);
        return 1;
    }

    // Take a reader slot.
    const LONG myPid = static_cast<LONG>(GetCurrentProcessId());
    int mySlot = -1;
    for (uint32_t i = 0; i < ReaderCount && mySlot < 0; i++) {
        if (InterlockedCompareExchange(&frames->readers[i].pid, myPid, 0) == 0) mySlot = static_cast<int>(i);
    }
    if (mySlot < 0) {
        MessageBoxW(nullptr, L"All reader places are taken.", L"Gaze Mirror 2.0 dev viewer", MB_OK | MB_ICONWARNING);
        return 1;
    }
    Reader& me = frames->readers[mySlot];
    me.readingSlot = NoSlot;
    me.heartbeatMs = static_cast<LONGLONG>(GetTickCount64());
    wchar_t eventName[64];
    swprintf_s(eventName, ReaderEventFormat, static_cast<unsigned>(mySlot));
    HANDLE frameReady = CreateEventW(nullptr, FALSE, FALSE, eventName);
    if (HANDLE wake = CreateEventW(nullptr, FALSE, FALSE, ProducerWakeName)) { // A sleeping producer: there is a reader now.
        SetEvent(wake);
        CloseHandle(wake);
    }

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure;
    windowClass.hInstance = instance;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.lpszClassName = L"GazeMirror2DevViewer";
    RegisterClassW(&windowClass);
    RECT rect{0, 0, 1280, 720};
    AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
    g_window = CreateWindowW(windowClass.lpszClassName, L"Gaze Mirror 2.0 dev viewer - waiting for a game", WS_OVERLAPPEDWINDOW,
                             CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, instance, nullptr);
    ShowWindow(g_window, SW_SHOWNOACTIVATE);

    Graphics graphics;
    ComPtr<ID3D11ShaderResourceView> pictures[SlotCount];
    LONG seenGeneration = -1;
    LONGLONG seenFrame = -1;
    int shownSlot = NoSlot;
    ULONGLONG rateStart = GetTickCount64();
    int rateFrames = 0;
    float rate = 0;

    while (g_running) {
        // Sleep until a frame is signalled or the window has something to say. No time-out: nothing to do otherwise.
        const DWORD woke = MsgWaitForMultipleObjects(1, &frameReady, FALSE, INFINITE, QS_ALLINPUT);
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (!g_running) break;
        if (woke == WAIT_OBJECT_0) me.heartbeatMs = static_cast<LONGLONG>(GetTickCount64());

        // Textures were (re)made, or the producer came or went.
        const LONG generation = frames->generation;
        if (generation != seenGeneration) {
            seenGeneration = generation;
            for (auto& picture : pictures) picture.Reset();
            shownSlot = NoSlot;
            if (frames->producerKind != ProducerNone) {
                const LUID luid{frames->adapterLuidLow, frames->adapterLuidHigh};
                if (!graphics.device || graphics.luid.LowPart != luid.LowPart || graphics.luid.HighPart != luid.HighPart) CreateGraphics(graphics, luid);
            } else {
                SetWindowTextW(g_window, L"Gaze Mirror 2.0 dev viewer - waiting for a game");
            }
            g_repaint = true;
        }
        if (!graphics.device && !CreateGraphics(graphics, LUID{})) break;
        if (g_resized) {
            g_resized = false;
            ResizeTarget(graphics);
            g_repaint = true;
        }

        const LONGLONG frameNumber = frames->frameNumber;
        const bool fresh = frameNumber != seenFrame && frames->latestSlot != NoSlot && frames->producerKind != ProducerNone;
        if (!fresh && !g_repaint) continue;
        g_repaint = false;

        ID3D11ShaderResourceView* picture = nullptr;
        if (frames->producerKind != ProducerNone && frames->latestSlot != NoSlot) {
            // Mark the slot we are about to use, and make sure it still was the latest when the mark went up.
            LONG slot;
            do {
                slot = frames->latestSlot;
                me.readingSlot = slot;
            } while (slot != frames->latestSlot);
            if (slot >= 0 && slot < static_cast<LONG>(SlotCount)) {
                if (!pictures[slot] && frames->textureHandle[slot] != 0) {
                    ComPtr<ID3D11Texture2D> texture;
                    const HANDLE handle = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(frames->textureHandle[slot]));
                    if (SUCCEEDED(graphics.device->OpenSharedResource(handle, IID_PPV_ARGS(&texture)))) {
                        graphics.device->CreateShaderResourceView(texture.Get(), nullptr, &pictures[slot]);
                    }
                }
                picture = pictures[slot].Get();
                shownSlot = slot;
            }
        }
        Draw(graphics, picture, frames->width, frames->height);
        graphics.context->Flush();
        me.readingSlot = NoSlot;

        if (fresh) {
            seenFrame = frameNumber;
            rateFrames++;
            const ULONGLONG now = GetTickCount64();
            if (now - rateStart >= 1000) {
                rate = rateFrames * 1000.f / float(now - rateStart);
                rateStart = now;
                rateFrames = 0;
                wchar_t title[256];
                swprintf_s(title, L"Gaze Mirror 2.0 dev viewer - %hs - %u x %u - %s eye - gaze %s - %.0f fps shown", frames->producerProgram,
                           frames->width, frames->height, frames->eye == 0 ? L"left" : L"right", frames->gazeValid ? L"yes" : L"no", rate);
                SetWindowTextW(g_window, title);
            }
        }
    }

    me.readingSlot = NoSlot;
    InterlockedExchange(&me.pid, 0);
    CloseHandle(frameReady);
    UnmapViewOfFile(frames);
    CloseHandle(mapping);
    return 0;
}
