#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "ApoInterfaces.h"
#include <new>
#include <cmath>
#include <cstring>
#include <cstdio>
#include "BridgeProtocol.h"

// These identifiers belong exclusively to Yuki's Pad.
static const GUID CLSID_YukiMicFx = {0x707a2e34,0x33cf,0x493d,{0xb7,0x53,0x72,0x71,0xe5,0x70,0x91,0xab}};
static const PROPERTYKEY BridgePathKey = {{0xe8a3270d,0xc8aa,0x4994,{0x89,0x34,0x41,0xd1,0xe2,0x22,0x98,0x35}},1};
static const PROPERTYKEY OriginalApoKey = {{0xe8a3270d,0xc8aa,0x4994,{0x89,0x34,0x41,0xd1,0xe2,0x22,0x98,0x35}},2};
static volatile LONG objects = 0;
static void Trace(const char* stage,long code=0) {
#ifdef YUKI_APO_DIAGNOSTICS
    wchar_t path[MAX_PATH]; DWORD n=GetEnvironmentVariableW(L"ProgramData",path,MAX_PATH);
    if(!n)wcscpy_s(path,L"C:\\ProgramData");
    if(n>MAX_PATH-40)return;wcscat_s(path,L"\\YukisPad\\apo-diagnostics.log");
    HANDLE file=CreateFileW(path,FILE_APPEND_DATA,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
    if(file==INVALID_HANDLE_VALUE)return;
    char line[240];int length=snprintf(line,sizeof(line),"PID %lu %s 0x%08lx\r\n",GetCurrentProcessId(),stage,(unsigned long)code);DWORD written;
    WriteFile(file,line,(DWORD)length,&written,nullptr);CloseHandle(file);
#else
    (void)stage;(void)code;
#endif
}

template<class T> static void ReleasePtr(T*& p) { if(p) { p->Release(); p = nullptr; } }
static bool IsFloat(IAudioMediaType* type, UNCOMPRESSEDAUDIOFORMAT* f) {
    if(!type || FAILED(type->GetUncompressedAudioFormat(f))) return false;
    return f->guidFormatType == YukiFloatFormat && f->dwBytesPerSampleContainer == 4 &&
        f->dwValidBitsPerSample == 32 && f->dwSamplesPerFrame > 0 && f->dwSamplesPerFrame <= 32 &&
        f->fFramesPerSecond >= 8000 && f->fFramesPerSecond <= 192000;
}

class MicFx final : public IAudioProcessingObject, public IAudioProcessingObjectRT,
    public IAudioProcessingObjectConfiguration, public IAudioSystemEffects2 {
    volatile LONG refs = 1;
    IUnknown* outer;
    class InnerUnknown final : public IUnknown {
        MicFx* owner;
    public:
        explicit InnerUnknown(MicFx* value):owner(value){}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {return owner->InnerQueryInterface(iid,out);}
        ULONG STDMETHODCALLTYPE AddRef() override {return owner->InnerAddRef();}
        ULONG STDMETHODCALLTYPE Release() override {return owner->InnerRelease();}
    } inner{this};
    bool initialized = false, locked = false, counted = false;
    UINT32 channels = 1, maxFrames = 0;
    double deviceRate = 48000, cursor = 0;
    LONG generation = -1;
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    yuki::Shared* shared = nullptr;
    IAudioProcessingObject* child = nullptr;
    IAudioProcessingObjectRT* childRt = nullptr;
    IAudioProcessingObjectConfiguration* childConfig = nullptr;
    IAudioSystemEffects2* childEffects = nullptr;

    void CloseBridge() {
        if(shared) {
            if(counted) InterlockedDecrement(&shared->header.activeInstances);
            VirtualUnlock(shared,sizeof(yuki::Shared)); UnmapViewOfFile(shared); shared=nullptr;
        }
        counted = false;
        if(mapping) { CloseHandle(mapping); mapping=nullptr; }
        if(file!=INVALID_HANDLE_VALUE) { CloseHandle(file); file=INVALID_HANDLE_VALUE; }
    }
    void OpenBridge(const wchar_t* path) {
        file=CreateFileW(path,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(file==INVALID_HANDLE_VALUE) { Trace("OpenBridge failed",GetLastError()); return; }
        LARGE_INTEGER size;
        if(!GetFileSizeEx(file,&size) || size.QuadPart!=sizeof(yuki::Shared)) { CloseBridge(); return; }
        mapping=CreateFileMappingW(file,nullptr,PAGE_READWRITE,0,0,nullptr);
        if(mapping) shared=(yuki::Shared*)MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(yuki::Shared));
        if(!shared) { Trace("Map bridge failed",GetLastError()); CloseBridge(); return; }
        if(shared->header.magic!=yuki::Magic || shared->header.version!=yuki::Version || shared->header.capacity!=yuki::Capacity || shared->header.sampleRate!=yuki::Rate) { CloseBridge(); return; }
        // Pin the small transport buffer before entering the real-time callback.
        VirtualLock(shared,sizeof(yuki::Shared));
        Trace("Bridge mapped");
    }
    HRESULT Format(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) {
        if(!supported) return E_POINTER; *supported=nullptr;
        UNCOMPRESSEDAUDIOFORMAT a{},b{};
        if(!IsFloat(requested,&a)) return APOERR_FORMAT_NOT_SUPPORTED;
        if(opposite && (!IsFloat(opposite,&b) || a.dwSamplesPerFrame!=b.dwSamplesPerFrame || a.fFramesPerSecond!=b.fFramesPerSecond)) {
            if(IsFloat(opposite,&b)) { *supported=opposite; opposite->AddRef(); return S_FALSE; }
            return APOERR_FORMAT_NOT_SUPPORTED;
        }
        *supported=requested; requested->AddRef(); return S_OK;
    }
public:
    explicit MicFx(IUnknown* controlling=nullptr):outer(controlling) { InterlockedIncrement(&objects); Trace("Created"); }
    ~MicFx() {
        if(locked && childConfig) childConfig->UnlockForProcess();
        CloseBridge(); ReleasePtr(childEffects); ReleasePtr(childConfig); ReleasePtr(childRt); ReleasePtr(child);
        InterlockedDecrement(&objects);
    }
    HRESULT InnerQueryInterface(REFIID iid,void** out) {
        if(!out) return E_POINTER; *out=nullptr;
        if(iid==IID_IUnknown) { *out=static_cast<IUnknown*>(&inner); InnerAddRef();return S_OK; }
        if(iid==__uuidof(IAudioProcessingObject)) *out=static_cast<IAudioProcessingObject*>(this);
        else if(iid==__uuidof(IAudioProcessingObjectRT)) *out=static_cast<IAudioProcessingObjectRT*>(this);
        else if(iid==__uuidof(IAudioProcessingObjectConfiguration)) *out=static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if(iid==__uuidof(IAudioSystemEffects) || iid==__uuidof(IAudioSystemEffects2)) *out=static_cast<IAudioSystemEffects2*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG InnerAddRef() { return InterlockedIncrement(&refs); }
    ULONG InnerRelease() { LONG n=InterlockedDecrement(&refs); if(!n) delete this; return n; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {return outer?outer->QueryInterface(iid,out):InnerQueryInterface(iid,out);}
    ULONG STDMETHODCALLTYPE AddRef() override {return outer?outer->AddRef():InnerAddRef();}
    ULONG STDMETHODCALLTYPE Release() override {return outer?outer->Release():InnerRelease();}
    HRESULT STDMETHODCALLTYPE Reset() override { generation=-1; cursor=0; return child?child->Reset():S_OK; }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* time) override { if(!time) return E_POINTER; *time=0; return child?child->GetLatency(time):S_OK; }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** out) override {
        if(!out) return E_POINTER; *out=nullptr;
        auto p=(APO_REG_PROPERTIES*)CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)); if(!p) return E_OUTOFMEMORY;
        memset(p,0,sizeof(*p)); p->clsid=CLSID_YukiMicFx; p->Flags=(APO_FLAG)(APO_FLAG_DEFAULT|APO_FLAG_INPLACE);
        wcscpy_s(p->szFriendlyName,L"Yuki's Pad Microphone Mixer"); wcscpy_s(p->szCopyrightInfo,L"Yuki's Pad");
        p->u32MajorVersion=1; p->u32MinInputConnections=p->u32MaxInputConnections=p->u32MinOutputConnections=p->u32MaxOutputConnections=1;
        p->u32MaxInstances=0xffffffff; p->u32NumAPOInterfaces=1; p->iidAPOInterfaceList[0]=__uuidof(IAudioProcessingObject);
        *out=p; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size,BYTE* bytes) override {
        Trace("Initialize",size);
        if(initialized) return APOERR_ALREADY_INITIALIZED;
        if(!bytes || size<sizeof(APOInitSystemEffects) || size>4096) return E_INVALIDARG;
        auto init=(APOInitSystemEffects*)bytes;
        if(!init->pAPOSystemEffectsProperties) return E_INVALIDARG;
        if(size>=sizeof(APOInitSystemEffects2) && ((APOInitSystemEffects2*)bytes)->InitializeForDiscoveryOnly) { initialized=true; return S_OK; }
        PROPVARIANT value; PropVariantInit(&value);
        HRESULT pathResult=init->pAPOSystemEffectsProperties->GetValue(BridgePathKey,&value);Trace("Bridge property",pathResult);Trace("Bridge value type",value.vt);
        if(SUCCEEDED(pathResult) && value.vt==VT_LPWSTR) OpenBridge(value.pwszVal);
        PropVariantClear(&value);
        // Preserve the vendor effect by wrapping its COM object, not by discarding it.
        if(SUCCEEDED(init->pAPOSystemEffectsProperties->GetValue(OriginalApoKey,&value)) && value.vt==VT_LPWSTR && value.pwszVal[0]) {
            GUID clsid;
            HRESULT hr=CLSIDFromString(value.pwszVal,&clsid);
            if(SUCCEEDED(hr) && clsid!=CLSID_YukiMicFx && clsid!=GUID_NULL) {
                hr=CoCreateInstance(clsid,nullptr,CLSCTX_INPROC_SERVER,__uuidof(IAudioProcessingObject),(void**)&child);
                if(SUCCEEDED(hr)) hr=child->QueryInterface(__uuidof(IAudioProcessingObjectRT),(void**)&childRt);
                if(SUCCEEDED(hr)) hr=child->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration),(void**)&childConfig);
                if(SUCCEEDED(hr)) {
                    child->QueryInterface(__uuidof(IAudioSystemEffects2),(void**)&childEffects);
                    BYTE copy[4096]; memcpy(copy,bytes,size); ((APOInitBaseStruct*)copy)->clsid=clsid;
                    hr=child->Initialize(size,copy);
                }
                if(FAILED(hr)) { if(shared) InterlockedExchange(&shared->header.lastError,hr); PropVariantClear(&value); return hr; }
            }
        }
        PropVariantClear(&value); initialized=true; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite,IAudioMediaType* requested,IAudioMediaType** supported) override {
        if(child) { HRESULT hr=child->IsInputFormatSupported(opposite,requested,supported); if(hr!=S_OK) return hr; if(supported && *supported) { (*supported)->Release(); *supported=nullptr; } }
        return Format(opposite,requested,supported);
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite,IAudioMediaType* requested,IAudioMediaType** supported) override {
        if(child) { HRESULT hr=child->IsOutputFormatSupported(opposite,requested,supported); if(hr!=S_OK) return hr; if(supported && *supported) { (*supported)->Release(); *supported=nullptr; } }
        return Format(opposite,requested,supported);
    }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* count) override { if(!count) return E_POINTER; *count=channels; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetEffectsList(GUID** ids,UINT* count,HANDLE event) override {
        if(!ids||!count) return E_POINTER; *ids=nullptr; *count=0;
        GUID* vendor=nullptr;UINT vendorCount=0;
        if(childEffects) { HRESULT hr=childEffects->GetEffectsList(&vendor,&vendorCount,event);if(FAILED(hr))return hr; }
        if(vendorCount>1024) { CoTaskMemFree(vendor);return E_UNEXPECTED; }
        auto list=(GUID*)CoTaskMemAlloc((vendorCount+1)*sizeof(GUID));
        if(!list){CoTaskMemFree(vendor);return E_OUTOFMEMORY;}
        if(vendorCount&&vendor)memcpy(list,vendor,vendorCount*sizeof(GUID));
        CoTaskMemFree(vendor);
        // Injecting a soundboard into capture is distinct from the standard DSP effects.
        list[vendorCount]=CLSID_YukiMicFx;*ids=list;*count=vendorCount+1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 ni,APO_CONNECTION_DESCRIPTOR** in,UINT32 no,APO_CONNECTION_DESCRIPTOR** out) override {
        Trace("LockForProcess");
        if(locked) return APOERR_APO_LOCKED;
        if(ni!=1 || no!=1 || !in || !out || !in[0] || !out[0]) return E_INVALIDARG;
        UNCOMPRESSEDAUDIOFORMAT a{},b{};
        if(!IsFloat(in[0]->pFormat,&a) || !IsFloat(out[0]->pFormat,&b) || a.dwSamplesPerFrame!=b.dwSamplesPerFrame || a.fFramesPerSecond!=b.fFramesPerSecond) return APOERR_FORMAT_NOT_SUPPORTED;
        if(childConfig) { HRESULT hr=childConfig->LockForProcess(ni,in,no,out); if(FAILED(hr)) return hr; }
        channels=a.dwSamplesPerFrame; deviceRate=a.fFramesPerSecond; maxFrames=in[0]->u32MaxFrameCount<out[0]->u32MaxFrameCount?in[0]->u32MaxFrameCount:out[0]->u32MaxFrameCount;
        if(shared) { InterlockedIncrement(&shared->header.activeInstances); counted=true; InterlockedExchange(&shared->header.channels,channels); InterlockedExchange(&shared->header.deviceRate,(LONG)deviceRate); }
        locked=true; generation=-1; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if(!locked) return APOERR_ALREADY_UNLOCKED;
        locked=false;
        if(shared && counted) { InterlockedDecrement(&shared->header.activeInstances); counted=false; }
        return childConfig?childConfig->UnlockForProcess():S_OK;
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 n) override { return n; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 n) override { return n; }
    void STDMETHODCALLTYPE APOProcess(UINT32 ni,APO_CONNECTION_PROPERTY** in,UINT32 no,APO_CONNECTION_PROPERTY** out) override {
        if(ni!=1||no!=1||!in||!out||!in[0]||!out[0]) return;
        auto src=in[0]; auto dst=out[0];
        if(!locked || src->u32BufferFlags==BUFFER_INVALID || src->u32ValidFrameCount>maxFrames) { dst->u32ValidFrameCount=0; dst->u32BufferFlags=BUFFER_INVALID; return; }
        if(childRt) childRt->APOProcess(ni,in,no,out);
        else {
            dst->u32ValidFrameCount=src->u32ValidFrameCount; dst->u32BufferFlags=src->u32BufferFlags;
            if(src->u32BufferFlags==BUFFER_VALID && dst->pBuffer!=src->pBuffer) memcpy((void*)dst->pBuffer,(const void*)src->pBuffer,size_t(src->u32ValidFrameCount)*channels*sizeof(float));
        }
        if(!shared || dst->u32BufferFlags==BUFFER_INVALID || dst->u32ValidFrameCount>maxFrames) return;
        auto& h=shared->header;
        LONG64 now=(LONG64)GetTickCount64(); InterlockedExchange64(&h.lastProcessMs,now); InterlockedIncrement64(&h.processCalls);
        if(yuki::Read32(&h.active)!=1 || now-yuki::Read64(&h.heartbeatMs)>1000 || now<yuki::Read64(&h.heartbeatMs)) { generation=-1; return; }
        LONG gen=yuki::Read32(&h.generation); LONG64 written=yuki::Read64(&h.publishedFrames), frequency=yuki::Read64(&h.qpcFrequency), start=yuki::Read64(&h.startQpc);
        if(written<0 || written>(1LL<<50) || frequency<=0) return;
        LARGE_INTEGER qpc; QueryPerformanceCounter(&qpc);
        double expected=(qpc.QuadPart-start)*(double)yuki::Rate/frequency;
        if(expected<0) return;
        if(generation!=gen || fabs(cursor-expected)>yuki::Rate*.2) { cursor=expected; generation=gen; }
        float gain=h.gain; if(!std::isfinite(gain)) gain=0; if(gain<0) gain=0; if(gain>1) gain=1;
        float* audio=(float*)dst->pBuffer; if(!audio) return;
        bool silent=dst->u32BufferFlags==BUFFER_SILENT, mixed=false;
        // No allocations, locks, file access or waiting are performed in this callback.
        if(silent) memset(audio,0,size_t(dst->u32ValidFrameCount)*channels*sizeof(float));
        LONG64 injected=0;
        for(UINT32 i=0;i<dst->u32ValidFrameCount;i++,cursor+=yuki::Rate/deviceRate) {
            LONG64 frame=(LONG64)cursor;
            if(frame<0 || frame+1>=written || frame<written-yuki::Capacity) continue;
            double fraction=cursor-frame;
            size_t a=(size_t)(frame%yuki::Capacity)*2,b=(size_t)((frame+1)%yuki::Capacity)*2;
            float left=(float)(shared->samples[a]+(shared->samples[b]-shared->samples[a])*fraction)*gain;
            float right=(float)(shared->samples[a+1]+(shared->samples[b+1]-shared->samples[a+1])*fraction)*gain;
            if(!std::isfinite(left)||!std::isfinite(right)) continue;
            for(UINT32 c=0;c<channels;c++) {
                float sound=channels==1?(left+right)*.5f:(c%2==0?left:right);
                float result=audio[i*channels+c]+sound;
                audio[i*channels+c]=result>1?1:result< -1?-1:result;
            }
            mixed=true; injected++;
        }
        if(mixed) { dst->u32BufferFlags=BUFFER_VALID; InterlockedExchangeAdd64(&h.injectedFrames,injected); }
    }
};

class Factory final : public IClassFactory {
    volatile LONG refs=1;
public:
    Factory(){InterlockedIncrement(&objects);} ~Factory(){InterlockedDecrement(&objects);}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override { if(!out)return E_POINTER; *out=nullptr; if(iid!=IID_IUnknown && iid!=IID_IClassFactory)return E_NOINTERFACE; *out=static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override{return InterlockedIncrement(&refs);}
    ULONG STDMETHODCALLTYPE Release() override{LONG n=InterlockedDecrement(&refs);if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** out) override { Trace("CreateInstance",outer?1:0);if(!out)return E_POINTER; *out=nullptr; if(outer&&iid!=IID_IUnknown)return E_NOINTERFACE; auto obj=new(std::nothrow) MicFx(outer); if(!obj)return E_OUTOFMEMORY; HRESULT hr=obj->InnerQueryInterface(iid,out);Trace("Requested interface",hr); obj->InnerRelease(); return hr; }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if(lock)InterlockedIncrement(&objects);else InterlockedDecrement(&objects);return S_OK; }
};
extern "C" __declspec(dllexport) HRESULT WINAPI DllGetClassObject(REFCLSID clsid,REFIID iid,void** out) {
    Trace("DllGetClassObject");
    if(!out)return E_POINTER;*out=nullptr;if(clsid!=CLSID_YukiMicFx)return CLASS_E_CLASSNOTAVAILABLE;
    auto factory=new(std::nothrow) Factory;if(!factory)return E_OUTOFMEMORY;HRESULT hr=factory->QueryInterface(iid,out);factory->Release();return hr;
}
extern "C" __declspec(dllexport) HRESULT WINAPI DllCanUnloadNow(){return yuki::Read32(&objects)==0?S_OK:S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH)DisableThreadLibraryCalls(instance);return TRUE;}
