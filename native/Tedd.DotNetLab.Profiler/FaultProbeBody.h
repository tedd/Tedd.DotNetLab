#pragma once
#include "Cil.h"
#include <map>

// This template is reachable only through the approved instrumentation path.
// Arbitrary candidates retain the exception-free validator.
static std::vector<unsigned char> FaultProbeBody(const std::vector<unsigned char>& original,const CilBody& parsed,
    unsigned localSignature,unsigned returnLocal,unsigned enter,unsigned exit,unsigned exceptional) {
    if(returnLocal>=64||parsed.maxStack>=1024) throw Failure("resource_limit","Fault probe local/stack budget exceeded.");
    std::map<size_t,size_t> offsets; size_t handler=10;
    for(const auto& row:parsed.rows) { offsets[row.offset]=handler; handler+=row.returns?7:row.target>=0?5:row.length; }
    size_t epilogue=handler+11,length=epilogue+13,section=(12+length+3)&~size_t(3);
    if(section+28>4096) throw Failure("resource_limit","Fault probe exceeds the native body budget.");
    std::vector<unsigned char> body(12,0); body[0]=0x1b; body[1]=0x30;
    unsigned stack=std::max(2U,parsed.maxStack+1); body[2]=static_cast<unsigned char>(stack); body[3]=static_cast<unsigned char>(stack>>8);
    for(int i=0;i<4;i++) { body[4+i]=static_cast<unsigned char>(length>>(8*i)); body[8+i]=static_cast<unsigned char>(localSignature>>(8*i)); }
    auto word=[&](unsigned value) { for(int i=0;i<4;i++) body.push_back(static_cast<unsigned char>(value>>(8*i))); };
    auto helper=[&](unsigned token) { body.push_back(0x20); word(0); body.push_back(0x28); word(token); };
    auto branch=[&](unsigned char op,size_t target) {
        auto displacement=static_cast<int>(target)-static_cast<int>(body.size()-12+5);
        body.push_back(op); word(static_cast<unsigned>(displacement));
    };
    helper(enter);
    for(const auto& row:parsed.rows) {
        if(row.returns) { body.push_back(0x13); body.push_back(static_cast<unsigned char>(returnLocal)); branch(0xdd,epilogue); }
        else if(row.target>=0) branch(row.opcode<=0x37?static_cast<unsigned char>(row.opcode+0x0d):row.opcode,offsets.at(static_cast<size_t>(row.target)));
        else body.insert(body.end(),original.begin()+parsed.offset+row.offset,original.begin()+parsed.offset+row.offset+row.length);
    }
    helper(exceptional); body.push_back(0xdc);
    helper(exit); body.push_back(0x11); body.push_back(static_cast<unsigned char>(returnLocal)); body.push_back(0x2a);
    body.resize(section,0); body.push_back(0x41); body.push_back(28); body.push_back(0); body.push_back(0);
    word(4); word(10); word(static_cast<unsigned>(handler-10)); word(static_cast<unsigned>(handler)); word(11); word(0);
    return body;
}
