"""Generate exact no-op callback signatures from the pinned CoreCLR header."""
import pathlib
import re
root=pathlib.Path(__file__).resolve().parent
header=(root/"vendor/coreclr/corprof.h").read_text()
methods=[]
for interface in ["ICorProfilerCallback","ICorProfilerCallback2","ICorProfilerCallback3","ICorProfilerCallback4"]:
    match=re.search(r"\b"+interface+r" : public \w+\s*\{(.*?)\n\s*\};",header,re.S)
    if not match:
        raise RuntimeError("Missing "+interface)
    for method in re.finditer(r"virtual HRESULT STDMETHODCALLTYPE (\w+)\((.*?)\) = 0;",match[1],re.S):
        name,args=method.groups()
        args=re.sub(r"/\*.*?\*/","",args,flags=re.S)
        args=re.sub(r"_Out_writes_to_\([^\n]+\)","",args)
        args=re.sub(r"\s+"," ",args).strip()
        names=[]
        for arg in args.split(","):
            found=re.search(r"([A-Za-z_]\w*)\s*(?:\[[^\]]*\])?$",arg.strip())
            if found and found[1]!="void":
                names.append(found[1])
        unused=" ".join("(void)"+name+";" for name in names)
        body=unused+" return S_OK;"
        if name=="JITInlining": body=unused+" *pfShouldInline=TRUE; return S_OK;"
        if name=="JITCachedFunctionSearchStarted": body=unused+" *pbUseCachedFunction=TRUE; return S_OK;"
        methods.append(f"    HRESULT STDMETHODCALLTYPE {name}({args}) override {{ {body} }}")
out="#pragma once\n#include <cor.h>\n#include <corprof.h>\nclass CallbackBase : public ICorProfilerCallback4 {\npublic:\n"
out+="\n".join(methods)+"\n};\n"
(root/"Tedd.DotNetLab.Profiler/CallbackBase.h").write_text(out,encoding="utf-8")
