#define INITGUID
#include <windows.h>
#include <sddl.h>
#include <bcrypt.h>
#include <cor.h>
#include <corprof.h>
#include <corerror.h>
#include <json.hpp>
#include <atomic>
#include <thread>
#include <string>
#include <vector>
#include <stdexcept>
#include <chrono>
#include <array>
#include <filesystem>
#include "CallbackBase.h"
#include "Failure.h"
using json=nlohmann::json;
static const GUID ProfilerClsid={0x63ea6b80,0x5694,0x4b6b,{0x8d,0x4a,0x75,0x4c,0xb2,0x24,0xae,0x37}};
static std::atomic<long long> executionEpoch{0};
static std::atomic<ULONG> objects{0};
static std::atomic<unsigned long long> observedCodeEpoch{0};
static std::atomic<int> activity{0};
static std::atomic<bool> mutationPending{false},mutationTainted{false},setupObserved{false},setupCorrect{false};
static std::string activeMethodScope;
struct ActivityLease {
    ActivityLease() { int expected=0; if(!activity.compare_exchange_strong(expected,2))
        throw Failure("lease_conflict","Snapshot cannot overlap registered execution."); }
    ~ActivityLease() { activity.store(0); }
};
static void Check(HRESULT hr,const char* action) {
    if(FAILED(hr)) throw Failure("profiler_api_failed",std::string(action)+" HRESULT="+std::to_string(static_cast<unsigned long>(hr)));
}
static std::string Utf8(const std::wstring& text) {
    if(text.empty()) return "";
    int count=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0,nullptr,nullptr);
    if(!count) throw Failure("invalid_text","Invalid UTF-16.");
    std::string result(static_cast<size_t>(count),'\0');
    WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),result.data(),count,nullptr,nullptr);
    return result;
}
[[maybe_unused]] static std::wstring Wide(const std::string& text) {
    if(text.empty()) return L"";
    int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0);
    if(!count) throw Failure("invalid_text","Invalid UTF-8.");
    std::wstring result(static_cast<size_t>(count),L'\0');
    MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),result.data(),count);
    return result;
}
static std::string Sha(const std::vector<unsigned char>& bytes) {
    BCRYPT_ALG_HANDLE algorithm=nullptr; BCRYPT_HASH_HANDLE hash=nullptr;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0) throw Failure("hash_failed","SHA provider failed.");
    std::array<unsigned char,32> digest{};
    NTSTATUS status=BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0);
    if(status>=0) status=BCryptHashData(hash,const_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(bytes.size()),0);
    if(status>=0) status=BCryptFinishHash(hash,digest.data(),static_cast<ULONG>(digest.size()),0);
    if(hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm,0);
    if(status<0) throw Failure("hash_failed","SHA operation failed.");
    const char* digits="0123456789abcdef"; std::string result;
    for(auto value:digest) { result+=digits[value>>4]; result+=digits[value&15]; }
    return result;
}
static unsigned long long StartTicks() {
    FILETIME creation,exit,kernel,user;
    if(!GetProcessTimes(GetCurrentProcess(),&creation,&exit,&kernel,&user)) throw Failure("identity_failed","Process identity unavailable.");
    ULARGE_INTEGER value; value.LowPart=creation.dwLowDateTime; value.HighPart=creation.dwHighDateTime;
    return value.QuadPart+504911232000000000ULL;
}
static std::wstring EnvironmentValue(const wchar_t* name) {
    DWORD size=GetEnvironmentVariableW(name,nullptr,0);
    if(size==0) return L"";
    if(size>32768) throw Failure("resource_limit","Environment value exceeds policy.");
    std::wstring result(size,L'\0');
    DWORD actual=GetEnvironmentVariableW(name,result.data(),size);
    result.resize(actual); return result;
}
struct IoEvent { HANDLE handle=CreateEventW(nullptr,TRUE,FALSE,nullptr); ~IoEvent() { if(handle) CloseHandle(handle); } };
static bool CompleteIo(HANDLE pipe,OVERLAPPED& operation,HANDLE stop,DWORD timeout,DWORD& count) {
    HANDLE handles[]={operation.hEvent,stop};
    DWORD wait=WaitForMultipleObjects(2,handles,FALSE,timeout);
    if(wait==WAIT_OBJECT_0) return GetOverlappedResult(pipe,&operation,&count,FALSE)!=FALSE;
    CancelIoEx(pipe,&operation); GetOverlappedResult(pipe,&operation,&count,TRUE); return false;
}
static bool Connect(HANDLE pipe,HANDLE stop) {
    IoEvent event; if(!event.handle) return false;
    OVERLAPPED operation{}; operation.hEvent=event.handle;
    if(WaitForSingleObject(stop,0)==WAIT_OBJECT_0) return false;
    if(ConnectNamedPipe(pipe,&operation)) return true;
    DWORD error=GetLastError(); if(error==ERROR_PIPE_CONNECTED) return true;
    if(error!=ERROR_IO_PENDING) return false;
    DWORD count=0; return CompleteIo(pipe,operation,stop,INFINITE,count);
}
static bool Transfer(HANDLE pipe,void* buffer,DWORD length,HANDLE stop,ULONGLONG deadline,bool write) {
    auto* bytes=static_cast<unsigned char*>(buffer); DWORD total=0;
    while(total<length) {
        ULONGLONG now=GetTickCount64();
        if(now>=deadline||WaitForSingleObject(stop,0)==WAIT_OBJECT_0) return false;
        IoEvent event; if(!event.handle) return false;
        OVERLAPPED operation{}; operation.hEvent=event.handle; DWORD count=0;
        BOOL complete=write?WriteFile(pipe,bytes+total,length-total,&count,&operation):ReadFile(pipe,bytes+total,length-total,&count,&operation);
        if(!complete) {
            if(GetLastError()!=ERROR_IO_PENDING) return false;
            if(!CompleteIo(pipe,operation,stop,static_cast<DWORD>(deadline-now),count)) return false;
        }
        if(count==0) return false; total+=count;
    }
    return true;
}
#include "Inspector.h"
#include "Cil.h"
#include "Inlining.h"
#include "Mutation.h"
class Profiler final : public CallbackBase {
    std::atomic<ULONG> references{1};
    ICorProfilerInfo12* info=nullptr;
    std::atomic<bool> stopped{false};
    HANDLE stopEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    std::thread worker;
    std::string targetId,workspace;
    unsigned long long startTicks=0;
    bool attached=false; std::atomic<bool> attachComplete{false}; NativeAuthorization authorization;
    static constexpr DWORD EventMask=COR_PRF_ENABLE_REJIT|COR_PRF_MONITOR_JIT_COMPILATION|COR_PRF_MONITOR_MODULE_LOADS;
    static_assert((EventMask&~COR_PRF_ALLOWABLE_AFTER_ATTACH)==0);
    std::unique_ptr<Mutation> mutation;
    Inlining inlining;
    json Execute(const json& request) {
        static const std::set<std::string> fields={"schema_version","action","target_id","process_id","start_time_utc_ticks","expected_target_epoch","grant","selector","transaction_id","body","base_il_hash","probe","probe_id","helper_hash","enter_token","exit_token","exceptional_exit_token","probe_local_signature","probe_local_count","return_local","inliners"};
        if(!request.is_object()) throw Failure("invalid_request","Native request must be an object.");
        for(auto entry=request.begin();entry!=request.end();++entry) if(!fields.contains(entry.key()))
            throw Failure("invalid_request","Unknown native request field.");
        if(request.value("schema_version","")!="1.0") throw Failure("schema_unsupported","Native protocol version unsupported.");
        if(request.value("target_id","")!=targetId||request.value("start_time_utc_ticks","")!=std::to_string(startTicks)||
            request.value("process_id",0U)!=GetCurrentProcessId()) throw Failure("target_identity_changed","Native target identity differs.");
        std::string action=request.value("action","");
        if(action=="snapshot") {
            if(activity.load()!=0) throw Failure("lease_conflict","Snapshot cannot overlap registered execution.");
            ActivityLease lease;
            authorization.CheckGrant(request,"inspect_live_code",targetId,startTicks,workspace,executionEpoch.load());
            auto result=Inspector(info,observedCodeEpoch).Snapshot(request,targetId,executionEpoch.load());
            mutation->Label(result);
            auto selected=Inspector(info,observedCodeEpoch).Resolve(request.at("selector"));
            result["inliners"]=json::array();
            result["inliner_coverage"]=selected.implementation&miNoInlining?"excluded_by_verified_noinline_method":
                attached?"not_collected_pre_attach_history":"startup_observed_and_native_enumerated";
            if(!attached&&!(selected.implementation&miNoInlining)) {
                Inspector inspector(info,observedCodeEpoch);
                result["inliners"]=InlinerBindings(inlining.Resolve(info,inspector,selected));
            }
            return result;
        }
        if(action=="mutation_status") return mutation->Status();
        if(action=="preflight"||action=="apply"||action=="verify_mutation"||action=="restore"||action=="reconcile_mutation") {
            if(EnvironmentValue(L"DOTNETLAB_DISPOSABLE")!=L"1")
                throw Failure("capability_unsupported","Native mutation requires an explicitly disposable target.");
            ActivityLease lease;
            auto body=DecodeBase64(request.value("body",""));
            bool probe=request.value("probe",false);
            if(attached&&probe) throw Failure("capability_unsupported","Attach-mode selected probes require separate qualification.");
            const auto& selector=request.at("selector");
            if(selector.value("module_hash","").size()!=64) throw Failure("invalid_request","Mutation requires an explicit module hash.");
            CilArguments(selector.value("signature",""));
            auto scope=selector.value("module_mvid","")+"/"+selector.value("method_token","")+"/"+selector.value("module_hash","");
            authorization.CheckGrant(request,probe?"install_probes":"replace_il",targetId,startTicks,workspace,executionEpoch.load(),MutationGrantHash(request,body),scope);
            Check(info->InitializeCurrentThread(),"InitializeCurrentThread");
            USHORT instance=0,major=0,minor=0,build=0,qfe=0; COR_PRF_RUNTIME_TYPE runtime=COR_PRF_CORE_CLR;
            WCHAR version[256]{}; ULONG needed=0;
            Check(info->GetRuntimeInformation(&instance,&runtime,&major,&minor,&build,&qfe,256,&needed,version),"GetRuntimeInformation");
            if(runtime!=COR_PRF_CORE_CLR||needed>256||!QualifiedRuntimeVersion(major,minor,build,Utf8(version),true))
                throw Failure("capability_unsupported","Runtime is outside the mutation feasibility matrix.");
            if(attached&&!(Inspector(info,observedCodeEpoch).Resolve(selector).implementation&miNoInlining))
                throw Failure("inliner_coverage_unknown","Attach mutation requires a verified no-inline method; pre-attach caller history is unavailable.");
            if(action=="preflight") return mutation->Preflight(request,Inspector(info,observedCodeEpoch).Resolve(selector),body);
            if(action=="apply") return mutation->Apply(request,Inspector(info,observedCodeEpoch).Resolve(selector),body);
            if(action=="reconcile_mutation") return mutation->Reconcile(request,Inspector(info,observedCodeEpoch).Resolve(selector));
            mutation->RequireScope(request,Sha(body),scope);
            if(action=="restore") return mutation->Restore(request);
            return mutation->Verify(request);
        }
        if(action!="handshake") throw Failure("capability_unsupported","Native action is not qualified.");
        USHORT instance=0,major=0,minor=0,build=0,qfe=0; COR_PRF_RUNTIME_TYPE type=COR_PRF_CORE_CLR;
        WCHAR version[256]{}; ULONG needed=0;
        Check(info->GetRuntimeInformation(&instance,&type,&major,&minor,&build,&qfe,256,&needed,version),"GetRuntimeInformation");
        if(type!=COR_PRF_CORE_CLR||needed>256||!QualifiedRuntimeVersion(major,minor,build,Utf8(version),true))
            throw Failure("capability_unsupported","Runtime is outside the Windows startup/attach matrix.");
        return json{{"schema_version","1.0"},{"target_id",targetId},{"process_id",GetCurrentProcessId()},
            {"start_time_utc_ticks",std::to_string(startTicks)},{"runtime",Utf8(version)},
            {"runtime_string",Utf8(version)},{"architecture","x64"},{"epoch",std::to_string(executionEpoch.load())},
            {"startup",!attached},{"workspace",workspace},{"interface","ICorProfilerInfo12"},
            {"native_inspection","experimental"},{"rejit",EnvironmentValue(L"DOTNETLAB_DISPOSABLE")==L"1"?"experimental":"unsupported"},
            {"probes",!attached&&EnvironmentValue(L"DOTNETLAB_DISPOSABLE")==L"1"?"experimental":"unsupported"},
            {"inliner_handling",EnvironmentValue(L"DOTNETLAB_DISPOSABLE")==L"1"?(attached?"verified_noinline_only":"experimental"):"unsupported"},
            {"restore",EnvironmentValue(L"DOTNETLAB_DISPOSABLE")==L"1"?"experimental":"unsupported"},
            {"attachment_complete",!attached||attachComplete.load()},{"event_mask_low",EventMask},
            {"execution_active",activity.load()==1}};
    }
    void Serve() noexcept {
        try {
            HANDLE token=nullptr;
            if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) return;
            DWORD needed=0; GetTokenInformation(token,TokenUser,nullptr,0,&needed);
            std::vector<unsigned char> buffer(needed);
            if(!GetTokenInformation(token,TokenUser,buffer.data(),needed,&needed)) { CloseHandle(token); return; }
            CloseHandle(token);
            LPWSTR sid=nullptr;
            if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid,&sid)) return;
            std::wstring descriptor=L"D:P(A;;GA;;;"+std::wstring(sid)+L")"; LocalFree(sid);
            PSECURITY_DESCRIPTOR security=nullptr;
            if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor.c_str(),SDDL_REVISION_1,&security,nullptr)) return;
            SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES),security,FALSE};
            std::wstring name=L"\\\\.\\pipe\\dotnetlab-profiler-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(startTicks);
            while(!stopped.load()) {
                HANDLE pipe=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_FIRST_PIPE_INSTANCE|FILE_FLAG_OVERLAPPED,
                    PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,1000,&attributes);
                if(pipe==INVALID_HANDLE_VALUE) break;
                if(Connect(pipe,stopEvent)) {
                    ULONGLONG deadline=GetTickCount64()+10000;
                    DWORD length=0;
                    if(Transfer(pipe,&length,4,stopEvent,deadline,false)&&length>0&&length<=1048576) {
                        std::string input(length,'\0'); json result;
                        if(Transfer(pipe,input.data(),length,stopEvent,deadline,false)) {
                            try {
                                auto parsed=json::parse(input,[](int depth,json::parse_event_t,json&) {
                                    if(depth>32) throw Failure("resource_limit","Native JSON nesting exceeds policy."); return true;
                                });
                                result=json{{"schema_version","1.0"},{"status","ok"},{"data",Execute(parsed)},{"code",nullptr},{"message",nullptr}};
                            }
                            catch(const Failure& error) { result=json{{"schema_version","1.0"},{"status","error"},{"data",nullptr},{"code",error.code},{"message",error.what()}}; }
                            catch(const std::exception&) { result=json{{"schema_version","1.0"},{"status","error"},{"data",nullptr},{"code","invalid_request"},{"message","Invalid bounded native request."}}; }
                            std::string output=result.dump();
                            if(output.size()>1048576) output=json{{"schema_version","1.0"},{"status","error"},{"data",nullptr},
                                {"code","resource_limit"},{"message","Native response exceeds frame budget."}}.dump();
                            DWORD size=static_cast<DWORD>(output.size()); unsigned char acknowledgement=0;
                            if(Transfer(pipe,&size,4,stopEvent,deadline,true)&&Transfer(pipe,output.data(),size,stopEvent,deadline,true))
                                Transfer(pipe,&acknowledgement,1,stopEvent,deadline,false);
                        }
                    }
                }
                DisconnectNamedPipe(pipe); CloseHandle(pipe);
            }
            LocalFree(security);
        } catch(...) { /* Endpoint failure remains unavailable, never elevated. */ }
    }
    HRESULT InitializeCore(IUnknown* unknown,const json* attachment=nullptr) noexcept {
        try {
            startTicks=StartTicks();
            if(!stopEvent) return E_OUTOFMEMORY;
            std::string canonical="{\"pid\":"+std::to_string(GetCurrentProcessId())+",\"start\":\""+std::to_string(startTicks)+"\"}";
            targetId="target-"+Sha(std::vector<unsigned char>(canonical.begin(),canonical.end())).substr(0,24);
            workspace=Utf8(EnvironmentValue(L"DOTNETLAB_WORKSPACE"));
            if(attachment) {
                static const std::set<std::string> fields={"schema_version","target_id","process_id","start_time_utc_ticks","workspace"};
                if(!attachment->is_object()||attachment->size()!=fields.size()) return E_INVALIDARG;
                for(auto item=attachment->begin();item!=attachment->end();++item) if(!fields.contains(item.key())) return E_INVALIDARG;
                if(attachment->value("schema_version","")!="1.0"||attachment->value("target_id","")!=targetId||
                    !attachment->at("process_id").is_number_unsigned()||attachment->at("process_id").get<unsigned long long>()!=GetCurrentProcessId()||
                    attachment->value("start_time_utc_ticks","")!=std::to_string(startTicks)) return E_INVALIDARG;
                auto selected=attachment->value("workspace","");
                if(selected.empty()||selected.size()>4096||selected.find('\0')!=std::string::npos||!std::filesystem::path(Wide(selected)).is_absolute()||
                    (!workspace.empty()&&workspace!=selected)) return E_INVALIDARG;
                workspace=selected;
            }
            HRESULT hr=unknown->QueryInterface(__uuidof(ICorProfilerInfo12),reinterpret_cast<void**>(&info));
            if(FAILED(hr)) return hr;
            mutation=std::make_unique<Mutation>(info,inlining,!attached);
            Check(info->SetEventMask2(EventMask,0),"SetEventMask2");
            if(!attached) worker=std::thread([this] { Serve(); });
            return S_OK;
        } catch(...) { return E_FAIL; }
    }
public:
    Profiler() { objects.fetch_add(1); }
    ~Profiler() {
        Stop();
        if(info) info->Release();
        if(stopEvent) CloseHandle(stopEvent);
        objects.fetch_sub(1);
    }
    void Stop() noexcept {
        stopped.store(true);
        if(stopEvent) SetEvent(stopEvent);
        if(worker.joinable()) worker.join();
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** output) override {
        if(!output) return E_POINTER; *output=nullptr;
        if(iid==IID_IUnknown||iid==__uuidof(ICorProfilerCallback)||iid==__uuidof(ICorProfilerCallback2)||
            iid==__uuidof(ICorProfilerCallback3)||iid==__uuidof(ICorProfilerCallback4)) {
            *output=static_cast<ICorProfilerCallback4*>(this); AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return references.fetch_add(1)+1; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG value=references.fetch_sub(1)-1; if(value==0) delete this; return value; }
    HRESULT STDMETHODCALLTYPE Initialize(IUnknown* unknown) override { return InitializeCore(unknown); }
    HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown* unknown,void* data,UINT length) override {
        attached=true;
        if(!data||length==0||length>16384) return E_INVALIDARG;
        try {
            auto payload=json::parse(std::string(static_cast<const char*>(data),length),[](int depth,json::parse_event_t,json&) {
                if(depth>16) throw Failure("resource_limit","Attach metadata nesting exceeds policy."); return true;
            });
            return InitializeCore(unknown,&payload);
        } catch(...) { return E_INVALIDARG; }
    }
    HRESULT STDMETHODCALLTYPE ProfilerAttachComplete() override {
        if(!attached||!info||!mutation) return E_FAIL;
        try { attachComplete.store(true); worker=std::thread([this] { Serve(); }); return S_OK; }
        catch(...) { attachComplete.store(false); return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID,BOOL) override { observedCodeEpoch.fetch_add(1); return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCompilationFinished(FunctionID,HRESULT,BOOL) override { observedCodeEpoch.fetch_add(1); return S_OK; }
    HRESULT STDMETHODCALLTYPE JITInlining(FunctionID caller,FunctionID callee,BOOL* allow) override {
        if(!allow) return E_POINTER; *allow=TRUE;
        inlining.Observe(caller,callee); if(mutation) mutation->ObserveInlining(caller,callee); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID,HRESULT) override { observedCodeEpoch.fetch_add(1); return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID) override { observedCodeEpoch.fetch_add(1); return S_OK; }
    HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID module,mdMethodDef method,ICorProfilerFunctionControl* control) override { return mutation->Parameters(module,method,control); }
    HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(FunctionID function,ReJITID id,BOOL) override { return mutation->Started(function,id); }
    HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(FunctionID function,ReJITID id,HRESULT result,BOOL) override { return mutation->Finished(function,id,result); }
    HRESULT STDMETHODCALLTYPE ReJITError(ModuleID,mdMethodDef,FunctionID,HRESULT) override { return mutation->Error(); }
    HRESULT STDMETHODCALLTYPE Shutdown() override { Stop(); return S_OK; }
};
class Factory final : public IClassFactory {
    std::atomic<ULONG> references{1};
public:
    Factory() { objects.fetch_add(1); }
    ~Factory() { objects.fetch_sub(1); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** output) override {
        if(!output) return E_POINTER; *output=nullptr;
        if(iid==IID_IUnknown||iid==IID_IClassFactory) { *output=this; AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return references.fetch_add(1)+1; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG value=references.fetch_sub(1)-1; if(value==0) delete this; return value; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** output) override {
        if(outer) return CLASS_E_NOAGGREGATION;
        auto* profiler=new(std::nothrow) Profiler(); if(!profiler) return E_OUTOFMEMORY;
        HRESULT result=profiler->QueryInterface(iid,output); profiler->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if(lock) objects.fetch_add(1); else objects.fetch_sub(1); return S_OK; }
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid,REFIID iid,void** output) {
    if(clsid!=ProfilerClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory=new(std::nothrow) Factory(); if(!factory) return E_OUTOFMEMORY;
    HRESULT result=factory->QueryInterface(iid,output); factory->Release(); return result;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects.load()==0?S_OK:S_FALSE; }
extern "C" __declspec(dllexport) long long __stdcall DotNetLabGetEpoch() { return executionEpoch.load(); }
extern "C" __declspec(dllexport) int __stdcall DotNetLabEnterExecution() {
    int expected=0; if(!activity.compare_exchange_strong(expected,1)) return 0;
    if(mutationPending.load()||mutationTainted.load()) { activity.store(0); return 0; } return 1;
}
extern "C" __declspec(dllexport) int __stdcall DotNetLabEnterSetup() {
    if(mutationTainted.load()) return 0;
    int expected=0; return activity.compare_exchange_strong(expected,1)?1:0;
}
extern "C" __declspec(dllexport) void __stdcall DotNetLabReportSetup(long long epoch,int correct,const char* scope) {
    if(scope&&activity.load()==1&&epoch==executionEpoch.load()&&mutationPending.load()&&activeMethodScope==scope)
    { setupCorrect.store(correct!=0); setupObserved.store(true); }
}
extern "C" __declspec(dllexport) void __stdcall DotNetLabLeaveExecution() { activity.store(0); }
