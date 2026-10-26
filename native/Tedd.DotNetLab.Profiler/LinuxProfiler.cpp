#include <json.hpp>
#include <atomic>
#include <cstring>
#include <thread>
#include <iomanip>
#include <map>
#include <set>
#include <mutex>
#include <memory>
#include <sys/eventfd.h>
#include <sys/stat.h>
#include <sys/un.h>
#include "LinuxPlatform.h"
// PAL SAL macros must follow the standard headers; their legacy names overlap
// with implementation identifiers in libstdc++.
#include <cor.h>
#include <corprof.h>
#include "CallbackBase.h"
#include <corerror.h>

using json=nlohmann::json;
extern "C" const IID IID_IUnknown={0,0,0,{0xc0,0,0,0,0,0,0,0x46}};
extern "C" const IID IID_IClassFactory={1,0,0,{0xc0,0,0,0,0,0,0,0x46}};
static constexpr GUID ProfilerClsid={0x63ea6b80,0x5694,0x4b6b,{0x8d,0x4a,0x75,0x4c,0xb2,0x24,0xae,0x37}};
static bool Equal(REFIID first,REFIID second) { return std::memcmp(&first,&second,sizeof(GUID))==0; }
static std::atomic<ULONG> objects{0};
static std::atomic<unsigned long long> observedCodeEpoch{0};
static std::atomic<int> activity{0};
static std::atomic<long long> executionEpoch{0};
static std::atomic<bool> mutationPending{false},mutationTainted{false},setupObserved{false},setupCorrect{false};
static std::string activeMethodScope;
static void Check(HRESULT result,const char* action) {
    if(FAILED(result)) throw Failure("profiler_api_failed",std::string(action)+" HRESULT="+std::to_string(static_cast<int32_t>(result)));
}
static std::string Utf8(const std::basic_string<WCHAR>& text) { return linux_platform::Utf8(text.data(),text.size()); }
static std::string Sha(const std::vector<unsigned char>& bytes) {
    return linux_platform::Sha(std::string(reinterpret_cast<const char*>(bytes.data()),bytes.size()));
}
#include "Inspector.h"
#include "Cil.h"
#include "Inlining.h"
#include "Mutation.h"
struct InspectionLease {
    InspectionLease() { int expected=0; if(!activity.compare_exchange_strong(expected,2)) throw Failure("lease_conflict","Snapshot cannot overlap registered execution."); }
    ~InspectionLease() { activity.store(0); }
};
struct Fd {
    int value=-1; explicit Fd(int descriptor=-1):value(descriptor) {}
    ~Fd() { if(value>=0) close(value); }
    Fd(const Fd&)=delete; Fd& operator=(const Fd&)=delete;
};
struct SocketPath {
    std::string value; dev_t device=0; ino_t inode=0;
    ~SocketPath() {
        struct stat current{};
        if(inode&&lstat(value.c_str(),&current)==0&&S_ISSOCK(current.st_mode)&&current.st_dev==device&&current.st_ino==inode)
            unlink(value.c_str());
    }
};

class LinuxProfiler final : public CallbackBase {
    std::atomic<ULONG> references{1}; ICorProfilerInfo12* info=nullptr;
    Fd stopping; std::thread worker; std::atomic<bool> stopped{false};
    unsigned long long start=0; std::string target,workspace;
    NativeAuthorization authorization;
    Inlining inlining;
    std::unique_ptr<Mutation> mutation;
    json Handle(const json& request) {
        if(request.value("schema_version","")!="1.0") throw Failure("schema_unsupported","Unsupported native protocol.");
        if(!request.contains("process_id")||!request["process_id"].is_number_integer()||request["process_id"].get<long long>()!=getpid()||
            request.value("target_id","")!=target||request.value("start_time_utc_ticks","")!=std::to_string(start))
            throw Failure("target_identity_changed","Native process identity differs.");
        auto action=request.value("action","");
        if(action!="handshake"&&request.value("expected_target_epoch","")!=std::to_string(executionEpoch.load())) throw Failure("stale_epoch","Native execution epoch differs.");
        if(action=="snapshot") {
            InspectionLease lease;
            for(auto field=request.begin();field!=request.end();++field)
                if(field.key()!="schema_version"&&field.key()!="action"&&field.key()!="process_id"&&field.key()!="target_id"&&field.key()!="start_time_utc_ticks"&&
                    field.key()!="expected_target_epoch"&&field.key()!="grant"&&field.key()!="selector") throw Failure("invalid_request","Unknown snapshot field.");
            authorization.CheckGrant(request,"inspect_live_code",target,start,workspace,executionEpoch.load());
            auto result=Inspector(info,observedCodeEpoch).Snapshot(request,target,executionEpoch.load());
            mutation->Label(result);
            auto selected=Inspector(info,observedCodeEpoch).Resolve(request.at("selector"));
            result["inliners"]=json::array();
            if(!(selected.implementation&miNoInlining)) {
                Inspector inspector(info,observedCodeEpoch);
                result["inliners"]=InlinerBindings(inlining.Resolve(info,inspector,selected));
            }
            return result;
        }
        if(action=="mutation_status") {
            for(auto field=request.begin();field!=request.end();++field)
                if(field.key()!="schema_version"&&field.key()!="action"&&field.key()!="process_id"&&field.key()!="target_id"&&field.key()!="start_time_utc_ticks"&&field.key()!="expected_target_epoch")
                    throw Failure("invalid_request","Unknown mutation status field.");
            return mutation->Status();
        }
        if(action=="preflight"||action=="apply"||action=="verify_mutation"||action=="restore"||action=="reconcile_mutation") {
            static const std::set<std::string> fields={"schema_version","action","target_id","process_id","start_time_utc_ticks","expected_target_epoch","grant","selector","transaction_id","body","base_il_hash","inliners","probe","probe_id","helper_hash","enter_token","exit_token","exceptional_exit_token","probe_local_signature","probe_local_count","return_local","body_policy"};
            for(auto field=request.begin();field!=request.end();++field) if(!fields.contains(field.key())) throw Failure("invalid_request","Unknown Linux mutation field.");
            if(linux_platform::Environment("DOTNETLAB_DISPOSABLE")!="1") throw Failure("capability_unsupported","Linux mutation requires an explicitly disposable startup target.");
            InspectionLease lease; auto body=DecodeBase64(request.value("body","")); const auto& selector=request.at("selector");
            if(selector.value("module_hash","").size()!=64) throw Failure("invalid_request","Mutation requires an explicit module hash.");
            if(!Mutation::ManagedPolicy(request)) CilArguments(selector.value("signature",""));
            auto scope=selector.value("module_mvid","")+"/"+selector.value("method_token","")+"/"+selector.value("module_hash","");
            authorization.CheckGrant(request,request.value("probe",false)?"install_probes":"replace_il",target,start,workspace,executionEpoch.load(),MutationGrantHash(request,body),scope);
            Check(info->InitializeCurrentThread(),"InitializeCurrentThread");
            USHORT instance=0,major=0,minor=0,build=0,qfe=0; COR_PRF_RUNTIME_TYPE runtime=COR_PRF_CORE_CLR;
            WCHAR version[256]{}; ULONG needed=0;
            Check(info->GetRuntimeInformation(&instance,&runtime,&major,&minor,&build,&qfe,256,&needed,version),"GetRuntimeInformation");
            auto runtimeVersion=needed<=256?linux_platform::Utf8(version,256):"";
            if(runtimeVersion.empty()) runtimeVersion=std::to_string(major)+"."+std::to_string(minor)+"."+std::to_string(build);
            if(runtime!=COR_PRF_CORE_CLR||needed>256||!QualifiedRuntimeVersion(major,minor,build,runtimeVersion,false))
                throw Failure("capability_unsupported","Runtime is outside the Linux CoreCLR profiler matrix.");
            auto selected=Inspector(info,observedCodeEpoch).Resolve(selector);
            if(action=="preflight") return mutation->Preflight(request,selected,body);
            if(action=="apply") return mutation->Apply(request,std::move(selected),body);
            if(action=="reconcile_mutation") return mutation->Reconcile(request,selected);
            mutation->RequireScope(request,Sha(body),scope);
            if(action=="restore") return mutation->Restore(request);
            return mutation->Verify(request);
        }
        if(action!="handshake") throw Failure("capability_unsupported","Native action is outside the Linux qualification subset.");
        for(auto field=request.begin();field!=request.end();++field)
            if(field.key()!="schema_version"&&field.key()!="action"&&field.key()!="process_id"&&field.key()!="target_id"&&
                field.key()!="start_time_utc_ticks"&&field.key()!="expected_target_epoch") throw Failure("invalid_request","Unknown handshake field.");
        HRESULT result=info->InitializeCurrentThread();
        if(FAILED(result)) throw Failure("profiler_api_failed","Profiler worker registration failed.");
        USHORT instance=0,major=0,minor=0,build=0,qfe=0; COR_PRF_RUNTIME_TYPE runtime=COR_PRF_CORE_CLR;
        WCHAR version[256]{}; ULONG needed=0;
        result=info->GetRuntimeInformation(&instance,&runtime,&major,&minor,&build,&qfe,256,&needed,version);
        if(FAILED(result)||needed>256) throw Failure("profiler_api_failed","Bounded runtime discovery failed.");
        auto runtimeVersion=linux_platform::Utf8(version,256);
        if(runtimeVersion.empty()) runtimeVersion=std::to_string(major)+"."+std::to_string(minor)+"."+std::to_string(build);
        if(runtime!=COR_PRF_CORE_CLR||!QualifiedRuntimeVersion(major,minor,build,runtimeVersion,false))
            throw Failure("capability_unsupported","Runtime is outside the Linux CoreCLR profiler matrix.");
        return json{{"schema_version","1.0"},{"target_id",target},{"process_id",getpid()},{"start_time_utc_ticks",std::to_string(start)},
            {"runtime",runtimeVersion},
            {"runtime_string",runtimeVersion},{"architecture","x64"},{"epoch",std::to_string(executionEpoch.load())},{"startup",true},
            {"workspace",workspace},{"interface","ICorProfilerInfo12"},{"native_inspection","experimental"},
            {"rejit",linux_platform::Environment("DOTNETLAB_DISPOSABLE")=="1"?"experimental":"unsupported"},
            {"probes",linux_platform::Environment("DOTNETLAB_DISPOSABLE")=="1"?"experimental":"unsupported"},
            {"restore",linux_platform::Environment("DOTNETLAB_DISPOSABLE")=="1"?"experimental":"unsupported"},
            {"inliner_handling",linux_platform::Environment("DOTNETLAB_DISPOSABLE")=="1"?"experimental":"unsupported"},{"execution_active",activity.load()==1}};
    }
    void Serve() noexcept {
        try {
            auto directory=linux_platform::Environment("TMPDIR"); struct stat parent{};
            if(directory.empty()||directory.front()!='/'||lstat(directory.c_str(),&parent)!=0||!S_ISDIR(parent.st_mode)||
                parent.st_uid!=geteuid()||(parent.st_mode&0077)!=0) return;
            SocketPath owned;
            owned.value=directory+(directory.back()=='/'?"":"/")+"CoreFxPipe_dotnetlab-profiler-"+std::to_string(getpid())+"-"+std::to_string(start);
            sockaddr_un address{}; address.sun_family=AF_UNIX;
            if(owned.value.size()>=sizeof(address.sun_path)) return;
            std::memcpy(address.sun_path,owned.value.c_str(),owned.value.size()+1);
            Fd listener(socket(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC|SOCK_NONBLOCK,0));
            if(listener.value<0||bind(listener.value,reinterpret_cast<sockaddr*>(&address),sizeof(address))!=0) return;
            struct stat bound{};
            if(lstat(owned.value.c_str(),&bound)!=0) return;
            owned.device=bound.st_dev; owned.inode=bound.st_ino;
            if(chmod(owned.value.c_str(),0600)!=0||listen(listener.value,4)!=0) return;
            while(!stopped.load()) {
                pollfd waiting[2]={{listener.value,POLLIN,0},{stopping.value,POLLIN,0}};
                int ready=poll(waiting,2,-1);
                if(ready<0&&errno==EINTR) continue;
                if(ready<=0||waiting[1].revents) break;
                Fd client(accept4(listener.value,nullptr,nullptr,SOCK_CLOEXEC|SOCK_NONBLOCK));
                if(client.value<0) continue;
                ucred credentials{}; socklen_t size=sizeof(credentials);
                if(getsockopt(client.value,SOL_SOCKET,SO_PEERCRED,&credentials,&size)!=0||size!=sizeof(credentials)||credentials.uid!=geteuid()||credentials.pid<=0) continue;
                auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(10);
                std::array<unsigned char,4> prefix{};
                if(!linux_platform::Transfer(client.value,stopping.value,prefix.data(),4,deadline,false)) continue;
                uint32_t length=static_cast<uint32_t>(prefix[0])|(static_cast<uint32_t>(prefix[1])<<8)|
                    (static_cast<uint32_t>(prefix[2])<<16)|(static_cast<uint32_t>(prefix[3])<<24);
                if(length==0||length>1048576) continue;
                std::string input(length,'\0');
                if(!linux_platform::Transfer(client.value,stopping.value,input.data(),length,deadline,false)) continue;
                json response;
                try {
                    auto bounded=[](int depth,json::parse_event_t,json&) {
                        if(depth>32) throw Failure("resource_limit","Native request nesting exceeds policy.");
                        return true;
                    };
                    response={{"schema_version","1.0"},{"status","ok"},{"data",Handle(json::parse(input,bounded))},{"code",nullptr},{"message",nullptr}};
                }
                catch(const Failure& failure) { response={{"schema_version","1.0"},{"status","error"},{"data",nullptr},{"code",failure.code},{"message",failure.what()}}; }
                catch(const json::exception&) { response={{"schema_version","1.0"},{"status","error"},{"data",nullptr},{"code","invalid_request"},{"message","Invalid bounded native request."}}; }
                auto output=response.dump();
                if(output.size()>1048576) output=json{{"schema_version","1.0"},{"status","error"},{"data",nullptr},
                    {"code","resource_limit"},{"message","Native response exceeds frame budget."}}.dump();
                auto bytes=static_cast<uint32_t>(output.size());
                for(size_t index=0;index<4;index++) prefix[index]=static_cast<unsigned char>(bytes>>(8*index));
                unsigned char acknowledgement=0;
                if(linux_platform::Transfer(client.value,stopping.value,prefix.data(),4,deadline,true)&&
                    linux_platform::Transfer(client.value,stopping.value,output.data(),output.size(),deadline,true))
                    linux_platform::Transfer(client.value,stopping.value,&acknowledgement,1,deadline,false);
            }
        } catch(...) { /* Startup discovery becomes unavailable; no elevation or mutation fallback. */ }
    }
public:
    LinuxProfiler():stopping(eventfd(0,EFD_CLOEXEC|EFD_NONBLOCK)) { objects.fetch_add(1); }
    ~LinuxProfiler() { Stop(); if(info) info->Release(); objects.fetch_sub(1); }
    void Stop() noexcept {
        stopped.store(true); uint64_t signal=1;
        if(stopping.value>=0) { auto ignored=write(stopping.value,&signal,sizeof(signal)); (void)ignored; }
        if(worker.joinable()) worker.join();
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** output) override {
        if(!output) return E_POINTER; *output=nullptr;
        if(Equal(iid,IID_IUnknown)||Equal(iid,IID_ICorProfilerCallback)||Equal(iid,IID_ICorProfilerCallback2)||
            Equal(iid,IID_ICorProfilerCallback3)||Equal(iid,IID_ICorProfilerCallback4)) { *output=static_cast<ICorProfilerCallback4*>(this); AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return references.fetch_add(1)+1; }
    ULONG STDMETHODCALLTYPE Release() override { auto count=references.fetch_sub(1)-1; if(!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE Initialize(IUnknown* unknown) override {
        try {
            if(!unknown||stopping.value<0) return E_FAIL;
            start=linux_platform::StartTicks(); workspace=linux_platform::Environment("DOTNETLAB_WORKSPACE");
            target="target-"+linux_platform::Sha("{\"pid\":"+std::to_string(getpid())+",\"start\":\""+std::to_string(start)+"\"}").substr(0,24);
            HRESULT result=unknown->QueryInterface(IID_ICorProfilerInfo12,reinterpret_cast<void**>(&info));
            if(FAILED(result)) return result;
            mutation=std::make_unique<Mutation>(info,inlining);
            result=info->SetEventMask2(COR_PRF_ENABLE_REJIT|COR_PRF_MONITOR_JIT_COMPILATION|COR_PRF_MONITOR_MODULE_LOADS,0); if(FAILED(result)) return result;
            worker=std::thread([this]{Serve();}); return S_OK;
        } catch(...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown*,void*,UINT) override { return E_NOTIMPL; }
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
    Factory() { objects.fetch_add(1); } ~Factory() { objects.fetch_sub(1); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** output) override {
        if(!output) return E_POINTER; *output=nullptr;
        if(!Equal(iid,IID_IUnknown)&&!Equal(iid,IID_IClassFactory)) return E_NOINTERFACE;
        *output=static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return references.fetch_add(1)+1; }
    ULONG STDMETHODCALLTYPE Release() override { auto count=references.fetch_sub(1)-1; if(!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** output) override {
        if(outer) return CLASS_E_NOAGGREGATION;
        auto* profiler=new(std::nothrow) LinuxProfiler(); if(!profiler) return E_OUTOFMEMORY;
        auto result=profiler->QueryInterface(iid,output); profiler->Release(); return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL locked) override { if(locked) objects.fetch_add(1); else objects.fetch_sub(1); return S_OK; }
};
extern "C" __attribute__((visibility("default"))) HRESULT DllGetClassObject(REFCLSID clsid,REFIID iid,void** output) {
    if(!output) return E_POINTER; *output=nullptr;
    if(!Equal(clsid,ProfilerClsid)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory=new(std::nothrow) Factory(); if(!factory) return E_OUTOFMEMORY;
    auto result=factory->QueryInterface(iid,output); factory->Release(); return result;
}
extern "C" __attribute__((visibility("default"))) HRESULT DllCanUnloadNow() { return objects.load()==0?S_OK:S_FALSE; }
extern "C" __attribute__((visibility("default"))) long long DotNetLabGetEpoch() { return executionEpoch.load(); }
extern "C" __attribute__((visibility("default"))) int DotNetLabEnterExecution() {
    int expected=0; if(!activity.compare_exchange_strong(expected,1)) return 0;
    if(mutationPending.load()||mutationTainted.load()) { activity.store(0); return 0; } return 1;
}
extern "C" __attribute__((visibility("default"))) int DotNetLabEnterSetup() {
    if(mutationTainted.load()) return 0;
    int expected=0; return activity.compare_exchange_strong(expected,1)?1:0;
}
extern "C" __attribute__((visibility("default"))) void DotNetLabReportSetup(long long epoch,int correct,const char* scope) {
    if(scope&&activity.load()==1&&epoch==executionEpoch.load()&&mutationPending.load()&&activeMethodScope==scope)
    { setupCorrect.store(correct!=0); setupObserved.store(true); }
}
extern "C" __attribute__((visibility("default"))) void DotNetLabLeaveExecution() { activity.store(0); }
