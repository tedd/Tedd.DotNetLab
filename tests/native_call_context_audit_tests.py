import copy
import hashlib
import unittest
from native_call_context_audit import audit, runtime_contract

MATRIX = {"schema_version": "native-call-context/v1", "runtime": "10.0.12", "info_apis": [
    {"api": "EnumModules", "project_context": "registered_worker", "entrypoint": "SYNC", "flags": ["kP2EETriggers"], "gc_contract": "GC_TRIGGERS"}],
    "function_control_apis": [{"api": "SetILFunctionBody"}]}
SOURCE = """HRESULT ProfToEEInterfaceImpl::EnumModules(ICorProfilerModuleEnum** result)
{
    CONTRACTL { GC_TRIGGERS; MODE_ANY; } CONTRACTL_END;
    PROFILER_TO_CLR_ENTRYPOINT_SYNC_EX(kP2EETriggers, (LF_CORPROF, "enum"));
}
"""

class NativeCallContextAuditTests(unittest.TestCase):
    def test_attach_initialization_requires_an_explicit_runtime_allowance(self):
        matrix=copy.deepcopy(MATRIX); matrix["info_apis"][0]["project_context"]="startup_or_windows_attach_initialize"
        production="info->EnumModules(); control->SetILFunctionBody();"
        with self.assertRaises(ValueError): audit(matrix,production)
        matrix["info_apis"][0]["flags"].append("kP2EEAllowableAfterAttach")
        self.assertFalse(audit(matrix,production)["runtime_integration_qualified"])
    def test_triggering_contract_and_async_allowance_are_not_inferred_from_api_name(self):
        self.assertEqual({"entrypoint": "SYNC", "flags": ["kP2EETriggers"], "gc_contract": "GC_TRIGGERS"}, runtime_contract(SOURCE, "EnumModules"))
        changed = SOURCE.replace("GC_TRIGGERS;", "DISABLED(GC_NOTRIGGER);").replace("SYNC_EX", "ASYNC_EX")
        self.assertEqual("DISABLED_GC_NOTRIGGER", runtime_contract(changed, "EnumModules")["gc_contract"])
        self.assertEqual("ASYNC", runtime_contract(changed, "EnumModules")["entrypoint"])
    def test_missing_or_ambiguous_runtime_definitions_require_review(self):
        for changed in ("", SOURCE + SOURCE):
            with self.assertRaises(ValueError): runtime_contract(changed, "EnumModules")
    def test_new_and_stale_production_calls_block_matrix_verification(self):
        production = "info->EnumModules(&result); control->SetILFunctionBody(n, bytes); info->Release();"
        self.assertFalse(audit(MATRIX, production)["runtime_integration_qualified"])
        for changed in (production + "info->SuspendRuntime();", production.replace("info->EnumModules(&result);", ""), production + "control->SetCodegenFlags(0);"):
            with self.assertRaises(ValueError): audit(MATRIX, changed)
    def test_duplicate_rows_and_unqualified_contexts_are_rejected(self):
        changed = copy.deepcopy(MATRIX); changed["info_apis"][0]["project_context"] = "managed_workload"
        with self.assertRaises(ValueError): audit(changed, "info->EnumModules(); control->SetILFunctionBody();")
        changed = copy.deepcopy(MATRIX); changed["info_apis"] *= 2
        with self.assertRaises(ValueError): audit(changed, "info->EnumModules(); control->SetILFunctionBody();")
    def test_source_hash_or_declared_runtime_contract_mismatch_is_rejected(self):
        sources = {"proftoeeinterfaceimpl.cpp": SOURCE.encode(), "rejit.cpp": b"HRESULT ProfilerFunctionControl::SetILFunctionBody() { GC_NOTRIGGER; memcpy(m_pbIL, pbNewILMethodHeader, cbNewILMethodHeader); }\nHRESULT ProfilerFunctionControl::Next() {}"}
        matrix = copy.deepcopy(MATRIX)
        matrix["sources"] = [{"file": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in sources.items()]
        production = "info->EnumModules(); control->SetILFunctionBody();"
        self.assertTrue(audit(matrix, production, sources)["source_contracts_verified"])
        changed = {**sources, "proftoeeinterfaceimpl.cpp": sources["proftoeeinterfaceimpl.cpp"] + b"changed"}
        with self.assertRaises(ValueError): audit(matrix, production, changed)
        matrix["info_apis"][0]["gc_contract"] = "GC_NOTRIGGER"
        with self.assertRaises(ValueError): audit(matrix, production, sources)

if __name__ == "__main__": unittest.main()
