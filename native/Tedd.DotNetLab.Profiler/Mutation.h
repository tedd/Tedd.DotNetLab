#pragma once
#include <mutex>
#include <array>
#include "FaultProbeBody.h"
#include "MutationBudget.h"
#ifdef _WIN32
#include <psapi.h>
#endif
static std::optional<std::uint64_t> MutationPrivateCommit() {
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS_EX memory{}; memory.cb=sizeof(memory);
    if(K32GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),sizeof(memory)))
        return memory.PrivateUsage;
#endif
    return std::nullopt;
}
static std::vector<unsigned char> DecodeBase64(const std::string& text) {
    if(text.size()>5500) throw Failure("resource_limit","Initial mutation body exceeds its byte budget.");
    const std::string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::vector<unsigned char> bytes; unsigned accumulator=0,bits=0;
    for(char character:text) {
        if(character=='=') break;
        auto value=alphabet.find(character);
        if(value==std::string::npos) throw Failure("invalid_cil","Malformed body encoding.");
        accumulator=(accumulator<<6)|static_cast<unsigned>(value); bits+=6;
        if(bits>=8) { bits-=8; bytes.push_back(static_cast<unsigned char>(accumulator>>bits)); }
    }
    if(Base64(bytes)!=text) throw Failure("invalid_cil","Noncanonical body encoding.");
    return bytes;
}
class Mutation {
    std::mutex gate;
    ICorProfilerInfo12* info;
    Inlining& inlining;
    const bool startupHistory;
    Inspector::Method selected;
    struct Caller {
        Inspector::Method method;
        std::array<unsigned char,4096> body{}; ULONG length=0;
        ReJITID started=0,finished=0; HRESULT compilation=S_FALSE,restoreStatus=E_FAIL;
    };
    std::vector<Caller> callers;
    json callerBindings=json::array();
    std::array<std::pair<FunctionID,ReJITID>,1024> priorCompilations{}; size_t priorCount=0;
    std::array<unsigned char,4096> candidate{};
    ULONG candidateLength=0;
    std::string transaction,hash,baseHash,scope,mode="candidate_rejit";
    ReJITID started=0,finished=0;
    bool hasActive=false,restoring=false,reverted=false;
    HRESULT compilation=S_FALSE;
    unsigned mutations=0;
#ifdef _WIN32
    MutationBudget budget{true};
#else
    MutationBudget budget{false};
#endif
    std::array<ReJITID,32> versionIds{};
    std::array<std::string,32> versionHashes;
    std::array<std::array<unsigned char,4096>,32> versionBodies{};
    std::array<ULONG,32> versionLengths{};
public:
    explicit Mutation(ICorProfilerInfo12* info,Inlining& inlining,bool startupHistory=true):info(info),inlining(inlining),startupHistory(startupHistory){}
    bool Prior(FunctionID function,ReJITID id) const {
        for(size_t i=0;i<priorCount;i++) if(priorCompilations[i]==std::pair{function,id}) return true; return false;
    }
    void ObserveInlining(FunctionID caller,FunctionID callee) noexcept {
        std::lock_guard lock(gate);
        if(!hasActive||restoring) return;
        bool affected=callee==selected.function,approved=caller==selected.function;
        for(const auto& item:callers) { affected|=callee==item.method.function; approved|=caller==item.method.function; }
        if(affected&&!approved) mutationTainted.store(true);
    }
    std::vector<Caller> PrepareCallers(const json& request,const Inspector::Method& method) {
        if(!startupHistory&&!(method.implementation&miNoInlining)) throw Failure("inliner_coverage_unknown","Pre-attach caller history is unavailable for this method.");
        Inspector inspector(info,observedCodeEpoch);
        auto methods=inlining.Resolve(info,inspector,method);
        if(request.value("inliners",json::array())!=InlinerBindings(methods))
            throw Failure("inliner_coverage_changed","Frozen caller bindings differ from current startup/native inliner observations.");
        std::vector<Caller> result;
        for(auto& caller:methods) {
            Caller item; item.length=static_cast<ULONG>(caller.original.size());
            std::copy(caller.original.begin(),caller.original.end(),item.body.begin()); item.method=std::move(caller);
            result.push_back(std::move(item));
        }
        return result;
    }
    void RequireScope(const json& request,const std::string& bodyHash,const std::string& methodScope) {
        std::lock_guard lock(gate);
        if(request.value("transaction_id","")!=transaction||bodyHash!=hash||methodScope!=scope||request.value("inliners",json::array())!=callerBindings)
            throw Failure("permission_denied","Active mutation identity/content/method scope differs.");
    }
    json StatusLocked() {
        json affected=json::array(); bool complete=true;
        for(const auto& caller:callers) {
            bool compiled=caller.finished!=0&&caller.compilation==S_OK; complete&=compiled;
            affected.push_back(json{{"binding",Inspector::Binding(caller.method)},{"rejit_id",std::to_string(caller.finished)},
                {"compilation_observed",compiled},{"restore_api_succeeded",restoring&&SUCCEEDED(caller.restoreStatus)}});
        }
        return json{{"transaction_id",transaction},{"epoch",std::to_string(executionEpoch.load())},{"candidate_hash",hash},
            {"method_scope",scope},{"rejit_id",std::to_string(finished)},{"compilation_observed",!restoring&&finished!=0&&compilation==S_OK&&complete},
            {"pending",mutationPending.load()},{"tainted",mutationTainted.load()},{"restoring",restoring},
            {"setup_observed",setupObserved.load()},{"correctness_passed",setupCorrect.load()},
            {"restore_api_succeeded",restoring&&reverted},{"current_body_hash",restoring?baseHash:hash},
            {"mode",restoring?(reverted?"restored_original_version":"restore_pending"):mode},
            {"inliner_coverage",callers.empty()?(selected.implementation&miNoInlining?"excluded_by_verified_noinline_method":startupHistory?"startup-observed-and-native-enumerated/empty":"not_collected_pre_attach_history"):
                "startup-observed-and-native-enumerated/verified-callers"},
            {"affected_callers",affected},{"request_flags",3},{"mutation_count",mutations},
            {"resource_budget",json{{"maximum_candidates",MutationBudget::MaximumCandidates},
                {"maximum_native_extent_bytes",std::to_string(budget.NativeLimit())},{"native_extent_bytes",std::to_string(budget.NativeBytes())},
                {"native_extent_regions",budget.RegionCount()},{"native_extent_accounting","cumulative_observed_regions"},
                {"maximum_private_commit_growth_bytes",std::to_string(budget.PrivateLimit())},{"private_commit_available",budget.MemoryAvailable()},
                {"private_commit_baseline_bytes",budget.MemoryInitialized()?json(std::to_string(budget.PrivateBaseline())):json(nullptr)},
                {"private_commit_sampled_peak_bytes",budget.MemoryInitialized()?json(std::to_string(budget.PrivatePeak())):json(nullptr)},
                {"private_commit_growth_bytes",budget.MemoryInitialized()?json(std::to_string(budget.PrivateGrowth())):json(nullptr)},
                {"restart_required",budget.RestartRequired(mutations)},{"restart_reason",budget.Reason(mutations)}}}};
    }
    json Status() { std::lock_guard lock(gate); if(mutations>0) budget.ObserveMemory(MutationPrivateCommit(),false); return StatusLocked(); }
    json Reconcile(const json& request,const Inspector::Method& method) {
        ValidateBody(method,method.original);
        if(!(method.attributes&mdStatic)||!method.function)
            throw Failure("capability_unsupported","Recovery requires the qualified compiled method subset.");
        auto prepared=PrepareCallers(request,method);
        std::lock_guard lock(gate);
        if(hasActive&&(selected.module!=method.module||selected.token!=method.token))
            throw Failure("mutation_unverified","Active mutation selects another method or module load.");
        auto result=StatusLocked();
        result["selected_method_scope"]=method.mvid+"/"+MethodToken(method.token)+"/"+method.moduleHash;
        result["original_il_hash"]=Sha(method.original); result["module_load_identity"]=Hex(method.module);
        if(!hasActive) {
            result["current_body_hash"]=Sha(method.original);
            for(const auto& caller:prepared) result["affected_callers"].push_back(json{{"binding",Inspector::Binding(caller.method)},
                {"rejit_id","0"},{"compilation_observed",false},{"restore_api_succeeded",false}});
        }
        return result;
    }
    CilBody ValidateBody(const Inspector::Method& method,const std::vector<unsigned char>& body,unsigned enter=0,unsigned exit=0) {
        unsigned locals=body.size()>=12&&(body[0]&3)==3?Read32(body.data()+8):0;
        return ValidateCil(body,CilArguments(method.signature),method.token,
            Inspector(info,observedCodeEpoch).LocalCount(method.module,locals),locals,enter,exit);
    }
    void VerifyProbeBody(const json& request,const Inspector::Method& method,const std::vector<unsigned char>& body) {
        std::vector<unsigned char> original=method.original;
        {
            std::lock_guard lock(gate);
            if(hasActive&&!restoring) original.assign(candidate.begin(),candidate.begin()+candidateLength);
        }
        auto parsed=ValidateBody(method,original);
        auto enter=request.value("enter_token",0U),exit=request.value("exit_token",0U),exceptional=request.value("exceptional_exit_token",0U);
        if(request.value("probe_id",999U)!=0) throw Failure("resource_limit","Initial selected probe uses slot zero.");
        Inspector inspector(info,observedCodeEpoch);
        inspector.VerifyProbeHelpers(method.module,request.value("helper_hash",""),enter,exit,exceptional);
        if(exceptional!=0) {
            unsigned localSignature=request.value("probe_local_signature",0U),returnLocal=request.value("return_local",999U);
            unsigned count=inspector.LocalCount(method.module,localSignature),originalCount=inspector.LocalCount(method.module,parsed.locals);
            if(returnLocal!=originalCount||count<=returnLocal||count!=request.value("probe_local_count",0U))
                throw Failure("invalid_cil","Fault probe return local differs from existing target metadata.");
            auto expected=FaultProbeBody(original,parsed,localSignature,returnLocal,enter,exit,exceptional);
            if(body!=expected) throw Failure("invalid_cil","Fault probe differs from the complete certified rewrite, ranges, or metadata bindings.");
            return;
        }
        for(const auto& row:parsed.rows) if(row.opcode>=0x5b&&row.opcode<=0x5e)
            throw Failure("capability_unsupported","Normal-completion probes exclude throwing arithmetic.");
        std::map<size_t,size_t> offsets; size_t length=10;
        for(const auto& row:parsed.rows) { offsets[row.offset]=length; length+=(row.returns?10:0)+(row.target>=0?5:row.length); }
        unsigned stack=std::max(2U,parsed.maxStack+1);
        if(body.size()!=12+length||body.size()>4096||body[0]!=(parsed.init?0x13:3)||body[1]!=0x30||
            (body[2]|(static_cast<unsigned>(body[3])<<8))!=stack||Read32(body.data()+4)!=length||Read32(body.data()+8)!=parsed.locals)
            throw Failure("invalid_cil","Selected probe header or length differs from the certified rewrite.");
        std::vector<unsigned char> expected;
        auto helper=[&](unsigned token) { expected.push_back(0x20); for(int i=0;i<4;i++) expected.push_back(0);
            expected.push_back(0x28); for(int i=0;i<4;i++) expected.push_back(static_cast<unsigned char>(token>>(8*i))); };
        helper(enter);
        for(const auto& row:parsed.rows) {
            if(row.returns) helper(exit);
            if(row.target>=0) {
                unsigned op=row.opcode<=0x37?row.opcode+0x0d:row.opcode;
                int displacement=static_cast<int>(offsets.at(static_cast<size_t>(row.target)))-static_cast<int>(expected.size()+5);
                expected.push_back(static_cast<unsigned char>(op));
                for(int i=0;i<4;i++) expected.push_back(static_cast<unsigned char>(static_cast<unsigned>(displacement)>>(8*i)));
            } else expected.insert(expected.end(),original.begin()+parsed.offset+row.offset,original.begin()+parsed.offset+row.offset+row.length);
        }
        if(!std::equal(expected.begin(),expected.end(),body.begin()+12))
            throw Failure("invalid_cil","Probe must preserve the complete approved base expression and balanced helper exits.");
        ValidateBody(method,body,enter,exit);
    }
    json Preflight(const json& request,const Inspector::Method& method,const std::vector<unsigned char>& body) {
        bool probe=request.value("probe",false);
        if(probe) VerifyProbeBody(request,method,body); else ValidateBody(method,body);
        if(!(method.attributes&mdStatic)||!method.function||method.name==".cctor")
            throw Failure("capability_unsupported","Live mutation requires a compiled nongeneric static Int32 method.");
        ValidateBody(method,method.original);
        auto prepared=PrepareCallers(request,method);
        std::lock_guard lock(gate);
        if(mutationTainted.load()||mutationPending.load()) throw Failure("target_tainted","Previous mutation is unresolved.");
        budget.ObserveMemory(MutationPrivateCommit(),true); budget.RequireCapacity(mutations);
        if(hasActive&&(selected.module!=method.module||selected.token!=method.token))
            throw Failure("capability_unsupported","Only one selected method may be active in the feasibility session.");
        std::string current=hasActive&&!restoring?hash:Sha(method.original);
        if(request.value("base_il_hash","")!=current) throw Failure("stale_candidate","Candidate base body differs.");
        return json{{"phase","preflight_passed"},{"body_hash",Sha(body)},{"base_il_hash",current},
            {"original_il_hash",Sha(method.original)},{"module_load_identity",Hex(method.module)},
            {"method_scope",method.mvid+"/"+MethodToken(method.token)+"/"+method.moduleHash},
            {"epoch",std::to_string(executionEpoch.load())},{"inliners",request.value("inliners",json::array())},
            {"inliner_coverage",prepared.empty()?"excluded_or_observed_empty":"startup-observed-and-native-enumerated"}};
    }
    json Apply(const json& request,Inspector::Method method,const std::vector<unsigned char>& body) {
        Preflight(request,method,body);
        if(mutationTainted.load()||mutationPending.load()) throw Failure("target_tainted","Previous mutation is unresolved.");
        std::string next=request.value("transaction_id","");
        if(next.empty()||next.size()>100) throw Failure("invalid_request","Bounded transaction identity required.");
        auto prepared=PrepareCallers(request,method);
        std::array<ModuleID,32> modules{}; std::array<mdMethodDef,32> tokens{};
        modules[0]=method.module; tokens[0]=method.token; ULONG count=1;
        for(const auto& item:prepared) { modules[count]=item.method.module; tokens[count++]=item.method.token; }
        {
            std::lock_guard lock(gate);
            budget.ObserveMemory(MutationPrivateCommit(),true); budget.RequireCapacity(mutations);
            if(hasActive&&(selected.module!=method.module||selected.token!=method.token))
                throw Failure("capability_unsupported","Only one selected method may be active in the feasibility session.");
            std::string current=hasActive&&!restoring?hash:Sha(method.original);
            if(request.value("base_il_hash","")!=current) throw Failure("stale_candidate","Candidate base body differs.");
            selected=std::move(method); candidateLength=static_cast<ULONG>(body.size());
            callers=std::move(prepared); callerBindings=request.value("inliners",json::array());
            mode=request.value("probe",false)?"selected_probe":Sha(body)==Sha(selected.original)?"original_body_rejit":"candidate_rejit";
            std::copy(body.begin(),body.end(),candidate.begin()); hash=Sha(body); baseHash=Sha(selected.original);
            scope=selected.mvid+"/"+MethodToken(selected.token)+"/"+selected.moduleHash;
            activeMethodScope=scope;
            transaction=next; started=finished=0; compilation=S_FALSE; hasActive=true; restoring=false; reverted=false; mutations++;
            versionHashes[mutations-1]=hash;
            versionBodies[mutations-1]=candidate; versionLengths[mutations-1]=candidateLength;
            setupObserved.store(false); setupCorrect.store(false); mutationPending.store(true); executionEpoch.fetch_add(1);
        }
        HRESULT result=info->RequestReJITWithInliners(COR_PRF_REJIT_BLOCK_INLINING|COR_PRF_REJIT_INLINING_CALLBACKS,count,modules.data(),tokens.data());
        if(FAILED(result)) { mutationTainted.store(true); Check(result,"RequestReJITWithInliners"); }
        return Status();
    }
    HRESULT Parameters(ModuleID module,mdMethodDef token,ICorProfilerFunctionControl* control) noexcept {
        std::array<unsigned char,4096> bytes{}; ULONG length=0;
        {
            std::lock_guard lock(gate);
            if(!hasActive||restoring) return S_OK;
            if(module==selected.module&&token==selected.token) { bytes=candidate; length=candidateLength; }
            else {
                bool found=false;
                for(const auto& caller:callers) if(module==caller.method.module&&token==caller.method.token)
                { bytes=caller.body; length=caller.length; found=true; break; }
                if(!found) { mutationTainted.store(true); return E_FAIL; }
            }
        }
        HRESULT result=control->SetILFunctionBody(length,bytes.data());
        if(FAILED(result)) mutationTainted.store(true);
        return result;
    }
    HRESULT Started(FunctionID function,ReJITID id) noexcept {
        std::lock_guard lock(gate);
        if(!hasActive||restoring) return S_OK;
        if(id==0) { mutationTainted.store(true); return S_OK; }
        if(Prior(function,id)) { observedCodeEpoch.fetch_add(1); return S_OK; }
        if(function!=selected.function) {
            for(auto& caller:callers) if(function==caller.method.function) { caller.started=id; observedCodeEpoch.fetch_add(1); return S_OK; }
            mutationTainted.store(true); return S_OK;
        }
        for(size_t index=0;index+1<mutations;index++) if(versionIds[index]==id) { observedCodeEpoch.fetch_add(1); return S_OK; }
        started=id; observedCodeEpoch.fetch_add(1); return S_OK;
    }
    HRESULT Finished(FunctionID function,ReJITID id,HRESULT result) noexcept {
        std::lock_guard lock(gate);
        if(!hasActive||restoring) return S_OK;
        if(Prior(function,id)) { observedCodeEpoch.fetch_add(1); return S_OK; }
        if(function!=selected.function) {
            for(auto& caller:callers) if(function==caller.method.function) {
                if(id==0||id!=caller.started||FAILED(result)) { mutationTainted.store(true); return S_OK; }
                caller.finished=id; caller.compilation=result;
                if(priorCount==priorCompilations.size()) mutationTainted.store(true); else priorCompilations[priorCount++]={function,id};
                observedCodeEpoch.fetch_add(1); return S_OK;
            }
            mutationTainted.store(true); return S_OK;
        }
        for(size_t index=0;index+1<mutations;index++) if(versionIds[index]==id) { observedCodeEpoch.fetch_add(1); return S_OK; }
        if(function!=selected.function||id!=started||id==0||FAILED(result)) { mutationTainted.store(true); return S_OK; }
        finished=id; compilation=result; versionIds[mutations-1]=id; observedCodeEpoch.fetch_add(1); return S_OK;
    }
    HRESULT Error() noexcept { mutationTainted.store(true); return S_OK; }
    json Verify(const json& request) {
        Inspector::Method method;
        std::vector<std::pair<FunctionID,ReJITID>> compiled;
        { std::lock_guard lock(gate); method=selected;
            if(!restoring&&finished!=0&&compilation==S_OK) {
                compiled.emplace_back(selected.function,finished);
                for(const auto& caller:callers) if(caller.finished!=0&&caller.compilation==S_OK) compiled.emplace_back(caller.method.function,caller.finished);
            }
        }
        PrepareCallers(request,method);
        try {
            Inspector inspector(info,observedCodeEpoch);
            for(const auto& [function,version]:compiled) for(const auto& [start,bytes]:inspector.NativeExtents(function,version)) {
                std::lock_guard lock(gate); budget.ObserveRegion(function,version,start,bytes);
            }
        } catch(...) { std::lock_guard lock(gate); budget.AccountingFailed(); throw; }
        {
            std::lock_guard lock(gate);
            if(request.value("transaction_id","")!=transaction||mutationTainted.load()||!setupObserved.load()||!setupCorrect.load()||
                (!restoring&&(finished==0||compilation!=S_OK))||(restoring&&!reverted))
                throw Failure("mutation_unverified","Compilation and application-owned correctness are required.");
            for(const auto& caller:callers)
                if((!restoring&&(caller.finished==0||caller.compilation!=S_OK))||(restoring&&FAILED(caller.restoreStatus)))
                    throw Failure("mutation_unverified","Every affected caller requires compilation or per-method restoration evidence.");
            budget.ObserveMemory(MutationPrivateCommit(),!restoring);
            mutationPending.store(false);
        }
        return Status();
    }
    json Restore(const json& request) {
        std::array<ModuleID,32> modules{}; std::array<mdMethodDef,32> tokens{}; ULONG count=1;
        {
            std::lock_guard lock(gate);
            if(!hasActive||request.value("transaction_id","")!=transaction) throw Failure("mutation_unverified","Active transaction differs.");
            modules[0]=selected.module; tokens[0]=selected.token;
            for(const auto& caller:callers) { modules[count]=caller.method.module; tokens[count++]=caller.method.token; }
            restoring=true; mutationPending.store(true);
            setupObserved.store(false); setupCorrect.store(false); executionEpoch.fetch_add(1);
        }
        std::array<HRESULT,32> statuses; statuses.fill(E_FAIL);
        HRESULT aggregate=info->RequestRevert(count,modules.data(),tokens.data(),statuses.data());
        { std::lock_guard lock(gate); for(size_t i=0;i<callers.size();i++) callers[i].restoreStatus=statuses[i+1]; }
        if(FAILED(aggregate)||std::any_of(statuses.begin(),statuses.begin()+count,[](HRESULT value) { return FAILED(value); }))
        { mutationTainted.store(true); throw Failure("restore_failed","Original-version revert failed for an affected method."); }
        { std::lock_guard lock(gate); reverted=true; }
        return Status();
    }
    void Label(json& snapshot) {
        std::lock_guard lock(gate);
        for(auto& version:snapshot["snapshots"]) {
            if(version["module_mvid"]!=selected.mvid||version["method_token"]!=MethodToken(selected.token)) continue;
            ReJITID id=static_cast<ReJITID>(std::stoull(version["rejit_id"].get<std::string>()));
            for(size_t index=0;index<mutations;index++) if(id!=0&&versionIds[index]==id) {
                version["current_il_hash"]=versionHashes[index];
                version["current_il_body"]=Base64(std::vector<unsigned char>(versionBodies[index].begin(),versionBodies[index].begin()+versionLengths[index]));
            }
        }
    }
};
