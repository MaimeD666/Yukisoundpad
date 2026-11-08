#include "YukiMicFx.cpp"
#include <propsys.h>
#include <propvarutil.h>
#include <cstdio>
#include <initializer_list>

class Media final : public IAudioMediaType {
    volatile LONG refs=1;
    WAVEFORMATEX wave{};
public:
    Media(UINT32 rate,UINT32 channels) { wave.wFormatTag=WAVE_FORMAT_IEEE_FLOAT; wave.nChannels=(WORD)channels; wave.nSamplesPerSec=rate; wave.wBitsPerSample=32; wave.nBlockAlign=(WORD)(4*channels); wave.nAvgBytesPerSec=rate*wave.nBlockAlign; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override { if(!out)return E_POINTER;*out=nullptr;if(id!=IID_IUnknown&&id!=__uuidof(IAudioMediaType))return E_NOINTERFACE;*out=this;AddRef();return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override{return InterlockedIncrement(&refs);}
    ULONG STDMETHODCALLTYPE Release() override{auto n=InterlockedDecrement(&refs);if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* out) override { if(!out)return E_POINTER;*out=FALSE;return S_OK; }
    HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* other,DWORD* flags) override{if(!other||!flags)return E_POINTER;*flags=memcmp(other->GetAudioFormat(),&wave,sizeof(wave))==0?0xf:0;return *flags?S_OK:S_FALSE;}
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override{return &wave;}
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* out) override { if(!out)return E_POINTER;*out={};out->guidFormatType=YukiFloatFormat;out->dwSamplesPerFrame=wave.nChannels;out->dwBytesPerSampleContainer=4;out->dwValidBitsPerSample=32;out->fFramesPerSecond=(float)wave.nSamplesPerSec;return S_OK; }
};
static int failures=0;
class AggregateHost final : public IUnknown {
public:
    ULONG refs=1;
    IUnknown* inner=nullptr;
    ~AggregateHost(){if(inner)inner->Release();}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {
        if(!out)return E_POINTER;*out=nullptr;
        if(iid==IID_IUnknown){*out=static_cast<IUnknown*>(this);AddRef();return S_OK;}
        return inner?inner->QueryInterface(iid,out):E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{return --refs;}
};
static void Check(bool value,const char* label) { printf("%s %s\n",value?"PASS":"FAIL",label); if(!value)failures++; }
int wmain(int argc,wchar_t** argv) {
    CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    const wchar_t* path=argc>1?argv[1]:L"native-test-bridge.bin";
    HANDLE file=CreateFileW(path,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    LARGE_INTEGER size;size.QuadPart=sizeof(yuki::Shared);SetFilePointerEx(file,size,nullptr,FILE_BEGIN);SetEndOfFile(file);
    HANDLE map=CreateFileMappingW(file,nullptr,PAGE_READWRITE,0,0,nullptr);auto shared=(yuki::Shared*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,sizeof(yuki::Shared));
    if(!shared) { printf("FAIL fixture mapping %lu\n",GetLastError());return 1; }
    memset(shared,0,sizeof(*shared));shared->header.magic=yuki::Magic;shared->header.version=1;shared->header.capacity=yuki::Capacity;shared->header.sampleRate=yuki::Rate;shared->header.gain=1;
    IPropertyStore* properties=nullptr;PSCreateMemoryPropertyStore(IID_IPropertyStore,(void**)&properties);
    PROPVARIANT value;InitPropVariantFromString(path,&value);properties->SetValue(BridgePathKey,value);PropVariantClear(&value);
    for(UINT32 rate:{16000u,44100u,48000u,96000u}) {
        auto fx=new MicFx;APOInitSystemEffects init{};init.APOInit.cbSize=sizeof(init);init.APOInit.clsid=CLSID_YukiMicFx;init.pAPOSystemEffectsProperties=properties;
        Check(SUCCEEDED(fx->Initialize(sizeof(init),(BYTE*)&init)),"APO Initialize");
        auto media=new Media(rate,1);APO_CONNECTION_DESCRIPTOR d{};d.pFormat=media;d.u32MaxFrameCount=480;APO_CONNECTION_DESCRIPTOR* descriptors[]={&d};
        Check(SUCCEEDED(fx->LockForProcess(1,descriptors,1,descriptors)),"Lock float processing");
        float input[480],output[480];for(auto& v:input)v=.125f;memset(output,0,sizeof(output));
        APO_CONNECTION_PROPERTY in{(UINT_PTR)input,480,BUFFER_VALID,APO_CONNECTION_PROPERTY_SIGNATURE},out{(UINT_PTR)output,0,BUFFER_INVALID,APO_CONNECTION_PROPERTY_SIGNATURE};
        APO_CONNECTION_PROPERTY* ins[]={&in};APO_CONNECTION_PROPERTY* outs[]={&out};
        shared->header.active=0;fx->APOProcess(1,ins,1,outs);Check(memcmp(input,output,sizeof(input))==0,"Microphone passes through unchanged");
        for(auto& sample:shared->samples)sample=.25f;
        LARGE_INTEGER qpc,freq;QueryPerformanceCounter(&qpc);QueryPerformanceFrequency(&freq);
        shared->header.startQpc=qpc.QuadPart;shared->header.qpcFrequency=freq.QuadPart;shared->header.publishedFrames=48000;shared->header.heartbeatMs=GetTickCount64();shared->header.generation++;shared->header.active=1;
        fx->APOProcess(1,ins,1,outs);Check(fabs(output[100]-.375f)<.0001f,"Inject and resample without losing microphone");
        in.u32BufferFlags=BUFFER_SILENT;fx->APOProcess(1,ins,1,outs);Check(out.u32BufferFlags==BUFFER_VALID&&fabs(output[100]-.25f)<.0001f,"Sound survives silent microphone flag");
        in.u32BufferFlags=BUFFER_VALID;for(auto& v:input)v=.9f;fx->APOProcess(1,ins,1,outs);Check(output[100]==1.f,"Mixed signal cannot exceed full scale");
        shared->header.heartbeatMs=GetTickCount64()-2000;fx->APOProcess(1,ins,1,outs);Check(memcmp(input,output,sizeof(input))==0,"Crashed/stale writer falls back to microphone");
        shared->header.active=0;fx->APOProcess(1,ins,1,outs);Check(memcmp(input,output,sizeof(input))==0,"Stop injection preserves microphone");
        Check(SUCCEEDED(fx->UnlockForProcess())&&shared->header.activeInstances==0,"Unlock releases active reader");media->Release();fx->Release();
    }
    {
        AggregateHost host;Factory factory;
        HRESULT hr=factory.CreateInstance(&host,IID_IUnknown,(void**)&host.inner);
        Check(hr==S_OK && host.inner && host.refs==1,"Windows audio engine aggregation activation");
        IAudioProcessingObject* apo=nullptr;
        hr=host.QueryInterface(__uuidof(IAudioProcessingObject),(void**)&apo);
        Check(hr==S_OK && host.refs==2,"Aggregated APO delegates lifetime to host");
        if(apo){
            IUnknown* identity=nullptr;apo->QueryInterface(IID_IUnknown,(void**)&identity);
            Check(identity==static_cast<IUnknown*>(&host),"Aggregated interfaces preserve controlling identity");
            if(identity)identity->Release();apo->Release();
        }
        Check(host.refs==1,"Aggregated interface references balanced");
        IAudioProcessingObject* invalid=nullptr;
        Check(factory.CreateInstance(&host,__uuidof(IAudioProcessingObject),(void**)&invalid)==E_NOINTERFACE && !invalid,"Aggregation rejects non-IUnknown creation");
    }
    Check(DllCanUnloadNow()==S_OK,"All COM references released");
    properties->Release();UnmapViewOfFile(shared);CloseHandle(map);CloseHandle(file);CoUninitialize();
    printf("RESULT %s; failures=%d\n",failures?"FAIL":"PASS",failures);return failures?1:0;
}
