#include "../Tedd.DotNetLab.Profiler/ManagedCil.h"
#include <iostream>

static void Require(bool value) { if(!value) throw std::runtime_error("Managed CIL contract failed."); }
template<class F> static void Reject(F action) {
    try { action(); } catch(const Failure&) { return; }
    throw std::runtime_error("Invalid managed body was admitted.");
}
int main() {
    try {
        Require(CilArguments("000008")==0);
        Require(ValidateCil({0x0a,0x17,0x2a},0,0x06000001,0,0).size==2);
        unsigned char signature[]={0x20,1,0x0c,0x1d,0x0c};
        auto shape=ManagedSignature(signature,sizeof(signature)); Require(shape.arguments==1&&shape.instance&&shape.returns);
        unsigned char locals[]={7,2,0x15,0x11,5,1,0x0c,0x08}; Require(ManagedLocals(locals,sizeof(locals))==2);
        unsigned char unsafe[]={0,1,1,0x0f,8}; Reject([&]{ ManagedSignature(unsafe,sizeof(unsafe)); });
        unsigned char truncated[]={0,1,8}; Reject([&]{ ManagedSignature(truncated,sizeof(truncated)); });
        auto token=[](unsigned value,int kind) { if(value!=0x0a000001||kind!=4) throw Failure("invalid_cil","Unexpected token."); };
        auto call=[](unsigned) { return ManagedSignatureShape{1,false,true}; };
        // float Call(float): argument, call, return.
        std::vector<unsigned char> body{0x1e,0x02,0x28,1,0,0,0x0a,0x2a};
        Require(ValidateManagedCil(body,1,0,true,token,call).size==7);
        auto missing=body; missing[3]=2; Reject([&]{ ValidateManagedCil(missing,1,0,true,token,call); });
        Reject([&]{ ValidateManagedCil(body,0,0,true,token,call); });
        Reject([&]{ ValidateManagedCil(body,1,0,false,token,call); });
        // void Increment(ref int): managed indirect loads and stores.
        std::vector<unsigned char> reference{0x1e,0x02,0x25,0x4a,0x17,0x58,0x54,0x2a};
        Require(ValidateManagedCil(reference,1,0,false,token,call).size==7);
        std::vector<unsigned char> interior{0x1a,0x2b,1,0xfe,0x13,0x16,0x2a};
        Reject([&]{ ValidateManagedCil(interior,0,0,true,token,call); });
        std::vector<unsigned char> underflow{0x0a,0x58,0x2a}; Reject([&]{ ValidateManagedCil(underflow,0,0,true,token,call); });
        for(size_t length=0;length<body.size();length++) {
            std::vector<unsigned char> partial(body.begin(),body.begin()+length);
            Reject([&]{ ValidateManagedCil(partial,1,0,true,token,call); });
        }
        Require(!ManagedOpcodeAllowed(0x29)&&!ManagedOpcodeAllowed(0xfe0f)&&ManagedOpcodeAllowed(0xfe16));
        std::cout<<"Managed CIL signatures, calls, references, boundaries, and rejection contracts passed.\n"; return 0;
    } catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
