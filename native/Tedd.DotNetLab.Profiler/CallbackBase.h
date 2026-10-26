#pragma once
#include <cor.h>
#include <corprof.h>
class CallbackBase : public ICorProfilerCallback4 {
public:
    HRESULT STDMETHODCALLTYPE Initialize(IUnknown *pICorProfilerInfoUnk) override { (void)pICorProfilerInfoUnk; return S_OK; }
    HRESULT STDMETHODCALLTYPE Shutdown(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainCreationStarted(AppDomainID appDomainId) override { (void)appDomainId; return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainCreationFinished(AppDomainID appDomainId, HRESULT hrStatus) override { (void)appDomainId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainShutdownStarted(AppDomainID appDomainId) override { (void)appDomainId; return S_OK; }
    HRESULT STDMETHODCALLTYPE AppDomainShutdownFinished(AppDomainID appDomainId, HRESULT hrStatus) override { (void)appDomainId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyLoadStarted(AssemblyID assemblyId) override { (void)assemblyId; return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyLoadFinished(AssemblyID assemblyId, HRESULT hrStatus) override { (void)assemblyId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyUnloadStarted(AssemblyID assemblyId) override { (void)assemblyId; return S_OK; }
    HRESULT STDMETHODCALLTYPE AssemblyUnloadFinished(AssemblyID assemblyId, HRESULT hrStatus) override { (void)assemblyId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleLoadStarted(ModuleID moduleId) override { (void)moduleId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID moduleId, HRESULT hrStatus) override { (void)moduleId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID moduleId) override { (void)moduleId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleUnloadFinished(ModuleID moduleId, HRESULT hrStatus) override { (void)moduleId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE ModuleAttachedToAssembly(ModuleID moduleId, AssemblyID AssemblyId) override { (void)moduleId; (void)AssemblyId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassLoadStarted(ClassID classId) override { (void)classId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassLoadFinished(ClassID classId, HRESULT hrStatus) override { (void)classId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassUnloadStarted(ClassID classId) override { (void)classId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ClassUnloadFinished(ClassID classId, HRESULT hrStatus) override { (void)classId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE FunctionUnloadStarted(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock) override { (void)functionId; (void)fIsSafeToBlock; return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCompilationFinished(FunctionID functionId, HRESULT hrStatus, BOOL fIsSafeToBlock) override { (void)functionId; (void)hrStatus; (void)fIsSafeToBlock; return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchStarted(FunctionID functionId, BOOL *pbUseCachedFunction) override { (void)functionId; (void)pbUseCachedFunction; *pbUseCachedFunction=TRUE; return S_OK; }
    HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchFinished(FunctionID functionId, COR_PRF_JIT_CACHE result) override { (void)functionId; (void)result; return S_OK; }
    HRESULT STDMETHODCALLTYPE JITFunctionPitched(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE JITInlining(FunctionID callerId, FunctionID calleeId, BOOL *pfShouldInline) override { (void)callerId; (void)calleeId; (void)pfShouldInline; *pfShouldInline=TRUE; return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadCreated(ThreadID threadId) override { (void)threadId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadDestroyed(ThreadID threadId) override { (void)threadId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadAssignedToOSThread(ThreadID managedThreadId, DWORD osThreadId) override { (void)managedThreadId; (void)osThreadId; return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientInvocationStarted(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientSendingMessage(GUID *pCookie, BOOL fIsAsync) override { (void)pCookie; (void)fIsAsync; return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientReceivingReply(GUID *pCookie, BOOL fIsAsync) override { (void)pCookie; (void)fIsAsync; return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingClientInvocationFinished(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerReceivingMessage(GUID *pCookie, BOOL fIsAsync) override { (void)pCookie; (void)fIsAsync; return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerInvocationStarted(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerInvocationReturned(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RemotingServerSendingReply(GUID *pCookie, BOOL fIsAsync) override { (void)pCookie; (void)fIsAsync; return S_OK; }
    HRESULT STDMETHODCALLTYPE UnmanagedToManagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason) override { (void)functionId; (void)reason; return S_OK; }
    HRESULT STDMETHODCALLTYPE ManagedToUnmanagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason) override { (void)functionId; (void)reason; return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON suspendReason) override { (void)suspendReason; return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeSuspendFinished(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeSuspendAborted(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeResumeStarted(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeResumeFinished(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeThreadSuspended(ThreadID threadId) override { (void)threadId; return S_OK; }
    HRESULT STDMETHODCALLTYPE RuntimeThreadResumed(ThreadID threadId) override { (void)threadId; return S_OK; }
    HRESULT STDMETHODCALLTYPE MovedReferences(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[ ], ObjectID newObjectIDRangeStart[ ], ULONG cObjectIDRangeLength[ ]) override { (void)cMovedObjectIDRanges; (void)oldObjectIDRangeStart; (void)newObjectIDRangeStart; (void)cObjectIDRangeLength; return S_OK; }
    HRESULT STDMETHODCALLTYPE ObjectAllocated(ObjectID objectId, ClassID classId) override { (void)objectId; (void)classId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ObjectsAllocatedByClass(ULONG cClassCount, ClassID classIds[ ], ULONG cObjects[ ]) override { (void)cClassCount; (void)classIds; (void)cObjects; return S_OK; }
    HRESULT STDMETHODCALLTYPE ObjectReferences(ObjectID objectId, ClassID classId, ULONG cObjectRefs, ObjectID objectRefIds[ ]) override { (void)objectId; (void)classId; (void)cObjectRefs; (void)objectRefIds; return S_OK; }
    HRESULT STDMETHODCALLTYPE RootReferences(ULONG cRootRefs, ObjectID rootRefIds[ ]) override { (void)cRootRefs; (void)rootRefIds; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionThrown(ObjectID thrownObjectId) override { (void)thrownObjectId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionEnter(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionLeave(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFilterEnter(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchFilterLeave(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionSearchCatcherFound(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionOSHandlerEnter(UINT_PTR __unused) override { (void)__unused; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionOSHandlerLeave(UINT_PTR __unused) override { (void)__unused; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionEnter(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionLeave(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyEnter(FunctionID functionId) override { (void)functionId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyLeave(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCatcherEnter(FunctionID functionId, ObjectID objectId) override { (void)functionId; (void)objectId; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCatcherLeave(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE COMClassicVTableCreated(ClassID wrappedClassId, REFGUID implementedIID, void *pVTable, ULONG cSlots) override { (void)wrappedClassId; (void)implementedIID; (void)pVTable; (void)cSlots; return S_OK; }
    HRESULT STDMETHODCALLTYPE COMClassicVTableDestroyed(ClassID wrappedClassId, REFGUID implementedIID, void *pVTable) override { (void)wrappedClassId; (void)implementedIID; (void)pVTable; return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherFound(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherExecute(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ThreadNameChanged(ThreadID threadId, ULONG cchName, _In_reads_opt_(cchName) WCHAR name[ ]) override { (void)threadId; (void)cchName; (void)name; return S_OK; }
    HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(int cGenerations, BOOL generationCollected[ ], COR_PRF_GC_REASON reason) override { (void)cGenerations; (void)generationCollected; (void)reason; return S_OK; }
    HRESULT STDMETHODCALLTYPE SurvivingReferences(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[ ], ULONG cObjectIDRangeLength[ ]) override { (void)cSurvivingObjectIDRanges; (void)objectIDRangeStart; (void)cObjectIDRangeLength; return S_OK; }
    HRESULT STDMETHODCALLTYPE GarbageCollectionFinished(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE FinalizeableObjectQueued(DWORD finalizerFlags, ObjectID objectID) override { (void)finalizerFlags; (void)objectID; return S_OK; }
    HRESULT STDMETHODCALLTYPE RootReferences2(ULONG cRootRefs, ObjectID rootRefIds[ ], COR_PRF_GC_ROOT_KIND rootKinds[ ], COR_PRF_GC_ROOT_FLAGS rootFlags[ ], UINT_PTR rootIds[ ]) override { (void)cRootRefs; (void)rootRefIds; (void)rootKinds; (void)rootFlags; (void)rootIds; return S_OK; }
    HRESULT STDMETHODCALLTYPE HandleCreated(GCHandleID handleId, ObjectID initialObjectId) override { (void)handleId; (void)initialObjectId; return S_OK; }
    HRESULT STDMETHODCALLTYPE HandleDestroyed(GCHandleID handleId) override { (void)handleId; return S_OK; }
    HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown *pCorProfilerInfoUnk, void *pvClientData, UINT cbClientData) override { (void)pCorProfilerInfoUnk; (void)pvClientData; (void)cbClientData; return S_OK; }
    HRESULT STDMETHODCALLTYPE ProfilerAttachComplete(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ProfilerDetachSucceeded(void) override {  return S_OK; }
    HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(FunctionID functionId, ReJITID rejitId, BOOL fIsSafeToBlock) override { (void)functionId; (void)rejitId; (void)fIsSafeToBlock; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl *pFunctionControl) override { (void)moduleId; (void)methodId; (void)pFunctionControl; return S_OK; }
    HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(FunctionID functionId, ReJITID rejitId, HRESULT hrStatus, BOOL fIsSafeToBlock) override { (void)functionId; (void)rejitId; (void)hrStatus; (void)fIsSafeToBlock; return S_OK; }
    HRESULT STDMETHODCALLTYPE ReJITError(ModuleID moduleId, mdMethodDef methodId, FunctionID functionId, HRESULT hrStatus) override { (void)moduleId; (void)methodId; (void)functionId; (void)hrStatus; return S_OK; }
    HRESULT STDMETHODCALLTYPE MovedReferences2(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[ ], ObjectID newObjectIDRangeStart[ ], SIZE_T cObjectIDRangeLength[ ]) override { (void)cMovedObjectIDRanges; (void)oldObjectIDRangeStart; (void)newObjectIDRangeStart; (void)cObjectIDRangeLength; return S_OK; }
    HRESULT STDMETHODCALLTYPE SurvivingReferences2(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[ ], SIZE_T cObjectIDRangeLength[ ]) override { (void)cSurvivingObjectIDRanges; (void)objectIDRangeStart; (void)cObjectIDRangeLength; return S_OK; }
};
