// QuadViews Gaze Mirror - mirror window.
//
// Shows the picture the mirror layer produces (cropped, with the gaze ring, following the gaze) in an ordinary window,
// so that anything that can capture a window can use it: OBS Window/Game Capture, Discord, ... - no OBS plugin needed.
//
// How it gets the picture: the layer publishes shared-texture handles in the "OpenXROBSMirrorSurface" block (the same
// one the OBS plugin reads). This program opens that texture on its own D3D11 device and draws it, scaled with a
// footprint filter, into a flip-model swap chain. Like the plugin it bumps `frameNumber` in that block, which is how the
// layer knows somebody wants frames (with no reader the layer idles).
// Layers from 1.2 on also know this window by its own block ("GazeOverlay.MirrorWindow", see eye_gaze_export.h): there
// it says "I am reading" and whether the OBS plugin should get a blank picture meanwhile, finds the real textures, and
// the layer signals an event after each finished frame so that this program sleeps until there is something to show.
//
// Starting it again while it runs does not open a second window: the new arguments are handed to the running one. That
// is how the settings app controls it (the window itself usually sits behind the game, out of reach).
//
// Usage: MirrorWindow.exe [--eye left|right] [--monitor N [--titled 0|1] | --windowed] [--size WxH|fill] [--fps N]
//                         [--exclusive 0|1] [--close]
//   --monitor N   filling monitor N (1 = primary) and kept BEHIND every other window: the place for it while streaming.
//                 Window capture still sees it there. A mouse click does not activate it.
//   --titled 1    with --monitor: keep a title bar (pushed off the top of the monitor) instead of being borderless, for
//                 capture tools that only list windows that have one.
//   --windowed    an ordinary resizable window (default), to look at it.
//   --size WxH    the size of the picture area in real pixels = what a capture tool gets (1920x1080, ...). With
//                 --monitor the window then sits in that monitor's top left corner instead of covering it. "fill"
//                 (default) = the whole monitor, or 1280x720 for an ordinary window. Smaller is cheaper.
//   --fps N       draw at most N pictures a second (default 60; 30 halves the work again). Frames in between are
//                 skipped - nothing is timed or polled, the game's frames still drive it.
//   --exclusive 1 while this window is open the OBS plugin gets a blank picture (default), 0 = both get the picture.
//   --close       close the running mirror window.
// The same choices are in the window's right-click menu (and its system menu, for when it is behind everything).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <mmsystem.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

    // Layout of the block shared with the mirror layer (MirrorSurfaceData in its dx11mirror.cpp).
    struct MirrorSurfaceData {
        uint32_t lastProcessedIndex;
        uint32_t frameNumber;
        uint32_t eyeIndex; // 0 = left, 1 = right, 2 = both
        float overlap;
        float blend;
        float blendPos;
        uint64_t sharedHandle[3];
    };
    constexpr wchar_t SurfaceBlockName[] = L"OpenXROBSMirrorSurface";

    // This window's own block (MirrorWindowBlock in eye_gaze_export.h). `magic` is set by a layer that knows about it.
    struct MirrorWindowBlock {
        uint32_t magic;
        uint32_t version;
        volatile LONG readerHeartbeat;
        volatile LONG exclusive;
        uint64_t sharedHandle[3];
    };
    constexpr wchar_t WindowBlockName[] = L"GazeOverlay.MirrorWindow";
    constexpr uint32_t WindowBlockMagic = 0x574D4F47; // 'GOMW'
    constexpr ULONG_PTR ArgumentsMessage = 0x474F4D57; // WM_COPYDATA tag: a command line for the running window
    // Newer layers signal this after each frame. It is created here (or by the layer - whoever starts first), so the
    // window can simply wait on it. An older layer never signals it; then a counter in the block is watched instead.
    constexpr wchar_t FrameEventName[] = L"GazeOverlay.MirrorFrame";
    constexpr wchar_t WindowClass[] = L"QuadViewsGazeMirror.MirrorWindow";
    constexpr wchar_t WindowTitle[] = L"QuadViews Gaze Mirror - Mirror";

    enum : UINT {
        CmdWindowed = 0x1000, // Below 0xF000 so they can live in the system menu too; low 4 bits unused.
        CmdEyeLeft = 0x1010,
        CmdEyeRight = 0x1020,
        CmdClose = 0x1040,
        CmdTitled = 0x1050,
        CmdFps60 = 0x1060,
        CmdFps30 = 0x1070,
        CmdMonitorFirst = 0x1100, // + 0x10 per monitor
    };

    const char ShaderSource[] = R"(
cbuffer Constants : register(b0) {
    float taps;      // samples per axis over each output pixel's footprint (1..4)
    float toSrgb;    // 1 = the source holds linear light and has to be encoded for display
    float2 padding;
};
Texture2D source : register(t0);
SamplerState linearClamp : register(s0);

struct VsOut { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

VsOut vs_main(uint id : SV_VertexID) {
    VsOut output;
    output.uv = float2((id << 1) & 2, id & 2);
    output.position = float4(output.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

float4 ps_main(VsOut input) : SV_TARGET {
    float2 stepX = ddx(input.uv);
    float2 stepY = ddy(input.uv);
    int count = (int)taps;
    float3 sum = float3(0, 0, 0);
    [loop] for (int a = 0; a < count; a++) {
        [loop] for (int b = 0; b < count; b++) {
            float2 offset = ((a + 0.5) / count - 0.5) * stepX + ((b + 0.5) / count - 0.5) * stepY;
            sum += source.SampleLevel(linearClamp, input.uv + offset, 0).rgb;
        }
    }
    float3 color = sum / (count * count);
    if (toSrgb > 0.5) {
        color = saturate(color);
        color = lerp(color * 12.92, 1.055 * pow(color, 1.0 / 2.4) - 0.055, step(0.0031308, color));
    }
    return float4(color, 1);
}
)";

    struct Constants {
        float taps;
        float toSrgb;
        float padding[2];
    };

    HWND g_window = nullptr;
    std::vector<RECT> g_monitors;
    int g_monitor = 0; // 0 = windowed, otherwise 1-based monitor index
    bool g_titled = false; // On a monitor: keep a (hidden) title bar instead of being borderless.
    int g_sizeWidth = 0, g_sizeHeight = 0; // Picture area in pixels; 0 = fill the monitor / the usual window size.
    int g_fps = 60;        // At most this many pictures a second.
    int g_eye = -1;    // -1 = leave whatever is set
    int g_exclusive = 1;
    bool g_closeRequested = false;

    ComPtr<ID3D11Device> g_device;
    ComPtr<ID3D11DeviceContext> g_context;
    ComPtr<IDXGISwapChain1> g_swapChain;
    ComPtr<ID3D11RenderTargetView> g_target;
    ComPtr<ID3D11VertexShader> g_vertexShader;
    ComPtr<ID3D11PixelShader> g_pixelShader;
    ComPtr<ID3D11SamplerState> g_sampler;
    ComPtr<ID3D11Buffer> g_constants;
    ComPtr<ID3D11Texture2D> g_source;
    ComPtr<ID3D11ShaderResourceView> g_sourceView;
    D3D11_TEXTURE2D_DESC g_sourceDesc{};
    uint64_t g_sourceHandle = 0;
    UINT g_width = 0, g_height = 0;
    bool g_resized = true;

    MirrorSurfaceData* g_surface = nullptr;
    MirrorWindowBlock* g_own = nullptr;

    void ParseArguments(const wchar_t* commandLine);
    void ApplyArguments();

    void SetStatus(const wchar_t* status) {
        static std::wstring last;
        std::wstring title = std::wstring(WindowTitle) + (status && *status ? std::wstring(L" - ") + status : L"");
        if (title != last) {
            last = title;
            SetWindowTextW(g_window, title.c_str());
        }
    }

    BOOL CALLBACK CollectMonitor(HMONITOR, HDC, LPRECT rect, LPARAM) {
        g_monitors.push_back(*rect);
        return TRUE;
    }

    void ListMonitors() {
        g_monitors.clear();
        EnumDisplayMonitors(nullptr, nullptr, CollectMonitor, 0);
        // Primary first (it contains the origin), the rest left to right.
        std::stable_sort(g_monitors.begin(), g_monitors.end(), [](const RECT& a, const RECT& b) {
            const bool primaryA = a.left == 0 && a.top == 0, primaryB = b.left == 0 && b.top == 0;
            return primaryA != primaryB ? primaryA : a.left < b.left;
        });
    }

    // Windowed: an ordinary window. On a monitor: its picture exactly covers the monitor and it stays at the bottom of the
    // window stack (see WM_WINDOWPOSCHANGING) - it is there to be captured, not to be looked at or clicked.
    // It must stay an ordinary application window in every other respect: Discord's window list left it out while it had
    // WS_EX_NOACTIVATE, so "never takes focus" is done by refusing mouse activation instead.
    void ApplyPlacement(bool force = true) {
        ListMonitors();
        if (g_monitor > static_cast<int>(g_monitors.size())) {
            g_monitor = 0;
        }
        // Arguments that change something else (the eye, ...) must not throw an ordinary window back to its start place.
        static int applied[4] = {-1, -1, -1, -1};
        const int wanted[4] = {g_monitor, g_titled ? 1 : 0, g_sizeWidth, g_sizeHeight};
        if (!force && std::equal(wanted, wanted + 4, applied)) {
            return;
        }
        std::copy(wanted, wanted + 4, applied);
        const bool sized = g_sizeWidth > 0 && g_sizeHeight > 0;
        if (g_monitor == 0) {
            SetWindowLongPtrW(g_window, GWL_STYLE, WS_OVERLAPPEDWINDOW | WS_VISIBLE);
            SetWindowLongPtrW(g_window, GWL_EXSTYLE, WS_EX_APPWINDOW);
            RECT frame{0, 0, sized ? g_sizeWidth : 1280, sized ? g_sizeHeight : 720};
            AdjustWindowRectExForDpi(&frame, WS_OVERLAPPEDWINDOW, FALSE, WS_EX_APPWINDOW, GetDpiForWindow(g_window));
            SetWindowPos(g_window, HWND_TOP, 120, 120, frame.right - frame.left, frame.bottom - frame.top,
                         SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        } else {
            RECT area = g_monitors[g_monitor - 1];
            if (sized) {
                // A chosen output size: that many pixels from the monitor's top left corner. It is behind everything
                // anyway, and a capture tool gets exactly this size - hanging over the monitor's edge does no harm.
                area.right = area.left + g_sizeWidth;
                area.bottom = area.top + g_sizeHeight;
            }
            const DWORD style = g_titled ? (WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX) : WS_POPUP;
            SetWindowLongPtrW(g_window, GWL_STYLE, style | WS_VISIBLE);
            SetWindowLongPtrW(g_window, GWL_EXSTYLE, WS_EX_APPWINDOW);
            if (g_titled) {
                // The CLIENT area covers the monitor; title bar and frame hang over its edges.
                const POINT middle{(area.left + area.right) / 2, (area.top + area.bottom) / 2};
                UINT dpiX = 96, dpiY = 96;
                HMODULE shcore = LoadLibraryW(L"shcore.dll");
                using GetDpiForMonitorFn = HRESULT(WINAPI*)(HMONITOR, int, UINT*, UINT*);
                if (auto getDpi = shcore ? reinterpret_cast<GetDpiForMonitorFn>(GetProcAddress(shcore, "GetDpiForMonitor")) : nullptr) {
                    getDpi(MonitorFromPoint(middle, MONITOR_DEFAULTTONEAREST), 0, &dpiX, &dpiY);
                }
                AdjustWindowRectExForDpi(&area, style, FALSE, WS_EX_APPWINDOW, dpiX);
            }
            SetWindowPos(g_window, HWND_BOTTOM, area.left, area.top, area.right - area.left, area.bottom - area.top,
                         SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    void FillMenu(HMENU menu) {
        ListMonitors();
        AppendMenuW(menu, MF_STRING | (g_monitor == 0 ? MF_CHECKED : 0), CmdWindowed, L"Ordinary window");
        for (size_t i = 0; i < g_monitors.size(); i++) {
            const RECT& area = g_monitors[i];
            wchar_t text[128];
            swprintf_s(text, L"Fill monitor %zu in the background (%ld x %ld)", i + 1, area.right - area.left, area.bottom - area.top);
            AppendMenuW(menu, MF_STRING | (g_monitor == static_cast<int>(i) + 1 ? MF_CHECKED : 0),
                        CmdMonitorFirst + static_cast<UINT>(i) * 0x10, text);
        }
        AppendMenuW(menu, MF_STRING | (g_titled ? MF_CHECKED : 0), CmdTitled, L"Keep a title bar when filling a monitor (try this if a capture tool does not list the window)");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        const uint32_t eye = g_surface ? g_surface->eyeIndex : 0;
        AppendMenuW(menu, MF_STRING | (eye == 0 ? MF_CHECKED : 0), CmdEyeLeft, L"Left eye");
        AppendMenuW(menu, MF_STRING | (eye == 1 ? MF_CHECKED : 0), CmdEyeRight, L"Right eye");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING | (g_fps > 30 ? MF_CHECKED : 0), CmdFps60, L"Up to 60 pictures a second");
        AppendMenuW(menu, MF_STRING | (g_fps <= 30 ? MF_CHECKED : 0), CmdFps30, L"Up to 30 pictures a second (cheaper)");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, CmdClose, L"Close the mirror window");
    }

    bool HandleCommand(UINT command) {
        if (command == CmdWindowed) {
            g_monitor = 0;
            ApplyPlacement();
        } else if (command >= CmdMonitorFirst && command < CmdMonitorFirst + 0x100) {
            g_monitor = static_cast<int>((command - CmdMonitorFirst) / 0x10) + 1;
            ApplyPlacement();
        } else if (command == CmdEyeLeft || command == CmdEyeRight) {
            if (g_surface) {
                g_surface->eyeIndex = command == CmdEyeLeft ? 0 : 1;
            }
        } else if (command == CmdFps60 || command == CmdFps30) {
            g_fps = command == CmdFps60 ? 60 : 30;
        } else if (command == CmdTitled) {
            g_titled = !g_titled;
            ApplyPlacement();
        } else if (command == CmdClose) {
            DestroyWindow(g_window);
        } else {
            return false;
        }
        return true;
    }

    LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
        switch (message) {
        case WM_SIZE:
            g_resized = true;
            return 0;
        case WM_CONTEXTMENU: {
            HMENU menu = CreatePopupMenu();
            FillMenu(menu);
            POINT where{static_cast<short>(LOWORD(lParam)), static_cast<short>(HIWORD(lParam))};
            if (where.x == -1 && where.y == -1) {
                GetCursorPos(&where);
            }
            SetForegroundWindow(window);
            const UINT command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, where.x, where.y, 0, window, nullptr);
            DestroyMenu(menu);
            HandleCommand(command);
            return 0;
        }
        case WM_INITMENUPOPUP:
            // The system menu (Alt+Space, or Shift+right-click on the taskbar button): same choices, rebuilt each time.
            if (HIWORD(lParam)) {
                HMENU menu = GetSystemMenu(window, TRUE);
                menu = GetSystemMenu(window, FALSE);
                AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
                FillMenu(menu);
            }
            break;
        case WM_SYSCOMMAND:
            if (HandleCommand(static_cast<UINT>(wParam) & 0xFFF0)) {
                return 0;
            }
            break;
        case WM_WINDOWPOSCHANGING:
            // Filling a monitor: whatever happens (taskbar click, Alt+Tab), stay underneath everything else.
            if (g_monitor != 0) {
                auto* position = reinterpret_cast<WINDOWPOS*>(lParam);
                position->hwndInsertAfter = HWND_BOTTOM;
                position->flags &= ~SWP_NOZORDER;
            }
            break;
        case WM_GETMINMAXINFO:
            // With a title bar the window is a little larger than the monitor it fills; Windows would clamp it otherwise.
            reinterpret_cast<MINMAXINFO*>(lParam)->ptMaxTrackSize = POINT{32767, 32767};
            return 0;
        case WM_MOUSEACTIVATE:
            if (g_monitor != 0) {
                return MA_NOACTIVATE;
            }
            break;
        case WM_DISPLAYCHANGE:
            if (g_monitor != 0) {
                ApplyPlacement();
            }
            return 0;
        case WM_COPYDATA: {
            // A second start of this program: take over its arguments.
            const auto* data = reinterpret_cast<const COPYDATASTRUCT*>(lParam);
            if (data && data->dwData == ArgumentsMessage && data->lpData && data->cbData >= sizeof(wchar_t)) {
                std::wstring commandLine(static_cast<const wchar_t*>(data->lpData), data->cbData / sizeof(wchar_t));
                commandLine.resize(wcsnlen(commandLine.c_str(), commandLine.size()));
                ParseArguments(commandLine.c_str());
                ApplyArguments();
                return TRUE;
            }
            return FALSE;
        }
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    bool CreateGraphics() {
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels,
                                     _countof(levels), D3D11_SDK_VERSION, g_device.GetAddressOf(), nullptr,
                                     g_context.GetAddressOf()))) {
            return false;
        }
        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        if (FAILED(g_device.As(&dxgiDevice)) || FAILED(dxgiDevice->GetAdapter(adapter.GetAddressOf())) ||
            FAILED(adapter->GetParent(IID_PPV_ARGS(factory.GetAddressOf())))) {
            return false;
        }

        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        if (FAILED(factory->CreateSwapChainForHwnd(g_device.Get(), g_window, &desc, nullptr, nullptr, g_swapChain.GetAddressOf()))) {
            return false;
        }
        factory->MakeWindowAssociation(g_window, DXGI_MWA_NO_ALT_ENTER);

        ComPtr<ID3DBlob> vertexCode, pixelCode, errors;
        if (FAILED(D3DCompile(ShaderSource, sizeof(ShaderSource) - 1, nullptr, nullptr, nullptr, "vs_main", "vs_5_0",
                              D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, vertexCode.GetAddressOf(), errors.GetAddressOf())) ||
            FAILED(D3DCompile(ShaderSource, sizeof(ShaderSource) - 1, nullptr, nullptr, nullptr, "ps_main", "ps_5_0",
                              D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, pixelCode.GetAddressOf(), errors.ReleaseAndGetAddressOf()))) {
            return false;
        }
        if (FAILED(g_device->CreateVertexShader(vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(), nullptr, g_vertexShader.GetAddressOf())) ||
            FAILED(g_device->CreatePixelShader(pixelCode->GetBufferPointer(), pixelCode->GetBufferSize(), nullptr, g_pixelShader.GetAddressOf()))) {
            return false;
        }

        CD3D11_SAMPLER_DESC samplerDesc(D3D11_DEFAULT);
        samplerDesc.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        samplerDesc.AddressU = samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        CD3D11_BUFFER_DESC constantsDesc(sizeof(Constants), D3D11_BIND_CONSTANT_BUFFER, D3D11_USAGE_DYNAMIC, D3D11_CPU_ACCESS_WRITE);
        return SUCCEEDED(g_device->CreateSamplerState(&samplerDesc, g_sampler.GetAddressOf())) &&
               SUCCEEDED(g_device->CreateBuffer(&constantsDesc, nullptr, g_constants.GetAddressOf()));
    }

    void ResizeIfNeeded() {
        if (!g_resized) {
            return;
        }
        g_resized = false;
        RECT client{};
        GetClientRect(g_window, &client);
        const UINT width = std::max<LONG>(client.right - client.left, 1), height = std::max<LONG>(client.bottom - client.top, 1);
        if (width == g_width && height == g_height && g_target) {
            return;
        }
        g_context->OMSetRenderTargets(0, nullptr, nullptr);
        g_target.Reset();
        if (FAILED(g_swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0))) {
            return;
        }
        ComPtr<ID3D11Texture2D> backBuffer;
        if (SUCCEEDED(g_swapChain->GetBuffer(0, IID_PPV_ARGS(backBuffer.GetAddressOf())))) {
            g_device->CreateRenderTargetView(backBuffer.Get(), nullptr, g_target.GetAddressOf());
        }
        g_width = width;
        g_height = height;
    }

    // The layer re-creates its shared textures when the crop box changes size (new handle): follow it.
    bool OpenSourceIfChanged() {
        // A layer that knows this window publishes the real textures in the window's own block (the plugin's block may
        // hold a blank picture meanwhile); an older layer only fills the plugin's block.
        const bool layerKnowsWindow = g_own && g_own->magic == WindowBlockMagic && g_own->sharedHandle[0] != 0;
        const uint64_t handle = layerKnowsWindow ? g_own->sharedHandle[0] : g_surface ? g_surface->sharedHandle[0] : 0;
        if (handle == g_sourceHandle) {
            return g_sourceView != nullptr;
        }
        g_sourceHandle = handle;
        g_source.Reset();
        g_sourceView.Reset();
        if (handle == 0) {
            return false;
        }
        if (FAILED(g_device->OpenSharedResource(reinterpret_cast<HANDLE>(static_cast<uintptr_t>(handle)), IID_PPV_ARGS(g_source.GetAddressOf())))) {
            // A stale handle from a game that has exited, or a texture on another graphics adapter.
            return false;
        }
        g_source->GetDesc(&g_sourceDesc);
        return SUCCEEDED(g_device->CreateShaderResourceView(g_source.Get(), nullptr, g_sourceView.GetAddressOf()));
    }

    void Render(bool havePicture) {
        ResizeIfNeeded();
        if (!g_target) {
            return;
        }
        const float background[4] = {0.f, 0.f, 0.f, 1.f};
        g_context->OMSetRenderTargets(1, g_target.GetAddressOf(), nullptr);
        g_context->ClearRenderTargetView(g_target.Get(), background);

        if (havePicture) {
            // As large as fits, centred; black around it when the shapes differ.
            const float sourceAspect = static_cast<float>(g_sourceDesc.Width) / static_cast<float>(g_sourceDesc.Height);
            float width = static_cast<float>(g_width), height = width / sourceAspect;
            if (height > static_cast<float>(g_height)) {
                height = static_cast<float>(g_height);
                width = height * sourceAspect;
            }
            const D3D11_VIEWPORT viewport = {(g_width - width) * 0.5f, (g_height - height) * 0.5f, width, height, 0.f, 1.f};
            g_context->RSSetViewports(1, &viewport);

            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (SUCCEEDED(g_context->Map(g_constants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
                auto* constants = static_cast<Constants*>(mapped.pData);
                // One sample per source pixel covered is plenty: 1 when enlarging, up to 4x4 when shrinking a lot.
                const float shrink = static_cast<float>(g_sourceDesc.Width) / std::max(width, 1.f);
                constants->taps = std::clamp(std::ceil(shrink), 1.f, 4.f);
                // 8-bit mirror images hold display-ready (sRGB-encoded) values; deeper formats hold linear light.
                constants->toSrgb = (g_sourceDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT ||
                                     g_sourceDesc.Format == DXGI_FORMAT_R10G10B10A2_UNORM ||
                                     g_sourceDesc.Format == DXGI_FORMAT_R32G32B32A32_FLOAT) ? 1.f : 0.f;
                g_context->Unmap(g_constants.Get(), 0);
            }
            g_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            g_context->IASetInputLayout(nullptr);
            g_context->VSSetShader(g_vertexShader.Get(), nullptr, 0);
            g_context->PSSetShader(g_pixelShader.Get(), nullptr, 0);
            g_context->PSSetConstantBuffers(0, 1, g_constants.GetAddressOf());
            g_context->PSSetSamplers(0, 1, g_sampler.GetAddressOf());
            g_context->PSSetShaderResources(0, 1, g_sourceView.GetAddressOf());
            g_context->Draw(3, 0);
            ID3D11ShaderResourceView* const none = nullptr;
            g_context->PSSetShaderResources(0, 1, &none);
        }
        // No vertical-sync wait: frames are paced by the game (one per headset frame), and a window that is fully covered
        // would not be throttled by Present anyway.
        g_swapChain->Present(0, 0);
    }

    void ParseArguments(const wchar_t* commandLine) {
        int count = 0;
        LPWSTR* arguments = CommandLineToArgvW(commandLine, &count);
        for (int i = 1; arguments && i < count; i++) {
            const std::wstring argument = arguments[i];
            const std::wstring next = i + 1 < count ? arguments[i + 1] : L"";
            if (argument == L"--eye") {
                g_eye = next == L"left" ? 0 : next == L"right" ? 1 : g_eye;
                i++;
            } else if (argument == L"--monitor") {
                g_monitor = std::max(_wtoi(next.c_str()), 0);
                i++;
            } else if (argument == L"--titled") {
                g_titled = next != L"0";
                i++;
            } else if (argument == L"--size") {
                int width = 0, height = 0;
                if (swscanf_s(next.c_str(), L"%dx%d", &width, &height) == 2 && width >= 64 && height >= 64 && width <= 16384 && height <= 16384) {
                    g_sizeWidth = width;
                    g_sizeHeight = height;
                } else {
                    g_sizeWidth = g_sizeHeight = 0; // "fill"
                }
                i++;
            } else if (argument == L"--fps") {
                g_fps = std::clamp(_wtoi(next.c_str()), 5, 1000);
                i++;
            } else if (argument == L"--exclusive") {
                g_exclusive = next != L"0" ? 1 : 0;
                i++;
            } else if (argument == L"--windowed") {
                g_monitor = 0;
            } else if (argument == L"--close") {
                g_closeRequested = true;
            }
        }
        LocalFree(arguments);
    }

    // What the arguments asked for, on the live window (used at start-up and when a second start hands its arguments over).
    void ApplyArguments() {
        if (g_closeRequested) {
            DestroyWindow(g_window);
            return;
        }
        ApplyPlacement(false);
        if (g_surface && g_eye >= 0) {
            g_surface->eyeIndex = static_cast<uint32_t>(g_eye);
        }
        if (g_own) {
            g_own->exclusive = g_exclusive;
        }
    }

    double Milliseconds() {
        static LARGE_INTEGER frequency = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        return static_cast<double>(now.QuadPart) * 1000.0 / static_cast<double>(frequency.QuadPart);
    }

    // The picture-rate cap: is a picture due? Keeps a schedule rather than a minimum gap, so that 90 game frames a
    // second become 60 pictures (two out of three) and not 45 (every other one).
    bool PictureDue() {
        static double nextDue = 0;
        const double now = Milliseconds(), interval = 1000.0 / g_fps;
        if (now < nextDue - 2.0) { // 2 ms: a frame that arrives a hair early still counts
            return false;
        }
        nextDue = nextDue + interval > now ? nextDue + interval : now + interval; // fell behind: start over from now
        return true;
    }

} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    // One mirror window is enough: a second start hands its arguments to the first and leaves.
    HANDLE single = CreateMutexW(nullptr, TRUE, L"QuadViewsGazeMirror.MirrorWindow.Single");
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        for (int attempt = 0; attempt < 50; attempt++) { // The first one may still be starting up.
            if (HWND running = FindWindowW(WindowClass, nullptr)) {
                const wchar_t* commandLine = GetCommandLineW();
                COPYDATASTRUCT data{ArgumentsMessage, static_cast<DWORD>((wcslen(commandLine) + 1) * sizeof(wchar_t)),
                                    const_cast<wchar_t*>(commandLine)};
                SendMessageTimeoutW(running, WM_COPYDATA, 0, reinterpret_cast<LPARAM>(&data), SMTO_ABORTIFHUNG, 2000, nullptr);
                break;
            }
            Sleep(100);
        }
        return 0;
    }
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); // "3840 x 2160" has to mean real pixels
    ParseArguments(GetCommandLineW());
    if (g_closeRequested) {
        return 0; // Asked to close a mirror window that is not running.
    }

    WNDCLASSEXW windowClass{sizeof(windowClass)};
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = instance;
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1));
    windowClass.hIconSm = windowClass.hIcon;
    windowClass.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    windowClass.lpszClassName = WindowClass;
    RegisterClassExW(&windowClass);
    g_window = CreateWindowExW(WS_EX_APPWINDOW, WindowClass, WindowTitle, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                               1280, 720, nullptr, nullptr, instance, nullptr);
    if (!g_window) {
        return 1;
    }
    ApplyPlacement();

    if (!CreateGraphics()) {
        MessageBoxW(g_window, L"Direct3D 11 could not be set up for the mirror window.", WindowTitle, MB_ICONERROR);
        return 1;
    }

    // Created here if the game has not started yet: whoever comes first creates the block, the other opens it.
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(MirrorSurfaceData), SurfaceBlockName);
    if (mapping) {
        g_surface = static_cast<MirrorSurfaceData*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(MirrorSurfaceData)));
    }
    HANDLE ownMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(MirrorWindowBlock), WindowBlockName);
    if (ownMapping) {
        g_own = static_cast<MirrorWindowBlock*>(MapViewOfFile(ownMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(MirrorWindowBlock)));
    }
    if (g_surface && g_eye >= 0) {
        g_surface->eyeIndex = static_cast<uint32_t>(g_eye);
    }
    if (g_own) {
        g_own->exclusive = g_exclusive;
    }

    HANDLE frameEvent = CreateEventW(nullptr, FALSE, FALSE, FrameEventName); // auto-reset; opens it if the layer made it
    bool layerSignals = false; // Until the first signal arrives, assume an older layer and watch the counter.
    timeBeginPeriod(1);

    uint32_t lastIndex = g_surface ? g_surface->lastProcessedIndex : 0;
    ULONGLONG lastFrameAt = 0, lastHeartbeat = 0;
    bool running = true;
    while (running) {
        // With a layer that signals new frames: sleep until the signal (waking a few times a second only to say "somebody
        // is reading"). With an older layer: nap a millisecond between looks at its frame counter. Input wakes it either way.
        const DWORD woke = MsgWaitForMultipleObjects(frameEvent ? 1 : 0, frameEvent ? &frameEvent : nullptr, FALSE,
                                                     layerSignals ? 40 : 1, QS_ALLINPUT);
        if (frameEvent && woke == WAIT_OBJECT_0) {
            layerSignals = true;
        }
        MSG message;
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) {
                running = false;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (!running || !g_surface) {
            continue;
        }
        const ULONGLONG now = GetTickCount64();
        const bool havePicture = OpenSourceIfChanged();
        const uint32_t index = g_surface->lastProcessedIndex;
        // A new frame that comes too soon after the last picture is left alone: the next frame (or, if the game stops
        // right here, the next heartbeat wake-up) shows the newest one.
        if (havePicture && index != lastIndex && PictureDue()) {
            lastIndex = index;
            lastFrameAt = now;
            Render(true);
            // "Somebody is reading" keeps the layer producing frames: in this window's own block for layers that know
            // it, and in the plugin's counter for older ones.
            g_surface->frameNumber++;
            if (g_own) {
                InterlockedIncrement(&g_own->readerHeartbeat);
            }
            lastHeartbeat = now;
            SetStatus(L"");
        } else if (now - lastHeartbeat >= 50) {
            // Nothing new. Keep saying "somebody is reading" (the layer only starts producing once it sees that), and show
            // black rather than a frozen frame once the picture has stopped for a while.
            g_surface->frameNumber++;
            if (g_own) {
                InterlockedIncrement(&g_own->readerHeartbeat);
            }
            lastHeartbeat = now;
            if (now - lastFrameAt > 1000) {
                Render(false);
                SetStatus(havePicture ? L"paused (no new frames)" : L"waiting for the game");
            }
        }
    }

    timeEndPeriod(1);
    if (g_surface) {
        UnmapViewOfFile(g_surface);
    }
    if (mapping) {
        CloseHandle(mapping);
    }
    if (g_own) {
        UnmapViewOfFile(g_own);
    }
    if (ownMapping) {
        CloseHandle(ownMapping);
    }
    if (frameEvent) {
        CloseHandle(frameEvent);
    }
    if (single) {
        CloseHandle(single);
    }
    return 0;
}
