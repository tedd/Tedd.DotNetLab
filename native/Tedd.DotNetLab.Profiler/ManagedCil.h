#pragma once
#include "Cil.h"
#include "ManagedOpcodes.h"
#include <functional>
#include <map>
#include <set>

static bool ManagedOpcodeAllowed(unsigned op) {
    switch(op) {
    case 0x27: case 0x29: case 0xc2: case 0xc3: case 0xc6: case 0xdc: case 0xdd: case 0xde:
    case 0xfe00: case 0xfe0f: case 0xfe11: case 0xfe14: case 0xfe17: case 0xfe18: case 0xfe19: case 0xfe1a: case 0xfe1d: return false;
    default: return true;
    }
}
struct ManagedSignatureShape { unsigned arguments=0; bool instance=false,returns=false; };
static unsigned ManagedCompressed(const unsigned char*& cursor,const unsigned char* end) {
    if(cursor==end) throw Failure("invalid_cil","Truncated signature.");
    unsigned value=*cursor++;
    if((value&0x80)==0) return value;
    unsigned more=(value&0xc0)==0x80?1:(value&0xe0)==0xc0?3:99;
    if(more==99||static_cast<size_t>(end-cursor)<more) throw Failure("invalid_cil","Invalid compressed signature integer.");
    value&=more==1?0x3f:0x1f;
    while(more--) value=(value<<8)|*cursor++;
    return value;
}
static void ManagedType(const unsigned char*& cursor,const unsigned char* end,unsigned depth=0) {
    if(cursor==end||depth>32) throw Failure("invalid_cil","Truncated or excessively nested type signature.");
    unsigned type=*cursor++;
    if((type>=1&&type<=0x0e)||type==0x18||type==0x19||type==0x1c) return;
    if(type==0x11||type==0x12||type==0x13||type==0x1e) { ManagedCompressed(cursor,end); return; }
    if(type==0x10||type==0x1d) { ManagedType(cursor,end,depth+1); return; }
    if(type==0x1f||type==0x20) { ManagedCompressed(cursor,end); ManagedType(cursor,end,depth+1); return; }
    if(type==0x15) {
        if(cursor==end||(*cursor!=0x11&&*cursor!=0x12)) throw Failure("invalid_cil","Invalid generic type signature.");
        cursor++; ManagedCompressed(cursor,end); unsigned count=ManagedCompressed(cursor,end);
        if(count>64) throw Failure("resource_limit","Generic type argument count exceeds policy.");
        while(count--) ManagedType(cursor,end,depth+1);
        return;
    }
    if(type==0x14) {
        ManagedType(cursor,end,depth+1); unsigned rank=ManagedCompressed(cursor,end),sizes=ManagedCompressed(cursor,end);
        if(rank>32||sizes>rank) throw Failure("invalid_cil","Invalid array signature.");
        while(sizes--) ManagedCompressed(cursor,end);
        unsigned bounds=ManagedCompressed(cursor,end); if(bounds>rank) throw Failure("invalid_cil","Invalid array lower bounds.");
        while(bounds--) ManagedCompressed(cursor,end);
        return;
    }
    throw Failure("capability_unsupported","Unmanaged pointer, pinned, function-pointer, or unsupported type signature.");
}
static ManagedSignatureShape ManagedSignature(const unsigned char* signature,size_t length) {
    if(length<3||length>4096) throw Failure("invalid_cil","Invalid managed signature length.");
    auto cursor=signature; auto end=signature+length; unsigned flags=*cursor++;
    if((flags&0x0f)!=0||(flags&0xc0)!=0) throw Failure("capability_unsupported","Only default managed calling conventions are supported.");
    if(flags&0x10) ManagedCompressed(cursor,end);
    ManagedSignatureShape result; result.instance=(flags&0x20)!=0; result.arguments=ManagedCompressed(cursor,end);
    if(result.arguments>64||cursor==end) throw Failure("resource_limit","Managed argument count exceeds policy.");
    result.returns=*cursor!=1; ManagedType(cursor,end);
    for(unsigned index=0;index<result.arguments;index++) ManagedType(cursor,end);
    if(cursor!=end) throw Failure("invalid_cil","Trailing managed signature bytes.");
    return result;
}
static unsigned ManagedLocals(const unsigned char* signature,size_t length) {
    if(length<2||length>4096||signature[0]!=7) throw Failure("invalid_cil","Invalid local signature.");
    auto cursor=signature+1; auto end=signature+length; unsigned count=ManagedCompressed(cursor,end);
    if(count>64) throw Failure("resource_limit","Managed local count exceeds policy.");
    for(unsigned index=0;index<count;index++) ManagedType(cursor,end);
    if(cursor!=end) throw Failure("invalid_cil","Trailing local signature bytes.");
    return count;
}
// Metadata is resolved independently in the profiler; the controller cannot
// supply call stack effects or a substitute target signature.
static CilBody ValidateManagedCil(const std::vector<unsigned char>& body,unsigned arguments,unsigned locals,bool returns,
    const std::function<void(unsigned,int)>& tokenCheck,const std::function<ManagedSignatureShape(unsigned)>& callShape) {
    if(body.size()<2||body.size()>4096||arguments>64||locals>64) throw Failure("resource_limit","Managed body exceeds policy.");
    CilBody result;
    if((body[0]&3)==2) { result.offset=1; result.size=body[0]>>2; result.maxStack=8; }
    else if(body.size()>=13&&(body[0]==3||body[0]==0x13)&&body[1]==0x30) {
        result.offset=12; result.size=Read32(body.data()+4); result.maxStack=body[2]|(static_cast<unsigned>(body[3])<<8);
        result.locals=Read32(body.data()+8); result.init=(body[0]&0x10)!=0;
    } else throw Failure("capability_unsupported","Managed bodies require tiny/fat headers without exception sections.");
    if(result.size==0||result.size!=body.size()-result.offset||result.maxStack==0||result.maxStack>1024||
        (result.locals!=0&&(result.locals>>24)!=0x11)) throw Failure("invalid_cil","Invalid managed header.");
    struct Row { size_t offset,length; int pop,push; std::vector<size_t> targets; bool fallthrough,terminal,prefix; };
    std::vector<Row> rows; std::map<size_t,size_t> positions; std::set<size_t> prefixInteriors;
    const auto* code=body.data()+result.offset; size_t offset=0;
    while(offset<result.size) {
        size_t start=offset; unsigned op=code[offset++];
        if(op==0xfe) { if(offset==result.size) throw Failure("invalid_cil","Truncated opcode."); op=0xfe00|code[offset++]; }
        const ManagedOpcode* definition=nullptr;
        for(const auto& entry:ManagedOpcodes) if(entry.code==op) { definition=&entry; break; }
        if(!definition||!ManagedOpcodeAllowed(op)) throw Failure("capability_unsupported","Unsupported managed opcode.");
        const auto& d=*definition; size_t length=d.operand>=0?static_cast<size_t>(d.operand):0;
        if(d.operand==-2) {
            if(result.size-offset<4) throw Failure("invalid_cil","Truncated switch.");
            unsigned count=Read32(code+offset); if(count>10000) throw Failure("resource_limit","Switch exceeds policy."); length=4+count*4;
        }
        if(length>result.size-offset) throw Failure("invalid_cil","Truncated operand.");
        size_t operand=offset; offset+=length; Row row{start,offset-start,d.pop,d.push,{},!d.terminal&&(!d.branch||d.conditional),d.terminal,d.prefix};
        int index=-1; bool argument=false;
        if(op>=2&&op<=5) { index=static_cast<int>(op-2); argument=true; }
        else if(op>=6&&op<=9) index=static_cast<int>(op-6);
        else if(op>=10&&op<=13) index=static_cast<int>(op-10);
        else if(op>=0x0e&&op<=0x13) { index=code[operand]; argument=op<=0x10; }
        else if(op>=0xfe09&&op<=0xfe0e) { index=code[operand]|(code[operand+1]<<8); argument=op<=0xfe0b; }
        if(index>=0&&static_cast<unsigned>(index)>=(argument?arguments:locals)) throw Failure("invalid_cil","Argument/local index exceeds signature.");
        if(d.token) {
            unsigned token=Read32(code+operand); tokenCheck(token,d.token);
            if(op==0x28||op==0x6f||op==0x73) {
                auto call=callShape(token); row.pop=static_cast<int>(call.arguments)+(op!=0x73&&call.instance?1:0);
                row.push=op==0x73||call.returns?1:0;
            }
        }
        if(op==0x2a) { row.pop=returns?1:0; row.push=0; }
        if(row.pop<0||row.push<0) throw Failure("invalid_cil","Unsupported variable stack behavior.");
        auto target=[&](int displacement) {
            auto destination=static_cast<long long>(offset)+displacement;
            if(destination<0||destination>=static_cast<long long>(result.size)) throw Failure("invalid_cil","Branch exceeds body.");
            row.targets.push_back(static_cast<size_t>(destination));
        };
        if(d.branch) {
            if(d.operand==-2) { for(size_t item=operand+4;item<offset;item+=4) target(static_cast<int>(Read32(code+item))); }
            else target(length==1?static_cast<signed char>(code[operand]):static_cast<int>(Read32(code+operand)));
        }
        if(!rows.empty()&&rows.back().prefix) prefixInteriors.insert(start);
        if(d.prefix&&offset==result.size) throw Failure("invalid_cil","Dangling prefix.");
        positions.emplace(start,rows.size()); rows.push_back(std::move(row));
        if(rows.size()>10000) throw Failure("resource_limit","Instruction count exceeds policy.");
    }
    for(const auto& row:rows) for(auto target:row.targets) if(!positions.contains(target)||prefixInteriors.contains(target))
        throw Failure("invalid_cil","Branch targets an operand or prefix interior.");
    std::map<size_t,int> depths; std::queue<std::pair<size_t,int>> pending; pending.push({0,0});
    while(!pending.empty()) {
        auto [position,depth]=pending.front(); pending.pop();
        if(depths.contains(position)) { if(depths.at(position)!=depth) throw Failure("invalid_cil","Stack merge differs."); continue; }
        depths.emplace(position,depth); const auto& row=rows.at(positions.at(position));
        if(depth<row.pop) throw Failure("invalid_cil","Stack underflow.");
        int next=depth-row.pop+row.push;
        if(next>static_cast<int>(result.maxStack)||(row.terminal&&next!=0)) throw Failure("invalid_cil","Invalid terminal stack or maxstack.");
        for(auto destination:row.targets) pending.push({destination,next});
        if(row.fallthrough) {
            size_t destination=row.offset+row.length;
            if(destination>=result.size) throw Failure("invalid_cil","Control flow falls through method end."); pending.push({destination,next});
        }
    }
    if(depths.size()!=rows.size()) throw Failure("invalid_cil","Unreachable managed instructions are unsupported.");
    return result;
}
