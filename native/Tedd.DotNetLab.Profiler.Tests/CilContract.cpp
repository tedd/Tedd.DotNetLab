#include "../Tedd.DotNetLab.Profiler/Cil.h"
#include "../Tedd.DotNetLab.Profiler/FaultProbeBody.h"
#include <iostream>

static constexpr unsigned self=0x06000001,locals=0x11000001;
static void Require(bool condition) {
    if(!condition) throw std::runtime_error("Native CIL contract failed.");
}
template<class F> static void Reject(F action) {
    try { action(); }
    catch(const Failure&) { return; }
    throw std::runtime_error("Invalid CIL was accepted.");
}
static std::vector<unsigned char> Tiny(std::initializer_list<unsigned char> code) {
    std::vector<unsigned char> body{static_cast<unsigned char>((code.size()<<2)|2)};
    body.insert(body.end(),code); return body;
}
int main() {
    try {
        Require(CilArguments("000008")==0&&CilArguments("0002080808")==2);
        for(const auto& signature:{"", "000108", "0005080808080808", "100008", "0001080e"})
            Reject([&] { CilArguments(signature); });
        auto recursive=Tiny({0x02,0x16,0x31,0x0b,0x02,0x17,0x59,0x28,1,0,0,6,0x17,0x58,0x2a,0x17,0x2a});
        auto parsed=ValidateCil(recursive,1,self,0,0);
        Require(parsed.rows.size()==12&&parsed.rows[2].target==15&&parsed.rows[6].call==self);
        recursive[9]=2; Reject([&] { ValidateCil(recursive,1,self,0,0); });
        std::vector<unsigned char> localBody{0x13,0x30,1,0,4,0,0,0,1,0,0,0x11,0x17,0x0a,0x06,0x2a};
        Require(ValidateCil(localBody,0,self,1,locals).init);
        localBody[0]=3; Reject([&] { ValidateCil(localBody,0,self,1,locals); });
        localBody[0]=0x13; localBody[14]=0x07; Reject([&] { ValidateCil(localBody,0,self,1,locals); });
        auto helper=Tiny({0x16,0x28,1,0,0,0x0a,0x17,0x2a});
        Reject([&] { ValidateCil(helper,0,self,0,0); });
        Require(ValidateCil(helper,0,self,0,0,0x0a000001,0x0a000002).rows.size()==4);
        for(const auto& bad:{Tiny({0x20,1}),Tiny({0x2a}),Tiny({0x17,0x18,0x2a}),Tiny({0x2b,0xff,0x17,0x2a}),
            Tiny({0x2b,0x02,0x1f,0,0x2a}),Tiny({0xfe,0x14,0x17,0x2a}),Tiny({0x0e,0,0x2a}),Tiny({0x06,0x2a}),
            Tiny({0x17}),Tiny({0x2b,0xfe}),Tiny({0x17,0x2a,0x18,0x2a})})
            Reject([&] { ValidateCil(bad,0,self,0,0); });
        auto merge=Tiny({0x02,0x2c,0x04,0x17,0x18,0x2b,0x01,0x19,0x2a});
        Reject([&] { ValidateCil(merge,1,self,0,0); });
        for(size_t length=0;length<helper.size();length++) {
            std::vector<unsigned char> truncated(helper.begin(),helper.begin()+length);
            Reject([&] { ValidateCil(truncated,0,self,0,0,0x0a000001,0x0a000002); });
        }
        Reject([&] { ValidateCil(std::vector<unsigned char>(4097),0,self,0,0); });
        auto divide=Tiny({0x1f,42,0x02,0x5b,0x2a});
        auto fault=FaultProbeBody(divide,ValidateCil(divide,1,self,0,0),locals,0,0x0a000001,0x0a000002,0x0a000003);
        auto length=Read32(fault.data()+4); size_t section=(12+length+3)&~size_t(3);
        Require(fault.size()==section+28&&fault[0]==0x1b&&fault[1]==0x30&&Read32(fault.data()+8)==locals);
        Require(fault[section]==0x41&&fault[section+1]==28&&Read32(fault.data()+section+4)==4);
        Require(Read32(fault.data()+section+8)==10&&Read32(fault.data()+section+12)==11&&Read32(fault.data()+section+16)==21);
        Require(Read32(fault.data()+section+20)==11&&Read32(fault.data()+section+24)==0&&fault[12+31]==0xdc);
        Reject([&] { ValidateCil(fault,1,self,1,locals); });
        std::cout<<"Native CIL control-flow, local, token, and rejection contracts passed.\n";
        return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
