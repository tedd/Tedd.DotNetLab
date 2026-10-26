#pragma once
#include "RuntimePolicy.h"
#include <algorithm>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <map>
#include <set>
#include <limits>
#include <chrono>
#include <thread>
#ifndef _WIN32
#include <sys/uio.h>
#endif
template<class T> struct Com {
    T* value=nullptr;
    ~Com() { if(value) value->Release(); }
    Com()=default;
    Com(const Com&)=delete;
    T* operator->() const { return value; }
};
static std::string Hex(unsigned long long value) {
    std::ostringstream stream; stream<<"0x"<<std::hex<<value; return stream.str();
}
static std::string MethodToken(mdMethodDef token) { std::ostringstream stream; stream<<"0x"<<std::hex<<std::setw(8)<<std::setfill('0')<<token; return stream.str(); }
static std::string GuidText(const GUID& guid) {
#ifdef _WIN32
    WCHAR text[40]{}; StringFromGUID2(guid,text,40);
    auto result=Utf8(text);
    if(result.size()>2) result=result.substr(1,result.size()-2);
    std::transform(result.begin(),result.end(),result.begin(),[](unsigned char c){return static_cast<char>(tolower(c));});
    return result;
#else
    std::ostringstream stream; stream<<std::hex<<std::setfill('0')<<std::setw(8)<<guid.Data1<<'-'<<std::setw(4)<<guid.Data2<<'-'<<std::setw(4)<<guid.Data3<<'-';
    for(size_t index=0;index<8;index++) { if(index==2) stream<<'-'; stream<<std::setw(2)<<static_cast<unsigned>(guid.Data4[index]); }
    return stream.str();
#endif
}
static std::string HexBytes(const BYTE* bytes,size_t count) {
    static const char* digits="0123456789abcdef"; std::string text; text.reserve(count*2);
    for(size_t i=0;i<count;i++) { text+=digits[bytes[i]>>4]; text+=digits[bytes[i]&15]; }
    return text;
}
static std::string Base64(const std::vector<unsigned char>& data) {
    static const char* table="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string result; result.reserve((data.size()+2)/3*4);
    for(size_t i=0;i<data.size();i+=3) {
        unsigned value=static_cast<unsigned>(data[i])<<16;
        if(i+1<data.size()) value|=static_cast<unsigned>(data[i+1])<<8;
        if(i+2<data.size()) value|=data[i+2];
        result+=table[(value>>18)&63]; result+=table[(value>>12)&63];
        result+=i+1<data.size()?table[(value>>6)&63]:'=';
        result+=i+2<data.size()?table[value&63]:'=';
    }
    return result;
}
static std::vector<unsigned char> CopyVerifiedRegion(UINT_PTR start,size_t size) {
    if(size==0||size>262144||start>std::numeric_limits<UINT_PTR>::max()-size)
        throw Failure("resource_limit","Verified region length/address exceeds policy.");
    std::vector<unsigned char> bytes(size);
#ifdef _WIN32
    SIZE_T actual=0;
    if(!ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<const void*>(start),bytes.data(),size,&actual)||actual!=size)
        throw Failure("snapshot_unavailable","Verified code region is no longer readable.");
#else
    iovec destination{bytes.data(),size},source{reinterpret_cast<void*>(start),size};
    auto actual=process_vm_readv(getpid(),&destination,1,&source,1,0);
    if(actual<0||static_cast<size_t>(actual)!=size) throw Failure("snapshot_unavailable","Verified code region is no longer readable.");
#endif
    return bytes;
}
static unsigned long long NowTicks() {
#ifdef _WIN32
    FILETIME time; GetSystemTimeAsFileTime(&time); ULARGE_INTEGER value;
    value.LowPart=time.dwLowDateTime; value.HighPart=time.dwHighDateTime;
    return value.QuadPart+504911232000000000ULL;
#else
    auto ticks=std::chrono::duration_cast<std::chrono::duration<long long,std::ratio<1,10000000>>>(std::chrono::system_clock::now().time_since_epoch()).count();
    return static_cast<unsigned long long>(621355968000000000LL+ticks);
#endif
}
static unsigned long long ExpiryTicks(const std::string& text) {
    if(text.size()<20||!(text.ends_with("Z")||text.ends_with("+00:00"))||text[4]!='-'||text[7]!='-'||text[10]!='T'||text[13]!=':'||text[16]!=':')
        throw Failure("permission_denied","Grant expiry must be UTC.");
    auto zone=text.size()-(text.ends_with("Z")?1:6);
    if(zone!=19&&(zone<21||zone>27||text[19]!='.')) throw Failure("permission_denied","Invalid grant expiry.");
    auto part=[&](size_t start,size_t length) {
        if(start+length>text.size()) throw Failure("permission_denied","Invalid expiry.");
        for(size_t i=start;i<start+length;i++) if(text[i]<'0'||text[i]>'9') throw Failure("permission_denied","Invalid expiry.");
        return static_cast<WORD>(std::stoul(text.substr(start,length)));
    };
#ifdef _WIN32
    SYSTEMTIME time{}; time.wYear=part(0,4); time.wMonth=part(5,2); time.wDay=part(8,2);
    time.wHour=part(11,2); time.wMinute=part(14,2); time.wSecond=part(17,2);
    FILETIME output;
    if(!SystemTimeToFileTime(&time,&output)) throw Failure("permission_denied","Invalid expiry.");
    ULARGE_INTEGER value; value.LowPart=output.dwLowDateTime; value.HighPart=output.dwHighDateTime;
    auto origin=value.QuadPart+504911232000000000ULL;
#else
    auto year=part(0,4),month=part(5,2),day=part(8,2),hour=part(11,2),minute=part(14,2),second=part(17,2);
    std::chrono::year_month_day date{std::chrono::year(year),std::chrono::month(month),std::chrono::day(day)};
    if(year<1||!date.ok()||hour>23||minute>59||second>59) throw Failure("permission_denied","Invalid expiry.");
    auto seconds=std::chrono::duration_cast<std::chrono::seconds>(std::chrono::sys_days(date).time_since_epoch()).count()+hour*3600+minute*60+second;
    auto origin=static_cast<unsigned long long>(621355968000000000LL+seconds*10000000LL);
#endif
    unsigned long long fraction=0;
    if(text[19]=='.') {
        size_t digits=0;
        for(size_t i=20;i<text.size()&&text[i]>='0'&&text[i]<='9';i++,digits++) {
            if(digits>=7) throw Failure("permission_denied","Invalid expiry precision.");
            fraction=fraction*10+static_cast<unsigned>(text[i]-'0');
        }
        for(;digits<7;digits++) fraction*=10;
    }
    return origin+fraction;
}
class NativeAuthorization {
    std::map<std::string,unsigned> uses;
    std::map<std::string,std::string> fingerprints;
public:
    void CheckGrant(const json& request,const std::string& permission,const std::string& target,
        unsigned long long start,const std::string& workspace,long long epoch,const std::string& content="",const std::string& scope="") {
        if(request.value("expected_target_epoch","")!=std::to_string(epoch)) throw Failure("stale_epoch","Native epoch differs.");
        if(!request.contains("grant")||!request["grant"].is_object()) throw Failure("permission_denied","Native grant required.");
        const auto& grant=request["grant"];
        static const std::set<std::string> fields={"id","workspace","target_id","start_time_utc_ticks","permission","expires_at","maximum_operations","content_hash","method_scope"};
        for(auto entry=grant.begin();entry!=grant.end();++entry) if(!fields.contains(entry.key()))
            throw Failure("permission_denied","Unknown grant field.");
        std::string id=grant.value("id","");
        unsigned maximum=grant.value("maximum_operations",0U);
        if(id.empty()||id.size()>128||maximum==0||maximum>100000||workspace.empty()||
            grant.value("workspace","")!=workspace||grant.value("target_id","")!=target||
            grant.value("start_time_utc_ticks","")!=std::to_string(start)||grant.value("permission","")!=permission||
            ExpiryTicks(grant.value("expires_at",""))<=NowTicks())
            throw Failure("permission_denied","Native grant is expired or outside scope.");
        if(content.empty()) {
            if((grant.contains("content_hash")&&!grant["content_hash"].is_null())||(grant.contains("method_scope")&&!grant["method_scope"].is_null()))
                throw Failure("permission_denied","Unexpected grant content or method scope.");
        } else if(grant.value("content_hash","")!=content||grant.value("method_scope","")!=scope)
            throw Failure("permission_denied","Mutation grant content/method scope differs.");
        if(uses.size()>=10000&&!uses.contains(id)) throw Failure("resource_limit","Native grant registry cap reached.");
        std::string fingerprint=grant.dump();
        if(fingerprints.contains(id)&&fingerprints[id]!=fingerprint) throw Failure("permission_denied","Grant identity was altered.");
        fingerprints[id]=fingerprint;
        if(uses[id]>=maximum) throw Failure("permission_denied","Native grant exhausted.");
        uses[id]++;
    }
};
class Inspector {
    ICorProfilerInfo12* info;
    std::atomic<unsigned long long>& codeEpoch;
    static std::string FileHash(const std::basic_string<WCHAR>& path) {
#ifdef _WIN32
        std::ifstream file(path,std::ios::binary|std::ios::ate);
#else
        std::ifstream file(Utf8(path),std::ios::binary|std::ios::ate);
#endif
        if(!file) throw Failure("module_identity_unavailable","Loaded module file is inaccessible.");
        auto size=file.tellg();
        if(size<0||size>67108864) throw Failure("resource_limit","Module file exceeds hashing policy.");
        std::vector<unsigned char> bytes(static_cast<size_t>(size));
        file.seekg(0); file.read(reinterpret_cast<char*>(bytes.data()),static_cast<std::streamsize>(bytes.size()));
        if(!file&&bytes.size()!=0) throw Failure("module_identity_unavailable","Loaded module file changed during hashing.");
        return Sha(bytes);
    }
    std::vector<ModuleID> Modules() {
        Com<ICorProfilerModuleEnum> modules; Check(info->EnumModules(&modules.value),"EnumModules");
        std::vector<ModuleID> result;
        while(result.size()<10000) {
            ModuleID module=0; ULONG fetched=0;
            Check(modules->Next(1,&module,&fetched),"EnumModules.Next");
            if(fetched==0) return result;
            result.push_back(module);
        }
        throw Failure("resource_limit","Module enumeration cap reached.");
    }
public:
    unsigned LocalCount(ModuleID module,unsigned token) {
        if(token==0) return 0;
        if((token>>24)!=0x11) throw Failure("invalid_cil","Expected an existing local signature token.");
        Com<IMetaDataImport2> metadata;
        Check(info->GetModuleMetaData(module,ofRead,IID_IMetaDataImport2,reinterpret_cast<IUnknown**>(&metadata.value)),"LocalMetadata");
        if(!metadata->IsValidToken(token)) throw Failure("invalid_cil","Local signature token is absent.");
        PCCOR_SIGNATURE signature=nullptr; ULONG length=0;
        Check(metadata->GetSigFromToken(token,&signature,&length),"GetLocalSignature");
        if(length<2||signature[0]!=7||signature[1]>64||length!=static_cast<ULONG>(signature[1])+2)
            throw Failure("capability_unsupported","Only bounded existing Int32 local signatures are supported.");
        for(ULONG i=2;i<length;i++) if(signature[i]!=8) throw Failure("capability_unsupported","Local signature contains an unsupported type.");
        return signature[1];
    }
    void VerifyProbeHelpers(ModuleID module,const std::string& helperHash,mdMemberRef enter,mdMemberRef exit,mdMemberRef exceptional=0) {
        if(helperHash.size()!=64||(enter>>24)!=0x0a||(exit>>24)!=0x0a||enter==exit||
            (exceptional!=0&&((exceptional>>24)!=0x0a||exceptional==enter||exceptional==exit)))
            throw Failure("invalid_request","Explicit approved helper hash and MemberRef tokens are required.");
        Com<IMetaDataImport2> metadata; Com<IMetaDataAssemblyImport> assemblies;
        Check(info->GetModuleMetaData(module,ofRead,IID_IMetaDataImport2,reinterpret_cast<IUnknown**>(&metadata.value)),"ProbeMetadata");
        Check(info->GetModuleMetaData(module,ofRead,IID_IMetaDataAssemblyImport,reinterpret_cast<IUnknown**>(&assemblies.value)),"ProbeAssemblyMetadata");
        std::vector<std::pair<mdMemberRef,const char*>> helpers{{enter,"Enter"},{exit,"Exit"}};
        if(exceptional!=0) helpers.push_back({exceptional,"ExitExceptional"});
        for(auto pair:helpers) {
            if(!metadata->IsValidToken(pair.first)) throw Failure("invalid_cil","Helper token is absent from the target module.");
            mdToken owner=0; WCHAR name[1024]{}; ULONG count=0,size=0; PCCOR_SIGNATURE signature=nullptr;
            Check(metadata->GetMemberRefProps(pair.first,&owner,name,1024,&count,&signature,&size),"ProbeMemberRef");
            if(count>=1024||Utf8(name)!=pair.second||size!=4||HexBytes(signature,size)!="00010108"||(owner>>24)!=0x01)
                throw Failure("invalid_cil","Helper member name/signature/owner differs from the approved ABI.");
            mdToken assembly=0; WCHAR type[1024]{};
            Check(metadata->GetTypeRefProps(owner,&assembly,type,1024,&count),"ProbeTypeRef");
            if(count>=1024||Utf8(type)!="Tedd.DotNetLab.Probes.ProbeRuntime"||(assembly>>24)!=0x23)
                throw Failure("invalid_cil","Helper type or resolution scope differs.");
            WCHAR assemblyName[1024]{}; ASSEMBLYMETADATA description{};
            const void* key=nullptr; const void* hash=nullptr; ULONG keySize=0,hashSize=0; DWORD flags=0;
            Check(assemblies->GetAssemblyRefProps(assembly,&key,&keySize,assemblyName,1024,&count,&description,&hash,&hashSize,&flags),"ProbeAssemblyRef");
            if(count>=1024||Utf8(assemblyName)!="Tedd.DotNetLab.Probes") throw Failure("invalid_cil","Helper assembly reference differs.");
        }
        unsigned matches=0;
        for(auto loaded:Modules()) {
            Com<IMetaDataImport2> candidate;
            if(FAILED(info->GetModuleMetaData(loaded,ofRead,IID_IMetaDataImport2,reinterpret_cast<IUnknown**>(&candidate.value)))) continue;
            WCHAR scope[1024]{}; ULONG count=0; GUID mvid{};
            if(FAILED(candidate->GetScopeProps(scope,1024,&count,&mvid))||count>=1024||Utf8(scope)!="Tedd.DotNetLab.Probes.dll") continue;
            LPCBYTE base=nullptr; WCHAR path[32768]{}; AssemblyID assembly=0; DWORD flags=0;
            Check(info->GetModuleInfo2(loaded,&base,32768,&count,path,&assembly,&flags),"ProbeModuleInfo");
            if(count>=32768||flags&(COR_PRF_MODULE_DYNAMIC|COR_PRF_MODULE_COLLECTIBLE)||FileHash(path)!=helperHash)
                throw Failure("helper_identity_changed","Loaded probe helper is outside the approved identity/lifetime scope.");
            matches++;
        }
        if(matches!=1) throw Failure("capability_unsupported","Exactly one approved probe helper must be loaded through startup integration.");
    }
    struct Method {
        ModuleID module=0; mdMethodDef token=0; FunctionID function=0;
        std::string mvid,moduleHash,signature,name,declaringType;
        std::vector<unsigned char> original;
        DWORD attributes=0,implementation=0;
    };
    Method FromToken(ModuleID module,mdMethodDef token) {
        Com<IMetaDataImport2> metadata;
        Check(info->GetModuleMetaData(module,ofRead,IID_IMetaDataImport2,reinterpret_cast<IUnknown**>(&metadata.value)),"InlinerMetadata");
        WCHAR name[1024]{}; ULONG count=0; GUID mvid{};
        Check(metadata->GetScopeProps(name,1024,&count,&mvid),"InlinerScope");
        auto result=Resolve(json{{"module_mvid",GuidText(mvid)},{"method_token",MethodToken(token)},{"module_load_id",Hex(module)}});
        if(!result.function||result.original.size()>4096)
            throw Failure("capability_unsupported","Inliner must have one bounded nongeneric retained method instance.");
        return result;
    }
    Method FromFunction(FunctionID function) {
        ClassID type=0; ModuleID module=0; mdToken token=0;
        Check(info->GetFunctionInfo(function,&type,&module,&token),"InlinerFunctionInfo");
        auto result=FromToken(module,token);
        if(result.function!=function) throw Failure("capability_unsupported","Inliner instantiation is outside the concrete method subset.");
        return result;
    }
    std::vector<ModuleID> PrecompiledModules() {
        std::vector<ModuleID> result;
        for(auto module:Modules()) {
            LPCBYTE base=nullptr; AssemblyID assembly=0; ULONG count=0; DWORD flags=0; WCHAR path[32768]{};
            Check(info->GetModuleInfo2(module,&base,32768,&count,path,&assembly,&flags),"PrecompiledModuleInfo");
            if(count>=32768) throw Failure("resource_limit","Precompiled module path exceeds policy.");
            if(flags&COR_PRF_MODULE_NGEN) result.push_back(module);
        }
        return result;
    }
    static json Binding(const Method& method) {
        return json{{"module_mvid",method.mvid},{"module_hash",method.moduleHash},{"module_load_identity",Hex(method.module)},
            {"method_token",MethodToken(method.token)},{"signature",method.signature},{"original_il_hash",Sha(method.original)}};
    }
    Method Resolve(const json& selector) {
        static const std::set<std::string> fields={"module_mvid","module_load_id","method_token","module_hash","signature"};
        if(!selector.is_object()) throw Failure("invalid_request","Method selector must be an object.");
        for(auto entry=selector.begin();entry!=selector.end();++entry) if(!fields.contains(entry.key()))
            throw Failure("invalid_request","Unknown method selector field.");
        std::string requestedMvid=selector.value("module_mvid","");
        std::string requestedLoad=selector.value("module_load_id","");
        std::string tokenText=selector.value("method_token","");
        if(requestedMvid.size()!=36||tokenText.size()!=10||!tokenText.starts_with("0x06"))
            throw Failure("invalid_request","Expected explicit MVID and MethodDef token.");
        size_t consumed=0; unsigned long value=std::stoul(tokenText.substr(2),&consumed,16);
        if(consumed!=8||(value&0xffffff)==0) throw Failure("invalid_request","Invalid MethodDef token.");
        Method result; unsigned matches=0;
        for(ModuleID module:Modules()) {
            if(!requestedLoad.empty()&&requestedLoad!=Hex(module)) continue;
            LPCBYTE base=nullptr; WCHAR path[32768]{}; ULONG needed=0; AssemblyID assembly=0; DWORD flags=0;
            HRESULT hr=info->GetModuleInfo2(module,&base,32768,&needed,path,&assembly,&flags);
            if(FAILED(hr)||needed>=32768||flags&(COR_PRF_MODULE_DYNAMIC|COR_PRF_MODULE_COLLECTIBLE|COR_PRF_MODULE_RESOURCE)) continue;
            Com<IMetaDataImport2> metadata;
            hr=info->GetModuleMetaData(module,ofRead,IID_IMetaDataImport2,reinterpret_cast<IUnknown**>(&metadata.value));
            if(FAILED(hr)) continue;
            GUID mvid{}; WCHAR scope[1024]{}; ULONG scopeSize=0;
            Check(metadata->GetScopeProps(scope,1024,&scopeSize,&mvid),"GetScopeProps");
            if(GuidText(mvid)!=requestedMvid) continue;
            if(!metadata->IsValidToken(static_cast<mdToken>(value))) throw Failure("method_not_found","MethodDef token is absent.");
            if(++matches>1) throw Failure("ambiguous_method","MVID is loaded more than once; supply module_load_id.");
            mdTypeDef declaring=0; WCHAR name[1024]{}; ULONG nameSize=0,rva=0; DWORD attributes=0,impl=0;
            PCCOR_SIGNATURE signature=nullptr; ULONG signatureSize=0;
            Check(metadata->GetMethodProps(static_cast<mdMethodDef>(value),&declaring,name,1024,&nameSize,
                &attributes,&signature,&signatureSize,&rva,&impl),"GetMethodProps");
            if(nameSize>=1024||signatureSize>512||rva==0) throw Failure("capability_unsupported","Method has no supported retained body.");
            for(mdToken owner:{static_cast<mdToken>(value),static_cast<mdToken>(declaring)}) {
                HCORENUM enumeration=nullptr; mdGenericParam parameter=0; ULONG count=0;
                HRESULT generic=metadata->EnumGenericParams(&enumeration,owner,&parameter,1,&count);
                if(enumeration) metadata->CloseEnum(enumeration);
                Check(generic,"EnumGenericParams");
                if(count) throw Failure("capability_unsupported","Generic methods/types are excluded from native qualification.");
            }
            WCHAR typeName[1024]{}; ULONG typeSize=0; DWORD typeFlags=0; mdToken parent=0;
            Check(metadata->GetTypeDefProps(declaring,typeName,1024,&typeSize,&typeFlags,&parent),"GetTypeDefProps");
            if(typeSize>=1024) throw Failure("resource_limit","Type name exceeds policy.");
            LPCBYTE il=nullptr; ULONG bytes=0;
            Check(info->GetILFunctionBody(module,static_cast<mdMethodDef>(value),&il,&bytes),"GetILFunctionBody");
            if(bytes==0||bytes>65536) throw Failure("resource_limit","Method body exceeds initial native subset.");
            result.module=module; result.token=static_cast<mdMethodDef>(value);
            result.mvid=requestedMvid; result.moduleHash=FileHash(path);
            result.signature=HexBytes(signature,signatureSize); result.name=Utf8(name); result.declaringType=Utf8(typeName);
            if(selector.contains("module_hash")&&selector["module_hash"]!=result.moduleHash)
                throw Failure("module_identity_changed","Loaded module hash differs from selected build.");
            if(selector.contains("signature")&&selector["signature"]!=result.signature)
                throw Failure("method_identity_changed","Selected signature differs.");
            result.original=CopyVerifiedRegion(reinterpret_cast<UINT_PTR>(il),bytes);
            result.attributes=attributes; result.implementation=impl;
            HRESULT function=info->GetFunctionFromToken(module,result.token,&result.function);
            if(FAILED(function)) result.function=0;
        }
        if(matches==0) throw Failure("method_not_found","Concrete noncollectible module/method was not found.");
        return result;
    }
    std::vector<UINT_PTR> Starts(FunctionID function,ReJITID rejit) {
        for(int attempt=0;attempt<3;attempt++) {
            ULONG32 count=0; auto status=info->GetNativeCodeStartAddresses(function,rejit,0,&count,nullptr);
            if(status==CORPROF_E_FUNCTION_NOT_COMPILED) return {};
            Check(status,"NativeCodeStartAddresses.count");
            if(count>128) throw Failure("resource_limit","Native version count exceeds policy.");
            std::vector<UINT_PTR> starts(count);
            ULONG32 actual=0; HRESULT result=info->GetNativeCodeStartAddresses(function,rejit,count,&actual,starts.data());
            if(result==HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)||actual>count) continue;
            Check(result,"NativeCodeStartAddresses"); starts.resize(actual); return starts;
        }
        throw Failure("version_changed","Native version enumeration did not stabilize.");
    }
public:
    Inspector(ICorProfilerInfo12* info,std::atomic<unsigned long long>& epoch):info(info),codeEpoch(epoch){}
    std::vector<std::pair<UINT_PTR,unsigned long long>> NativeExtents(FunctionID function,ReJITID version) {
        std::vector<std::pair<UINT_PTR,unsigned long long>> result;
        for(auto start:Starts(function,version)) {
            ULONG32 count=0; Check(info->GetCodeInfo4(start,0,&count,nullptr),"BudgetCodeInfo4.count");
            if(count==0||count>16) throw Failure("resource_limit","Native budget extent count exceeds policy.");
            std::vector<COR_PRF_CODE_INFO> regions(count); ULONG32 actual=0;
            Check(info->GetCodeInfo4(start,count,&actual,regions.data()),"BudgetCodeInfo4");
            if(actual==0||actual>count) throw Failure("version_changed","Native budget extent count changed.");
            for(ULONG32 i=0;i<actual;i++) {
                if(regions[i].size==0) throw Failure("snapshot_unavailable","Native budget extent is empty.");
                result.emplace_back(regions[i].startAddress,regions[i].size);
            }
        }
        if(result.empty()) throw Failure("snapshot_unavailable","Compiled native budget extents are unavailable.");
        return result;
    }
    json Snapshot(const json& request,const std::string& target,long long execution) {
        Check(info->InitializeCurrentThread(),"InitializeCurrentThread");
        USHORT instance=0,major=0,minor=0,build=0,qfe=0; COR_PRF_RUNTIME_TYPE runtime=COR_PRF_CORE_CLR; WCHAR version[256]{}; ULONG needed=0;
        Check(info->GetRuntimeInformation(&instance,&runtime,&major,&minor,&build,&qfe,256,&needed,version),"GetRuntimeInformation");
#ifdef _WIN32
        std::string runtimeVersion=needed<=256?Utf8(version):"";
        constexpr bool windows=true;
#else
        std::string runtimeVersion=needed<=256?linux_platform::Utf8(version,256):"";
        constexpr bool windows=false;
#endif
        if(runtime!=COR_PRF_CORE_CLR||!QualifiedRuntimeVersion(major,minor,build,runtimeVersion,windows))
            throw Failure("capability_unsupported","Runtime version is outside the qualified snapshot matrix.");
        auto method=Resolve(request.at("selector"));
        if(!method.function) throw Failure("not_jitted","No compiled body is available; no invocation was performed.");
        std::vector<ReJITID> rejits{0};
        ULONG count=0; Check(info->GetReJITIDs(method.function,0,&count,nullptr),"GetReJITIDs.count");
        if(count>128) throw Failure("resource_limit","ReJIT version count exceeds policy.");
        if(count) {
            std::vector<ReJITID> values(count); ULONG actual=0;
            Check(info->GetReJITIDs(method.function,count,&actual,values.data()),"GetReJITIDs");
            if(actual>count) throw Failure("version_changed","ReJIT versions changed.");
            for(ULONG i=0;i<actual;i++) if(values[i]!=0) rejits.push_back(values[i]);
        }
        for(int attempt=0;attempt<3;attempt++) {
            auto epoch=codeEpoch.load(); json snapshots=json::array(); size_t total=0;
            for(auto rejit:rejits) for(auto start:Starts(method.function,rejit)) {
                ULONG32 regionsCount=0; Check(info->GetCodeInfo4(start,0,&regionsCount,nullptr),"GetCodeInfo4.count");
                if(regionsCount==0||regionsCount>16) throw Failure("snapshot_unavailable","Native extents unavailable or exceed policy.");
                std::vector<COR_PRF_CODE_INFO> regions(regionsCount); ULONG32 actual=0;
                Check(info->GetCodeInfo4(start,regionsCount,&actual,regions.data()),"GetCodeInfo4");
                if(actual>regionsCount) throw Failure("version_changed","Native extent count changed.");
                regions.resize(actual);
                json regionData=json::array();
                for(const auto& region:regions) {
                    if(region.size==0||region.size>262144||total>262144-region.size) throw Failure("resource_limit","Native snapshot response byte budget reached.");
                    auto bytes=CopyVerifiedRegion(region.startAddress,region.size); total+=region.size;
                    regionData.push_back(json{{"start_address",Hex(region.startAddress)},{"bytes",Base64(bytes)},{"kind","runtime_region"}});
                }
                ULONG32 mapCount=0; Check(info->GetILToNativeMapping3(start,0,&mapCount,nullptr),"ILToNativeMapping3.count");
                if(mapCount>100000) throw Failure("resource_limit","Mapping count exceeds policy.");
                std::vector<COR_DEBUG_IL_TO_NATIVE_MAP> maps(mapCount); ULONG32 mapActual=0;
                Check(info->GetILToNativeMapping3(start,mapCount,&mapActual,maps.data()),"ILToNativeMapping3");
                if(mapActual>mapCount) throw Failure("version_changed","Mapping count changed.");
                json qualifiedMaps=json::array(),rawMaps=json::array();
                for(ULONG32 i=0;i<mapActual;i++) {
                    const auto& map=maps[i]; auto il=static_cast<int32_t>(map.ilOffset);
                    rawMaps.push_back(json{{"il_offset",il},{"native_start_offset",std::to_string(map.nativeStartOffset)},
                        {"native_end_offset",std::to_string(map.nativeEndOffset)}});
                    if(!regions.empty()&&map.nativeEndOffset<=regions[0].size&&map.nativeStartOffset<=map.nativeEndOffset)
                        qualifiedMaps.push_back(json{{"il_offset",il},{"region_index",0},{"native_start_offset",map.nativeStartOffset},
                            {"native_end_offset",map.nativeEndOffset},{"category",il==-2?"prolog":il==-3?"epilog":il==-1?"unmapped":"il"}});
                }
                snapshots.push_back(json{{"schema_version","1.0"},{"target_id",target},{"runtime_version",runtimeVersion},{"architecture","x64"},
                    {"module_hash",method.moduleHash},{"module_mvid",method.mvid},{"module_load_identity",Hex(method.module)},
                    {"method_token",MethodToken(method.token)},{"signature",method.signature},{"rejit_id",std::to_string(rejit)},
                    {"epoch",std::to_string(execution)},{"tier","unknown"},{"regions",regionData},{"maps",qualifiedMaps},
                    {"acquisition","profiler-owned-worker/runtime-extents/no-suspension"},{"original_il_hash",Sha(method.original)},
                    {"current_il_hash",Sha(method.original)},{"raw_maps",rawMaps},{"observation_epoch",std::to_string(epoch)}});
            }
            if(codeEpoch.load()==epoch) {
                if(snapshots.empty()) throw Failure("not_jitted","No existing native body was enumerated.");
                return json{{"snapshots",snapshots},{"method_name",method.name},{"declaring_type",method.declaringType},
                    {"original_body",Base64(method.original)},{"original_body_hash",Sha(method.original)},
                    {"perturbation",json{{"runtime_suspended",false},{"worker_registered_with_runtime",true},{"inspection_outside_measurement_required",true}}},
                    {"code_rewritten_for_inspection",false}};
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
        throw Failure("version_changed","JIT/module epoch changed during snapshot; retry outside measurement.");
    }
};
