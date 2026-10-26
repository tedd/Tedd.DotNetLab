#pragma once
#include "InliningGraph.h"

class Inlining : public InliningGraph {
public:
    std::vector<Inspector::Method> Resolve(ICorProfilerInfo12* info,Inspector& inspector,const Inspector::Method& selected) {
        std::vector<Inspector::Method> methods;
        if(selected.implementation&miNoInlining) return methods;
        std::set<FunctionID> included{selected.function};
        auto add=[&](Inspector::Method method) {
            if(included.contains(method.function)) return;
            if(methods.size()==31) throw Failure("resource_limit","Affected caller closure exceeds its method budget.");
            included.insert(method.function); methods.push_back(std::move(method));
        };
        for(auto function:Closure(selected.function)) add(inspector.FromFunction(function));
        auto nativeModules=inspector.PrecompiledModules();
        for(size_t position=0;position<=methods.size();position++) {
            const auto method=position==0?selected:methods[position-1];
            for(auto module:nativeModules) {
                BOOL incomplete=FALSE; Com<ICorProfilerMethodEnum> enumeration;
                Check(info->EnumNgenModuleMethodsInliningThisMethod(module,method.module,method.token,&incomplete,&enumeration.value),"NativeInliners");
                if(incomplete) throw Failure("inliner_coverage_unknown","Precompiled inliner enumeration is incomplete.");
                if(!enumeration.value) throw Failure("inliner_coverage_unknown","Precompiled inliner enumeration is unavailable.");
                for(;;) {
                    COR_PRF_METHOD caller{}; ULONG count=0;
                    Check(enumeration->Next(1,&caller,&count),"NativeInliners.Next");
                    if(count==0) break;
                    add(inspector.FromToken(caller.moduleId,caller.methodId));
                }
            }
            for(auto function:Closure(method.function)) add(inspector.FromFunction(function));
        }
        std::sort(methods.begin(),methods.end(),[](const auto& a,const auto& b) { return Inspector::Binding(a).dump()<Inspector::Binding(b).dump(); });
        return methods;
    }
};
static json InlinerBindings(const std::vector<Inspector::Method>& methods) {
    json result=json::array(); for(const auto& method:methods) result.push_back(Inspector::Binding(method)); return result;
}
static std::string MutationGrantHash(const json& request,const std::vector<unsigned char>& body) {
    auto inliners=request.value("inliners",json::array());
    if(!inliners.is_array()||inliners.size()>31) throw Failure("resource_limit","Inliner manifest exceeds its bounds.");
    if(inliners.empty()) return Sha(body);
    std::string canonical=json{{"body_hash",Sha(body)},{"inliners",inliners}}.dump();
    return Sha(std::vector<unsigned char>(canonical.begin(),canonical.end()));
}
