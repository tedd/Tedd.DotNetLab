#pragma once
#include "Failure.h"
#include <algorithm>
#include <queue>
#include <unordered_map>
#include <cstring>
#include <vector>
struct CilRow {
    size_t offset=0,length=0; unsigned char opcode=0; int pop=0,push=0,target=-1;
    bool conditional=false,returns=false; unsigned call=0;
};
struct CilBody {
    size_t offset=0,size=0; unsigned maxStack=0,locals=0; bool init=false;
    std::vector<CilRow> rows;
};
static unsigned Read32(const unsigned char* bytes) { unsigned value=0; memcpy(&value,bytes,4); return value; }
static unsigned CilArguments(const std::string& signature) {
    if(signature.size()<6||!signature.starts_with("00")||signature.substr(4,2)!="08")
        throw Failure("capability_unsupported","Only default static Int32 signatures are supported.");
    unsigned arguments=static_cast<unsigned>(std::stoul(signature.substr(2,2),nullptr,16));
    if(arguments>4||signature.size()!=6+arguments*2) throw Failure("capability_unsupported","Argument signature exceeds policy.");
    for(unsigned i=0;i<arguments;i++) if(signature.substr(6+i*2,2)!="08") throw Failure("capability_unsupported","Only Int32 arguments are supported.");
    return arguments;
}
static CilBody ValidateCil(const std::vector<unsigned char>& body,unsigned arguments,unsigned self,unsigned localCount,
    unsigned expectedLocals,unsigned enter=0,unsigned exit=0) {
    if(arguments>4||localCount>64||(self>>24)!=0x06||(self&0xffffff)==0||
        (expectedLocals==0&&localCount!=0)||(expectedLocals!=0&&(expectedLocals>>24)!=0x11))
        throw Failure("invalid_cil","Unresolved argument, method, or local policy.");
    if(body.size()<2||body.size()>4096) throw Failure("resource_limit","Native CIL body exceeds 4 KiB policy.");
    CilBody result;
    if((body[0]&3)==2) { result.offset=1; result.size=body[0]>>2; result.maxStack=8; }
    else if((body[0]&3)==3) {
        if(body.size()<12) throw Failure("invalid_cil","Truncated fat header.");
        unsigned flags=body[0]|(static_cast<unsigned>(body[1])<<8);
        if((flags>>12)!=3||(flags&0xfff&~0x13)!=0) throw Failure("capability_unsupported","Unsupported header or exception sections.");
        result.offset=12; result.size=Read32(body.data()+4); result.maxStack=body[2]|(static_cast<unsigned>(body[3])<<8);
        result.locals=Read32(body.data()+8); result.init=(flags&0x10)!=0;
    } else throw Failure("invalid_cil","Invalid CIL header format.");
    if(result.size==0||result.size!=body.size()-result.offset||result.maxStack==0||result.maxStack>1024||
        result.locals!=expectedLocals||(localCount>0&&!result.init)) throw Failure("invalid_cil","CIL header differs from resolved policy.");
    const auto* code=body.data()+result.offset; size_t offset=0;
    std::unordered_map<size_t,size_t> indices;
    while(offset<result.size) {
        CilRow row; row.offset=offset; row.opcode=code[offset++]; unsigned op=row.opcode; size_t operand=0;
        if(op==0) { }
        else if(op>=2&&op<=5) { if(op-2>=arguments) throw Failure("invalid_cil","Argument index exceeds signature."); row.push=1; }
        else if(op>=6&&op<=9) { if(op-6>=localCount) throw Failure("invalid_cil","Local index exceeds signature."); row.push=1; }
        else if(op>=10&&op<=13) { if(op-10>=localCount) throw Failure("invalid_cil","Local index exceeds signature."); row.pop=1; }
        else if(op==0x0e||op==0x11||op==0x13) { operand=1; if(op==0x13) row.pop=1; else row.push=1; }
        else if(op>=0x15&&op<=0x1e) row.push=1;
        else if(op==0x1f||op==0x20) { operand=op==0x1f?1:4; row.push=1; }
        else if(op==0x25) { row.pop=1; row.push=2; }
        else if(op==0x26) row.pop=1;
        else if(op==0x28) operand=4;
        else if(op==0x2a) { row.pop=1; row.returns=true; }
        else if(op>=0x2b&&op<=0x44) {
            operand=op<=0x37?1:4; int kind=static_cast<int>(op)-(op<=0x37?0x2b:0x38);
            row.conditional=kind!=0; row.pop=kind==0?0:kind<=2?1:2;
        } else if(op>=0x58&&op<=0x64) { row.pop=2; row.push=1; }
        else if(op==0x65||op==0x66) { row.pop=1; row.push=1; }
        else throw Failure("capability_unsupported","Instruction/prefix is outside the native Int32 subset.");
        if(operand>result.size-offset) throw Failure("invalid_cil","Truncated operand.");
        if(op==0x0e||op==0x11||op==0x13) {
            if(code[offset]>=(op==0x0e?arguments:localCount)) throw Failure("invalid_cil","Argument/local index exceeds resolved definition.");
        }
        if(op==0x28) {
            row.call=Read32(code+offset);
            if(row.call==self) { row.pop=static_cast<int>(arguments); row.push=1; }
            else if(row.call!=0&&(row.call==enter||row.call==exit)) { row.pop=1; }
            else throw Failure("invalid_cil","Call token is not an approved existing reference.");
        }
        if(op>=0x2b&&op<=0x44) {
            int displacement=operand==1?static_cast<signed char>(code[offset]):static_cast<int>(Read32(code+offset));
            long long target=static_cast<long long>(offset+operand)+displacement;
            if(target<0||target>=static_cast<long long>(result.size)) throw Failure("invalid_cil","Branch exceeds body."); row.target=static_cast<int>(target);
        }
        offset+=operand; row.length=offset-row.offset; indices[row.offset]=result.rows.size(); result.rows.push_back(row);
    }
    for(const auto& row:result.rows) if(row.target>=0&&!indices.contains(static_cast<size_t>(row.target))) throw Failure("invalid_cil","Branch targets an operand.");
    std::vector<int> depths(result.rows.size(),-1); std::queue<std::pair<size_t,int>> pending; pending.push({0,0}); bool returned=false;
    while(!pending.empty()) {
        auto [position,depth]=pending.front(); pending.pop(); auto index=indices.at(position); const auto& row=result.rows[index];
        if(depths[index]>=0) { if(depths[index]!=depth) throw Failure("invalid_cil","Stack merge differs."); continue; }
        depths[index]=depth;
        if(depth<row.pop) throw Failure("invalid_cil","Stack underflow."); int next=depth-row.pop+row.push;
        if(next>static_cast<int>(result.maxStack)) throw Failure("invalid_cil","Maxstack is insufficient.");
        if(row.returns) { if(next!=0) throw Failure("invalid_cil","Return has extra stack values."); returned=true; continue; }
        if(row.target>=0) pending.push({static_cast<size_t>(row.target),next});
        if(row.target<0||row.conditional) {
            size_t nextOffset=row.offset+row.length;
            if(nextOffset>=result.size) throw Failure("invalid_cil","Control flow falls through body end."); pending.push({nextOffset,next});
        }
    }
    if(!returned||std::find(depths.begin(),depths.end(),-1)!=depths.end()) throw Failure("invalid_cil","Unreachable code or missing return.");
    return result;
}
