#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <ks.h>
#include <ksmedia.h>
#include <cstdio>
#include <cmath>
#include <share.h>
#include "BridgeProtocol.h"

static double Level(IAudioCaptureClient* capture,const WAVEFORMATEX* format,int milliseconds,long long& frames,HRESULT& error) {
    const GUID floatFormat={3,0,0x10,{0x80,0,0,0xaa,0,0x38,0x9b,0x71}};
    bool isFloat=format->wFormatTag==WAVE_FORMAT_IEEE_FLOAT || (format->wFormatTag==WAVE_FORMAT_EXTENSIBLE && ((const WAVEFORMATEXTENSIBLE*)format)->SubFormat==floatFormat);
    double sinSum=0,cosSum=0;frames=0;ULONGLONG until=GetTickCount64()+milliseconds;
    while(GetTickCount64()<until) {
        UINT32 packet=0;error=capture->GetNextPacketSize(&packet);if(FAILED(error))return 0;
        while(packet) {
            BYTE* data=nullptr;UINT32 count=0;DWORD flags=0;error=capture->GetBuffer(&data,&count,&flags,nullptr,nullptr);if(FAILED(error))return 0;
            for(UINT32 i=0;i<count;i++,frames++) {
                double sample=0;
                if(!(flags&AUDCLNT_BUFFERFLAGS_SILENT)) {
                    if(isFloat&&format->wBitsPerSample==32)sample=((const float*)data)[i*format->nChannels];
                    else if(!isFloat&&format->wBitsPerSample==16)sample=((const short*)data)[i*format->nChannels]/32768.0;
                    else if(!isFloat&&format->wBitsPerSample==32)sample=((const int*)data)[i*format->nChannels]/2147483648.0;
                }
                double phase=2*3.141592653589793*997*frames/format->nSamplesPerSec;
                sinSum+=sample*sin(phase);cosSum+=sample*cos(phase);
            }
            capture->ReleaseBuffer(count);error=capture->GetNextPacketSize(&packet);if(FAILED(error))return 0;
        }
        Sleep(4);
    }
    return frames?2*sqrt(sinSum*sinSum+cosSum*cosSum)/frames:0;
}
int wmain(int argc,wchar_t** argv) {
    if(argc<4||argc>5) { printf("Usage: MicProbe endpoint-guid bridge-file result-file [communications|observe|observe-communications]\n");return 2; }
    bool observe=argc==5&&wcsstr(argv[4],L"observe"),communications=argc==5&&wcsstr(argv[4],L"communications");
    bool raw=argc==5&&wcsstr(argv[4],L"raw");
    FILE* log=_wfsopen(argv[3],L"w",_SH_DENYNO);if(!log)return 3;
    HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    GUID mixerClsid={0x707a2e34,0x33cf,0x493d,{0xb7,0x53,0x72,0x71,0xe5,0x70,0x91,0xab}};
    IUnknown* registered=nullptr;HRESULT registeredResult=CoCreateInstance(mixerClsid,nullptr,CLSCTX_INPROC_SERVER,IID_IUnknown,(void**)&registered);
    fprintf(log,"Registered COM activation: 0x%08lx\n",(unsigned long)registeredResult);if(registered)registered->Release();
    IMMDeviceEnumerator* enumerator=nullptr;IMMDevice* device=nullptr;IAudioClient* client=nullptr;IAudioCaptureClient* capture=nullptr;WAVEFORMATEX* format=nullptr;
    HANDLE file=INVALID_HANDLE_VALUE,map=nullptr;yuki::Shared* shared=nullptr;bool started=false;int result=1;
    wchar_t endpoint[128];swprintf_s(endpoint,L"{0.0.1.00000000}.%ls",argv[1]);
    do {
        hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,__uuidof(IMMDeviceEnumerator),(void**)&enumerator);if(FAILED(hr))break;
        hr=enumerator->GetDevice(endpoint,&device);if(FAILED(hr))break;
        file=CreateFileW(argv[2],GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);if(file==INVALID_HANDLE_VALUE){hr=HRESULT_FROM_WIN32(GetLastError());break;}
        LARGE_INTEGER size;if(!GetFileSizeEx(file,&size)||size.QuadPart!=sizeof(yuki::Shared)){hr=E_INVALIDARG;break;}
        map=CreateFileMappingW(file,nullptr,PAGE_READWRITE,0,0,nullptr);if(!map){hr=HRESULT_FROM_WIN32(GetLastError());break;}
        shared=(yuki::Shared*)MapViewOfFile(map,FILE_MAP_ALL_ACCESS,0,0,sizeof(yuki::Shared));if(!shared){hr=HRESULT_FROM_WIN32(GetLastError());break;}
        if(shared->header.magic!=yuki::Magic){hr=E_INVALIDARG;break;}
        InterlockedExchange(&shared->header.active,0);
        hr=device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,(void**)&client);if(FAILED(hr))break;
        if(communications||raw) {
            IAudioClient2* client2=nullptr;hr=client->QueryInterface(__uuidof(IAudioClient2),(void**)&client2);if(FAILED(hr))break;
            AudioClientProperties properties{};properties.cbSize=sizeof(properties);properties.eCategory=communications?AudioCategory_Communications:AudioCategory_Other;
            if(raw)properties.Options=AUDCLNT_STREAMOPTIONS_RAW;
            hr=client2->SetClientProperties(&properties);client2->Release();if(FAILED(hr))break;
        }
        fprintf(log,"Mode: %s; raw: %s; writer: %s\n",communications?"Communications":"Default",raw?"yes":"no",observe?"application":"probe");
        hr=client->GetMixFormat(&format);if(FAILED(hr))break;
        fprintf(log,"Format: %lu Hz, %u channels, %u bits, tag=%u\n",format->nSamplesPerSec,format->nChannels,format->wBitsPerSample,format->wFormatTag);
        hr=client->Initialize(AUDCLNT_SHAREMODE_SHARED,0,1000000,0,format,nullptr);if(FAILED(hr))break;
        hr=client->GetService(__uuidof(IAudioCaptureClient),(void**)&capture);if(FAILED(hr))break;
        hr=client->Start();if(FAILED(hr))break;started=true;
        long long baselineFrames=0,injectedFrames=0;double baseline=Level(capture,format,700,baselineFrames,hr);if(FAILED(hr))break;
        auto& h=shared->header;LONG64 before=yuki::Read64(&h.injectedFrames);
        if(observe) {
            fprintf(log,"READY\n");fflush(log);ULONGLONG until=GetTickCount64()+10000;
            while(yuki::Read32(&h.active)!=1 && GetTickCount64()<until)Sleep(5);
            if(yuki::Read32(&h.active)!=1){hr=HRESULT_FROM_WIN32(WAIT_TIMEOUT);break;}
        } else {
        for(uint32_t f=0;f<yuki::Capacity;f++)shared->samples[f*2]=shared->samples[f*2+1]=(float)(.125*sin(2*3.141592653589793*997*f/yuki::Rate));
        LARGE_INTEGER qpc,freq;QueryPerformanceCounter(&qpc);QueryPerformanceFrequency(&freq);
        InterlockedIncrement(&h.generation);InterlockedExchange64(&h.publishedFrames,yuki::Capacity);InterlockedExchange64(&h.qpcFrequency,freq.QuadPart);InterlockedExchange64(&h.startQpc,qpc.QuadPart);InterlockedExchange64(&h.heartbeatMs,GetTickCount64());h.gain=1;MemoryBarrier();InterlockedExchange(&h.active,1);
        }
        // Stay under the 1 s watchdog; no voice samples are saved to disk.
        double injected=Level(capture,format,800,injectedFrames,hr);InterlockedExchange(&h.active,0);if(FAILED(hr))break;
        fprintf(log,"Baseline tone amplitude: %.6f (%lld frames)\nInjected tone amplitude: %.6f (%lld frames)\nAPO callbacks: %lld\nAPO injected frames: %lld\nAPO initialization error: 0x%08lx\n",baseline,baselineFrames,injected,injectedFrames,yuki::Read64(&h.processCalls),yuki::Read64(&h.injectedFrames)-before,yuki::Read32(&h.lastError));
        if(injected>.035 && injected>baseline*2 && yuki::Read64(&h.injectedFrames)>before)result=0;
    }while(false);
    if(shared){InterlockedExchange(&shared->header.active,0);UnmapViewOfFile(shared);}if(map)CloseHandle(map);if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);
    if(started)client->Stop();if(capture)capture->Release();if(format)CoTaskMemFree(format);if(client)client->Release();if(device)device->Release();if(enumerator)enumerator->Release();
    fprintf(log,"HRESULT: 0x%08lx\nRESULT %s\n",(unsigned long)hr,result==0?"PASS":"FAIL");fclose(log);CoUninitialize();return result;
}
