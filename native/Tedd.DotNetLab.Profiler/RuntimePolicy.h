#pragma once
#include <string>

inline bool QualifiedRuntimeVersion(unsigned major,unsigned minor,unsigned patch,const std::string& version,bool) {
    if(major<6||version.empty()||version.size()>128) return false;
    auto prefix=std::to_string(major)+"."+std::to_string(minor)+"."+std::to_string(patch);
    if(version.size()<prefix.size()||version.compare(0,prefix.size(),prefix)!=0) return false;
    if(version.size()==prefix.size()) return true;
    char separator=version[prefix.size()];
    if((separator!='-'&&separator!='+')||version.size()==prefix.size()+1) return false;
    for(size_t i=prefix.size()+1;i<version.size();i++) {
        char c=version[i];
        bool valid=(c>='0'&&c<='9')||(c>='A'&&c<='Z')||(c>='a'&&c<='z')||c=='.'||c=='-'||c=='+';
        if(!valid) return false;
    }
    return true;
}
