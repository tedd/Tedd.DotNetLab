#pragma once
#include "Failure.h"
#include <array>
#include <cstdint>
#include <mutex>
#include <set>
#include <vector>

class InliningGraph {
    using Function=std::uintptr_t;
    struct Edge { Function caller=0,callee=0; };
    std::array<Edge,65536> edges{};
    std::mutex gate;
    size_t count=0; bool overflow=false;
public:
    void Observe(Function caller,Function callee) noexcept {
        std::lock_guard lock(gate);
        if(count==edges.size()) { overflow=true; return; }
        edges[count++]={caller,callee};
    }
    std::vector<Function> Closure(Function callee) {
        std::lock_guard lock(gate);
        if(overflow) throw Failure("inliner_coverage_unknown","Startup inlining observation budget overflowed.");
        std::set<Function> affected{callee}; bool changed=true;
        while(changed) {
            changed=false;
            for(size_t i=0;i<count;i++) if(affected.contains(edges[i].callee)&&!affected.contains(edges[i].caller)) {
                if(affected.size()==32) throw Failure("resource_limit","Affected caller closure exceeds 31 callers.");
                affected.insert(edges[i].caller); changed=true;
            }
        }
        affected.erase(callee); return {affected.begin(),affected.end()};
    }
};
