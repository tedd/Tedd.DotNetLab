#include "../Tedd.DotNetLab.Profiler/InliningGraph.h"
#include <iostream>
#include <memory>

static void Require(bool condition) { if(!condition) throw std::runtime_error("Inlining graph contract failed."); }
template<class F> static void Reject(const char* code,F action) {
    try { action(); } catch(const Failure& error) { Require(error.code==code); return; }
    throw std::runtime_error("Unbounded inliner closure was accepted.");
}
int main() {
    try {
        auto graph=std::make_unique<InliningGraph>();
        graph->Observe(2,1); graph->Observe(3,2); graph->Observe(4,1); graph->Observe(2,1); graph->Observe(1,3); graph->Observe(9,8);
        Require(graph->Closure(1)==std::vector<std::uintptr_t>{2,3,4}); Require(graph->Closure(7).empty());
        auto capped=std::make_unique<InliningGraph>();
        for(std::uintptr_t i=2;i<34;i++) capped->Observe(i,1);
        Reject("resource_limit",[&] { capped->Closure(1); });
        auto overflow=std::make_unique<InliningGraph>();
        for(size_t i=0;i<=65536;i++) overflow->Observe(2,1);
        Reject("inliner_coverage_unknown",[&] { overflow->Closure(1); });
        std::cout<<"Transitive/cyclic caller closure, method limits, and observer overflow passed.\n"; return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
