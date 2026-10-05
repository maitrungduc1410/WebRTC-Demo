// Keeps Windows' voice-capture DMO out of libwebrtc's audio device module. With the DMO, the ADM
// echo-cancels in the DMO instead of AEC3, but its capture only starts while playout runs: the
// first send stream of a call usually starts before any audio is received (always in a group
// call, whose publish connection only sends), StartRecording then fails and nothing retries it,
// and when playout stops the DMO's capture thread ends for good. Either way the call sends no
// audio. The ADM creates the DMO once, in its constructor, so a class factory that refuses to
// make one while the factory initializes leaves it on plain WASAPI capture with AEC3.
#include "shim_internal.h"

#include <exception>
#include <thread>

#include <windows.h>
#include <objbase.h>
#include <wmcodecdsp.h>

namespace rtc_shim {
namespace {

class RefusingClassFactory final : public IClassFactory {
 public:
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** out) override {
    if (!out) return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_IClassFactory) {
      *out = static_cast<IClassFactory*>(this);
      return S_OK;
    }
    *out = nullptr;
    return E_NOINTERFACE;
  }
  // A static object: COM's references don't own it.
  ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
  ULONG STDMETHODCALLTYPE Release() override { return 1; }
  HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown*, REFIID, void** out) override {
    if (out) *out = nullptr;
    refused = true;
    return CLASS_E_CLASSNOTAVAILABLE;
  }
  HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }

  std::atomic<bool> refused{false};
};

RefusingClassFactory g_voice_capture_dmo;
// One registration at a time: a second CoRegisterClassObject of the CLSID would fail.
std::mutex g_initialize_mutex;

}  // namespace

bool InitializeFactory(libwebrtc::RTCPeerConnectionFactory* factory) {
  std::lock_guard<std::mutex> lock(g_initialize_mutex);
  bool ok = false;
  bool registered = false;
  std::exception_ptr error;
  g_voice_capture_dmo.refused = false;
  // The ADM is created on libwebrtc's worker thread, which joins the multithreaded apartment, so
  // the class object is registered from (and stays visible through) an MTA thread.
  std::thread([&] {
    const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD cookie = 0;
    registered = SUCCEEDED(CoRegisterClassObject(CLSID_CWMAudioAEC, &g_voice_capture_dmo,
                                                 CLSCTX_INPROC_SERVER, REGCLS_MULTIPLEUSE,
                                                 &cookie));
    try {
      ok = factory->Initialize();
    } catch (...) {
      error = std::current_exception();
    }
    if (registered) CoRevokeClassObject(cookie);
    if (SUCCEEDED(com)) CoUninitialize();
  }).join();
  if (error) std::rethrow_exception(error);
  if (g_voice_capture_dmo.refused) {
    ShimLog(RTC_LOG_INFO,
            "(shim_audio_win.cpp): voice-capture DMO kept out of the audio device; AEC3 "
            "cancels echo");
  } else {
    ShimLog(RTC_LOG_WARNING,
            registered ? "(shim_audio_win.cpp): the audio device didn't ask for the voice-capture "
                         "DMO; if it uses it, the microphone may send nothing"
                       : "(shim_audio_win.cpp): couldn't hide the voice-capture DMO; the "
                         "microphone may send nothing");
  }
  return ok;
}

}  // namespace rtc_shim
