#include "../Tedd.DotNetLab.Profiler/MutationBudget.h"
#include <memory>
#include <iostream>

static void Require(bool value) { if(!value) throw std::runtime_error("Mutation budget contract failed."); }
template<class F> static void Reject(F action) {
    try { action(); } catch(const Failure& error) { Require(error.code=="resource_limit"); return; }
    throw std::runtime_error("Mutation resource bound was bypassed.");
}
int main() {
    try {
        auto budget=std::make_unique<MutationBudget>(true,100,1000);
        budget->ObserveMemory(1000,true); budget->RequireCapacity(31);
        budget->ObserveMemory(1400,true); budget->ObserveMemory(1200,true); Require(budget->PrivateGrowth()==400);
        budget->ObserveRegion(1,1,100,10); budget->ObserveRegion(1,1,100,10); budget->ObserveRegion(1,1,100,15);
        budget->ObserveRegion(1,2,100,20); budget->ObserveRegion(2,1,100,30);
        Require(budget->NativeBytes()==65&&budget->RegionCount()==3);
        Reject([&] { budget->ObserveRegion(3,1,100,36); }); Require(budget->RestartRequired(0)); Reject([&] { budget->RequireCapacity(0); });
        auto memory=std::make_unique<MutationBudget>(true,100,1000);
        memory->ObserveMemory(1000,true); Reject([&] { memory->ObserveMemory(2001,true); });
        Require(memory->PrivateGrowth()==1001&&memory->Reason(0)=="private_commit_growth");
        memory->ObserveMemory(1000,false); Require(memory->RestartRequired(0));
        auto unavailable=std::make_unique<MutationBudget>(true); Reject([&] { unavailable->ObserveMemory(std::nullopt,true); });
        auto count=std::make_unique<MutationBudget>(false); count->ObserveMemory(std::nullopt,true); Reject([&] { count->RequireCapacity(32); });
        Require(count->Reason(32)=="candidate_count");
        auto edge=std::make_unique<MutationBudget>(true,100,1000); edge->ObserveMemory(1000,true); edge->ObserveMemory(2000,true);
        Reject([&] { edge->RequireCapacity(0); });
        auto overflow=std::make_unique<MutationBudget>(false); Reject([&] { overflow->ObserveRegion(1,1,1,UINT64_MAX); });
        Reject([&] { overflow->ObserveRegion(1,2,2,UINT64_MAX); });
        std::cout<<"Candidate, native-region, private-commit, identity/deduplication, overflow, and restart bounds passed.\n"; return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
