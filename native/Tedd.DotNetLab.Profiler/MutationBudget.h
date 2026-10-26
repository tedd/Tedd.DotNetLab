#pragma once
#include <array>
#include <cstdint>
#include <optional>
#include "Failure.h"

// Resource accounting runs on the control worker, never inside JIT callbacks.
class MutationBudget {
    struct Region { std::uintptr_t function=0,version=0,start=0; std::uint64_t bytes=0; };
    std::array<Region,8192> regions{};
    size_t regionCount=0;
    std::uint64_t nativeBytes=0,privateBaseline=0,privatePeak=0;
    bool initialized=false,memoryAvailable=false,restart=false;
    const bool requireMemory;
    const std::uint64_t nativeLimit,privateLimit;
    std::string reason;
    void Stop(const char* cause,bool enforce) {
        restart=true; if(reason.empty()) reason=cause;
        if(enforce) throw Failure("resource_limit","Mutation resource budget requires a fresh process.");
    }
public:
    static constexpr unsigned MaximumCandidates=32;
    static constexpr std::uint64_t MaximumNativeBytes=8*1024*1024,MaximumPrivateGrowth=128*1024*1024;
    explicit MutationBudget(bool requireMemory,std::uint64_t nativeLimit=MaximumNativeBytes,std::uint64_t privateLimit=MaximumPrivateGrowth):
        requireMemory(requireMemory),nativeLimit(nativeLimit),privateLimit(privateLimit) {
        if(nativeLimit==0||nativeLimit>MaximumNativeBytes||privateLimit==0||privateLimit>MaximumPrivateGrowth)
            throw Failure("invalid_request","Mutation resource limits exceed policy.");
    }
    void ObserveMemory(std::optional<std::uint64_t> bytes,bool enforce) {
        memoryAvailable=bytes.has_value();
        if(!bytes) { if(requireMemory) Stop("private_commit_unavailable",enforce); return; }
        if(!initialized) { privateBaseline=privatePeak=*bytes; initialized=true; }
        if(*bytes>privatePeak) privatePeak=*bytes;
        if(PrivateGrowth()>=privateLimit) Stop("private_commit_growth",enforce&&PrivateGrowth()>privateLimit);
    }
    void RequireCapacity(unsigned candidates) {
        if(candidates>=MaximumCandidates) Stop("candidate_count",false);
        if(restart) throw Failure("resource_limit","Mutation resource budget requires a fresh process.");
    }
    void ObserveRegion(std::uintptr_t function,std::uintptr_t version,std::uintptr_t start,std::uint64_t bytes) {
        if(function==0||version==0||start==0||bytes==0) { Stop("native_extent_unavailable",true); }
        size_t index=0;
        for(;index<regionCount;index++) if(regions[index].function==function&&regions[index].version==version&&regions[index].start==start) break;
        if(index==regionCount) {
            if(regionCount==regions.size()) Stop("native_extent_capacity",true);
            regions[regionCount++]={function,version,start,0};
        }
        if(bytes<=regions[index].bytes) return;
        std::uint64_t delta=bytes-regions[index].bytes;
        if(delta>UINT64_MAX-nativeBytes) Stop("native_extent_overflow",true);
        nativeBytes+=delta; regions[index].bytes=bytes;
        if(nativeBytes>=nativeLimit) Stop("native_code_growth",nativeBytes>nativeLimit);
    }
    void AccountingFailed() { Stop("native_extent_unavailable",false); }
    bool RestartRequired(unsigned candidates) const { return restart||candidates>=MaximumCandidates; }
    std::string Reason(unsigned candidates) const { return !reason.empty()?reason:candidates>=MaximumCandidates?"candidate_count":""; }
    bool MemoryAvailable() const { return memoryAvailable; }
    bool MemoryInitialized() const { return initialized; }
    std::uint64_t PrivateBaseline() const { return privateBaseline; }
    std::uint64_t PrivatePeak() const { return privatePeak; }
    std::uint64_t PrivateGrowth() const { return privatePeak-privateBaseline; }
    std::uint64_t NativeBytes() const { return nativeBytes; }
    size_t RegionCount() const { return regionCount; }
    std::uint64_t NativeLimit() const { return nativeLimit; }
    std::uint64_t PrivateLimit() const { return privateLimit; }
};
