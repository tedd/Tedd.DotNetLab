#include "../Tedd.DotNetLab.Profiler/RuntimePolicy.h"
#include <cstdlib>

int main() {
    auto require=[](bool condition) { if(!condition) std::abort(); };
    require(QualifiedRuntimeVersion(6,0,36,"6.0.36",true));
    require(QualifiedRuntimeVersion(8,0,33,"8.0.33",true));
    require(QualifiedRuntimeVersion(9,0,22,"9.0.22",false));
    require(QualifiedRuntimeVersion(10,0,12,"10.0.12",true));
    require(QualifiedRuntimeVersion(10,0,12,"10.0.12",false));
    require(QualifiedRuntimeVersion(10,0,14,"10.0.14",true));
    require(QualifiedRuntimeVersion(10,0,14,"10.0.14",false));
    require(QualifiedRuntimeVersion(11,0,0,"11.0.0-rc.1.26425.128",true));
    require(QualifiedRuntimeVersion(11,0,0,"11.0.0-rc.2.26504.105",true));
    require(QualifiedRuntimeVersion(11,0,0,"11.0.0-rc.2.26504.105",false));
    require(QualifiedRuntimeVersion(11,0,0,"11.0.0-rc.3.26522.148",true));
    require(QualifiedRuntimeVersion(11,0,0,"11.0.0",true));
    require(QualifiedRuntimeVersion(12,0,0,"12.0.0-preview.1+build.42",true));
    require(!QualifiedRuntimeVersion(5,0,17,"5.0.17",true));
    require(!QualifiedRuntimeVersion(4,8,0,"4.8.0",true));
    require(!QualifiedRuntimeVersion(10,0,12,"11.0.0-rc.2.26504.105",true));
    require(!QualifiedRuntimeVersion(11,1,0,"11.0.0-rc.2.26504.105",true));
    require(!QualifiedRuntimeVersion(11,0,0,"11.0.0-",true));
    require(!QualifiedRuntimeVersion(11,0,0,"",true));
}
