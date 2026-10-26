#include <cor.h>
#include <corprof.h>
#include <cstddef>
#ifdef _WIN32
static_assert(sizeof(CONTEXT)==1232);
static_assert(alignof(CONTEXT)==16);
static_assert(offsetof(CONTEXT,ContextFlags)==48);
static_assert(offsetof(CONTEXT,Rip)==248);
#endif
static_assert(sizeof(void*)==8);
static_assert(sizeof(HRESULT)==4);
static_assert(sizeof(ULONG)==4);
static_assert(sizeof(DWORD)==4);
static_assert(sizeof(WCHAR)==2);
static_assert(sizeof(GUID)==16);
static_assert(sizeof(FunctionID)==8);
static_assert(sizeof(COR_PRF_CODE_INFO)==16);
static_assert(sizeof(COR_DEBUG_IL_TO_NATIVE_MAP)==12);
static_assert(((COR_PRF_ENABLE_REJIT|COR_PRF_MONITOR_JIT_COMPILATION|COR_PRF_MONITOR_MODULE_LOADS)&~COR_PRF_ALLOWABLE_AFTER_ATTACH)==0);
int main() { return 0; }
