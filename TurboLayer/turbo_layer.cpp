// VR Optimizer OpenXR Turbo Layer
//
// Implements a one-frame asynchronous xrWaitFrame pipeline inspired by the
// Turbo Mode algorithm in OpenXR Toolkit (MIT licence). The loader ABI and
// structure definitions below follow the Khronos OpenXR headers
// (Apache-2.0 OR MIT). See LICENSES in this directory.

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <future>
#include <memory>
#include <mutex>
#include <unordered_map>

#if defined(_WIN32)
#define XRAPI_PTR __stdcall
#define XR_EXPORT extern "C" __declspec(dllexport)
#else
#define XRAPI_PTR
#define XR_EXPORT extern "C" __attribute__((visibility("default")))
#endif

using XrVersion = uint64_t;
using XrTime = int64_t;
using XrDuration = int64_t;
using XrBool32 = uint32_t;
using XrResult = int32_t;
using XrInstance = struct XrInstance_T*;
using XrSession = struct XrSession_T*;
using PFN_xrVoidFunction = void(XRAPI_PTR*)();

constexpr XrResult XR_SUCCESS = 0;
constexpr XrResult XR_ERROR_INITIALIZATION_FAILED = -6;
constexpr XrResult XR_ERROR_FUNCTION_UNSUPPORTED = -7;
constexpr uint32_t XR_TRUE = 1;
constexpr uint32_t XR_MAX_API_LAYER_NAME_SIZE = 256;
constexpr uint32_t XR_API_LAYER_MAX_SETTINGS_PATH_SIZE = 512;
constexpr uint32_t XR_CURRENT_LOADER_API_LAYER_VERSION = 1;

enum XrStructureType : int32_t {
    XR_TYPE_FRAME_WAIT_INFO = 33,
    XR_TYPE_FRAME_STATE = 44,
    XR_TYPE_FRAME_BEGIN_INFO = 46,
};

struct XrInstanceCreateInfo;
struct XrFrameWaitInfo {
    XrStructureType type;
    const void* next;
};
struct XrFrameState {
    XrStructureType type;
    void* next;
    XrTime predictedDisplayTime;
    XrDuration predictedDisplayPeriod;
    XrBool32 shouldRender;
};
struct XrFrameBeginInfo {
    XrStructureType type;
    const void* next;
};
struct XrFrameEndInfo;

using PFN_xrGetInstanceProcAddr = XrResult(XRAPI_PTR*)(XrInstance, const char*, PFN_xrVoidFunction*);
struct XrApiLayerCreateInfo;
using PFN_xrCreateApiLayerInstance = XrResult(XRAPI_PTR*)(const XrInstanceCreateInfo*, const XrApiLayerCreateInfo*, XrInstance*);
using PFN_xrWaitFrame = XrResult(XRAPI_PTR*)(XrSession, const XrFrameWaitInfo*, XrFrameState*);
using PFN_xrBeginFrame = XrResult(XRAPI_PTR*)(XrSession, const XrFrameBeginInfo*);
using PFN_xrEndFrame = XrResult(XRAPI_PTR*)(XrSession, const XrFrameEndInfo*);
using PFN_xrDestroySession = XrResult(XRAPI_PTR*)(XrSession);

enum XrLoaderInterfaceStructs : int32_t {
    XR_LOADER_INTERFACE_STRUCT_UNINTIALIZED = 0,
    XR_LOADER_INTERFACE_STRUCT_LOADER_INFO = 1,
    XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST = 2,
    XR_LOADER_INTERFACE_STRUCT_API_LAYER_CREATE_INFO = 4,
    XR_LOADER_INTERFACE_STRUCT_API_LAYER_NEXT_INFO = 5,
};

struct XrApiLayerNextInfo {
    XrLoaderInterfaceStructs structType;
    uint32_t structVersion;
    size_t structSize;
    char layerName[XR_MAX_API_LAYER_NAME_SIZE];
    PFN_xrGetInstanceProcAddr nextGetInstanceProcAddr;
    PFN_xrCreateApiLayerInstance nextCreateApiLayerInstance;
    XrApiLayerNextInfo* next;
};
struct XrApiLayerCreateInfo {
    XrLoaderInterfaceStructs structType;
    uint32_t structVersion;
    size_t structSize;
    void* loaderInstance;
    char settings_file_location[XR_API_LAYER_MAX_SETTINGS_PATH_SIZE];
    XrApiLayerNextInfo* nextInfo;
};
struct XrNegotiateApiLayerRequest {
    XrLoaderInterfaceStructs structType;
    uint32_t structVersion;
    size_t structSize;
    uint32_t layerInterfaceVersion;
    XrVersion layerApiVersion;
    PFN_xrGetInstanceProcAddr getInstanceProcAddr;
    PFN_xrCreateApiLayerInstance createApiLayerInstance;
};
struct XrNegotiateLoaderInfo {
    XrLoaderInterfaceStructs structType;
    uint32_t structVersion;
    size_t structSize;
    uint32_t minInterfaceVersion;
    uint32_t maxInterfaceVersion;
    XrVersion minApiVersion;
    XrVersion maxApiVersion;
};

struct SessionState {
    std::mutex frameLock;
    std::mutex resultLock;
    std::future<void> asyncWait;
    bool asyncPolled = false;
    bool asyncCompleted = false;
    XrResult asyncResult = XR_SUCCESS;
    XrTime predictedDisplayTime = 0;
    XrDuration predictedDisplayPeriod = 0;
};

static PFN_xrGetInstanceProcAddr g_nextGetInstanceProcAddr = nullptr;
static PFN_xrWaitFrame g_nextWaitFrame = nullptr;
static PFN_xrBeginFrame g_nextBeginFrame = nullptr;
static PFN_xrEndFrame g_nextEndFrame = nullptr;
static PFN_xrDestroySession g_nextDestroySession = nullptr;
static std::mutex g_sessionsLock;
static std::unordered_map<XrSession, std::shared_ptr<SessionState>> g_sessions;

static std::shared_ptr<SessionState> getSession(XrSession session) {
    std::lock_guard lock(g_sessionsLock);
    auto& state = g_sessions[session];
    if (!state) state = std::make_shared<SessionState>();
    return state;
}

static std::shared_ptr<SessionState> removeSession(XrSession session) {
    std::lock_guard lock(g_sessionsLock);
    const auto found = g_sessions.find(session);
    if (found == g_sessions.end()) return {};
    auto state = found->second;
    g_sessions.erase(found);
    return state;
}

static XrResult XRAPI_PTR turbo_xrWaitFrame(XrSession session, const XrFrameWaitInfo* info, XrFrameState* frameState);
static XrResult XRAPI_PTR turbo_xrBeginFrame(XrSession session, const XrFrameBeginInfo* info);
static XrResult XRAPI_PTR turbo_xrEndFrame(XrSession session, const XrFrameEndInfo* info);
static XrResult XRAPI_PTR turbo_xrDestroySession(XrSession session);
static XrResult XRAPI_PTR turbo_xrGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function);
static XrResult XRAPI_PTR turbo_xrCreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo* layerInfo, XrInstance* instance);

static XrResult XRAPI_PTR turbo_xrWaitFrame(XrSession session, const XrFrameWaitInfo* info, XrFrameState* frameState) {
    if (!frameState || !g_nextWaitFrame) return XR_ERROR_INITIALIZATION_FAILED;
    const auto state = getSession(session);
    std::unique_lock frameGuard(state->frameLock);

    if (state->asyncWait.valid()) {
        if (state->asyncPolled) state->asyncWait.wait();
        state->asyncPolled = true;

        std::lock_guard resultGuard(state->resultLock);
        if (state->asyncCompleted && state->asyncResult != XR_SUCCESS) return state->asyncResult;
        if (!state->asyncCompleted && state->predictedDisplayPeriod > 0)
            state->predictedDisplayTime += state->predictedDisplayPeriod;
        frameState->predictedDisplayTime = state->predictedDisplayTime;
        frameState->predictedDisplayPeriod = state->predictedDisplayPeriod;
        frameState->shouldRender = XR_TRUE;
        return XR_SUCCESS;
    }

    frameGuard.unlock();
    const XrResult result = g_nextWaitFrame(session, info, frameState);
    if (result == XR_SUCCESS) {
        std::lock_guard resultGuard(state->resultLock);
        state->predictedDisplayTime = frameState->predictedDisplayTime;
        state->predictedDisplayPeriod = frameState->predictedDisplayPeriod;
    }
    return result;
}

static XrResult XRAPI_PTR turbo_xrBeginFrame(XrSession session, const XrFrameBeginInfo* info) {
    if (!g_nextBeginFrame) return XR_ERROR_INITIALIZATION_FAILED;
    const auto state = getSession(session);
    std::lock_guard frameGuard(state->frameLock);
    return state->asyncWait.valid() ? XR_SUCCESS : g_nextBeginFrame(session, info);
}

static XrResult XRAPI_PTR turbo_xrEndFrame(XrSession session, const XrFrameEndInfo* info) {
    if (!g_nextWaitFrame || !g_nextBeginFrame || !g_nextEndFrame) return XR_ERROR_INITIALIZATION_FAILED;
    const auto state = getSession(session);
    std::unique_lock frameGuard(state->frameLock);

    if (state->asyncWait.valid()) {
        state->asyncWait.wait();
        state->asyncWait.get();
        {
            std::lock_guard resultGuard(state->resultLock);
            if (state->asyncResult != XR_SUCCESS) return state->asyncResult;
        }
        const XrFrameBeginInfo beginInfo{XR_TYPE_FRAME_BEGIN_INFO, nullptr};
        const XrResult beginResult = g_nextBeginFrame(session, &beginInfo);
        if (beginResult != XR_SUCCESS) return beginResult;
    }

    const XrResult result = g_nextEndFrame(session, info);
    if (result != XR_SUCCESS) return result;

    state->asyncPolled = false;
    state->asyncCompleted = false;
    state->asyncResult = XR_SUCCESS;
    state->asyncWait = std::async(std::launch::async, [state, session] {
        const XrFrameWaitInfo waitInfo{XR_TYPE_FRAME_WAIT_INFO, nullptr};
        XrFrameState nextFrame{XR_TYPE_FRAME_STATE, nullptr, 0, 0, 0};
        const XrResult waitResult = g_nextWaitFrame(session, &waitInfo, &nextFrame);
        std::lock_guard resultGuard(state->resultLock);
        state->asyncResult = waitResult;
        if (waitResult == XR_SUCCESS) {
            state->predictedDisplayTime = nextFrame.predictedDisplayTime;
            state->predictedDisplayPeriod = nextFrame.predictedDisplayPeriod;
        }
        state->asyncCompleted = true;
    });
    return result;
}

static XrResult XRAPI_PTR turbo_xrDestroySession(XrSession session) {
    const auto state = removeSession(session);
    if (state) {
        std::lock_guard frameGuard(state->frameLock);
        if (state->asyncWait.valid()) state->asyncWait.wait_for(std::chrono::seconds(5));
    }
    return g_nextDestroySession ? g_nextDestroySession(session) : XR_ERROR_INITIALIZATION_FAILED;
}

static XrResult XRAPI_PTR turbo_xrGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function) {
    if (!name || !function) return XR_ERROR_INITIALIZATION_FAILED;
    if (std::strcmp(name, "xrGetInstanceProcAddr") == 0)
        *function = reinterpret_cast<PFN_xrVoidFunction>(turbo_xrGetInstanceProcAddr);
    else if (std::strcmp(name, "xrWaitFrame") == 0)
        *function = reinterpret_cast<PFN_xrVoidFunction>(turbo_xrWaitFrame);
    else if (std::strcmp(name, "xrBeginFrame") == 0)
        *function = reinterpret_cast<PFN_xrVoidFunction>(turbo_xrBeginFrame);
    else if (std::strcmp(name, "xrEndFrame") == 0)
        *function = reinterpret_cast<PFN_xrVoidFunction>(turbo_xrEndFrame);
    else if (std::strcmp(name, "xrDestroySession") == 0)
        *function = reinterpret_cast<PFN_xrVoidFunction>(turbo_xrDestroySession);
    else if (g_nextGetInstanceProcAddr)
        return g_nextGetInstanceProcAddr(instance, name, function);
    else
        return XR_ERROR_FUNCTION_UNSUPPORTED;
    return XR_SUCCESS;
}

static XrResult XRAPI_PTR turbo_xrCreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo* layerInfo, XrInstance* instance) {
    if (!layerInfo || !layerInfo->nextInfo || !instance) return XR_ERROR_INITIALIZATION_FAILED;
    const auto* nextInfo = layerInfo->nextInfo;
    g_nextGetInstanceProcAddr = nextInfo->nextGetInstanceProcAddr;
    if (!g_nextGetInstanceProcAddr || !nextInfo->nextCreateApiLayerInstance) return XR_ERROR_INITIALIZATION_FAILED;

    XrApiLayerCreateInfo nextLayerInfo = *layerInfo;
    nextLayerInfo.nextInfo = nextInfo->next;
    const XrResult result = nextInfo->nextCreateApiLayerInstance(info, &nextLayerInfo, instance);
    if (result != XR_SUCCESS) return result;

    PFN_xrVoidFunction function = nullptr;
    if (g_nextGetInstanceProcAddr(*instance, "xrWaitFrame", &function) != XR_SUCCESS) return XR_ERROR_INITIALIZATION_FAILED;
    g_nextWaitFrame = reinterpret_cast<PFN_xrWaitFrame>(function);
    if (g_nextGetInstanceProcAddr(*instance, "xrBeginFrame", &function) != XR_SUCCESS) return XR_ERROR_INITIALIZATION_FAILED;
    g_nextBeginFrame = reinterpret_cast<PFN_xrBeginFrame>(function);
    if (g_nextGetInstanceProcAddr(*instance, "xrEndFrame", &function) != XR_SUCCESS) return XR_ERROR_INITIALIZATION_FAILED;
    g_nextEndFrame = reinterpret_cast<PFN_xrEndFrame>(function);
    if (g_nextGetInstanceProcAddr(*instance, "xrDestroySession", &function) != XR_SUCCESS) return XR_ERROR_INITIALIZATION_FAILED;
    g_nextDestroySession = reinterpret_cast<PFN_xrDestroySession>(function);
    return XR_SUCCESS;
}

XR_EXPORT XrResult XRAPI_PTR xrNegotiateLoaderApiLayerInterface(
    const XrNegotiateLoaderInfo* loaderInfo,
    const char*,
    XrNegotiateApiLayerRequest* request) {
    if (!loaderInfo || !request ||
        loaderInfo->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION)
        return XR_ERROR_INITIALIZATION_FAILED;

    request->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    request->layerApiVersion = loaderInfo->maxApiVersion;
    request->getInstanceProcAddr = turbo_xrGetInstanceProcAddr;
    request->createApiLayerInstance = turbo_xrCreateApiLayerInstance;
    return XR_SUCCESS;
}
