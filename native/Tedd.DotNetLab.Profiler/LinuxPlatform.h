#pragma once
#include "Failure.h"
#include <openssl/sha.h>
#include <array>
#include <charconv>
#include <chrono>
#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <limits>
#include <poll.h>
#include <sstream>
#include <string>
#include <sys/socket.h>
#include <unistd.h>

namespace linux_platform {
inline std::string Read(const std::string& path,size_t maximum) {
    std::ifstream input(path,std::ios::binary);
    if(!input) throw Failure("identity_failed","Kernel identity is unavailable.");
    std::string result(maximum+1,'\0'); input.read(result.data(),static_cast<std::streamsize>(result.size()));
    result.resize(static_cast<size_t>(input.gcount()));
    if(result.size()>maximum) throw Failure("resource_limit","Kernel identity exceeds its read budget.");
    return result;
}
inline unsigned long long Integer(const std::string& text) {
    unsigned long long result=0; auto parsed=std::from_chars(text.data(),text.data()+text.size(),result);
    if(parsed.ec!=std::errc{}||parsed.ptr!=text.data()+text.size()) throw Failure("identity_failed","Kernel identity is malformed.");
    return result;
}
inline unsigned long long StartTicks() {
    auto stat=Read("/proc/self/stat",65536); auto end=stat.rfind(')');
    if(end==std::string::npos) throw Failure("identity_failed","Kernel process stat is malformed.");
    std::istringstream fields(stat.substr(end+1)); std::string value;
    for(int field=3;field<=22;field++) if(!(fields>>value)) throw Failure("identity_failed","Kernel start counter is missing.");
    auto counter=Integer(value); auto frequency=sysconf(_SC_CLK_TCK);
    if(frequency<=0||frequency>10000000) throw Failure("identity_failed","Kernel clock frequency is unsupported.");
    std::istringstream kernel(Read("/proc/stat",1048576)); std::string line; unsigned long long boot=0; bool found=false;
    while(std::getline(kernel,line)) if(line.starts_with("btime ")) { boot=Integer(line.substr(6)); found=true; break; }
    if(!found) throw Failure("identity_failed","Kernel boot epoch is unavailable.");
    constexpr auto ticks=10000000ULL,epoch=621355968000000000ULL;
    constexpr auto limit=3155378975999999999ULL;
    auto seconds=counter/static_cast<unsigned long long>(frequency),fraction=counter%static_cast<unsigned long long>(frequency)*ticks/static_cast<unsigned long long>(frequency);
    if(boot>(limit-epoch)/ticks) throw Failure("identity_failed","Kernel boot epoch overflows its identity range.");
    auto origin=epoch+boot*ticks;
    if(fraction>limit-origin||seconds>(limit-origin-fraction)/ticks)
        throw Failure("identity_failed","Kernel start counter overflows its identity range.");
    return epoch+boot*ticks+seconds*ticks+fraction;
}
inline std::string Sha(const std::string& bytes) {
    std::array<unsigned char,SHA256_DIGEST_LENGTH> digest{};
    if(!SHA256(reinterpret_cast<const unsigned char*>(bytes.data()),bytes.size(),digest.data()))
        throw Failure("hash_failed","SHA-256 failed.");
    static constexpr char digits[]="0123456789abcdef"; std::string text; text.reserve(64);
    for(auto value:digest) { text+=digits[value>>4]; text+=digits[value&15]; } return text;
}
inline std::string Environment(const char* name) {
    const char* value=getenv(name); if(!value) return "";
    size_t size=strnlen(value,32769); if(size>32768) throw Failure("resource_limit","Environment value exceeds policy.");
    return {value,size};
}
using Deadline=std::chrono::steady_clock::time_point;
inline bool Transfer(int socket,int stopping,void* buffer,size_t length,Deadline deadline,bool writing) {
    size_t offset=0; auto* bytes=static_cast<unsigned char*>(buffer);
    while(offset<length) {
        auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(deadline-std::chrono::steady_clock::now()).count();
        if(remaining<=0) return false;
        pollfd waiting[2]={{socket,static_cast<short>(writing?POLLOUT:POLLIN),0},{stopping,POLLIN,0}};
        int ready=poll(waiting,2,static_cast<int>(remaining));
        if(ready<0&&errno==EINTR) continue;
        if(ready<=0||waiting[1].revents) return false;
        auto count=writing?send(socket,bytes+offset,length-offset,MSG_NOSIGNAL):recv(socket,bytes+offset,length-offset,0);
        if(count<0&&(errno==EINTR||errno==EAGAIN||errno==EWOULDBLOCK)) continue;
        if(count<=0) return false;
        offset+=static_cast<size_t>(count);
    }
    return true;
}
template<class Character> inline std::string Utf8(const Character* text,size_t count) {
    std::string result;
    for(size_t index=0;index<count&&text[index];index++) {
        unsigned code=text[index];
        if(code>=0xd800&&code<=0xdbff) {
            if(index+1>=count||text[index+1]<0xdc00||text[index+1]>0xdfff) throw Failure("invalid_text","Invalid runtime UTF-16.");
            code=0x10000+((code-0xd800)<<10)+(text[++index]-0xdc00);
        } else if(code>=0xdc00&&code<=0xdfff) throw Failure("invalid_text","Invalid runtime UTF-16.");
        if(code<0x80) result+=static_cast<char>(code);
        else if(code<0x800) { result+=static_cast<char>(0xc0|(code>>6)); result+=static_cast<char>(0x80|(code&63)); }
        else {
            if(code>=0x10000) result+=static_cast<char>(0xf0|(code>>18));
            result+=static_cast<char>((code>=0x10000?0x80:0xe0)|((code>>12)&(code>=0x10000?63:15)));
            result+=static_cast<char>(0x80|((code>>6)&63)); result+=static_cast<char>(0x80|(code&63));
        }
    }
    return result;
}
}
