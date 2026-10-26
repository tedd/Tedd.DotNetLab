import unittest
from test_evidence_audit import audit

SPEC = b"| FR-001 | Identity. |\n| FR-073 | Controller resource footprint. |\n"
PLAN = b"| UT-ID-001 | Identity check. | FR-001 |\n| PERF-009 | Tool latency. | FR-073 |\n"
DECLARATIONS = {"schema_version": "test-declarations/v1", "assembly_path": "D:/Workspaces/AI/task/tests.dll", "assembly_hash": "a"*64,
                "tests": [{"method": "Tests.Identity.Check", "cases": ["UT-ID-001"], "requirements": ["FR-001"], "milestone": "M0", "level": "Unit"},
                          {"method": "Tests.Resources.Check", "cases": ["PERF-009"], "requirements": ["FR-073"], "milestone": "M7", "level": "Performance"}]}

def trx(outcome="Passed", duplicate=False, total=1, run_outcome="Completed", run_error=False):
    row = f'<UnitTestResult testId="one" executionId="run" testName="identity" outcome="{outcome}" />'
    infos = '<RunInfos><RunInfo outcome="Error"><Text>Testhost crashed.</Text></RunInfo></RunInfos>' if run_error else ''
    return (f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="one"><TestMethod className="Tests.Identity" name="Check" codeBase="D:/Workspaces/AI/task/tests.dll" /></UnitTest></TestDefinitions><Results>{row}{row if duplicate else ""}</Results><ResultSummary outcome="{run_outcome}"><Counters total="{total}" executed="{total}" />{infos}</ResultSummary></TestRun>').encode()

class EvidenceAuditTests(unittest.TestCase):
    def test_partial_run_never_certifies_unexecuted_requirements_or_named_gates(self):
        report = audit(SPEC, PLAN, DECLARATIONS, trx())
        self.assertFalse(report["requirement_completion_proven"])
        self.assertEqual(["FR-073"], report["summary"]["requirements_without_executed_evidence"])
        self.assertEqual(["PERF-009"], report["summary"]["named_gates_without_executed_evidence"])
        self.assertEqual(["Tests.Resources.Check"], report["summary"]["unexecuted_declared_methods"])
        self.assertTrue(report["summary"]["suite_passed"])
    def test_failure_is_not_relabelled_as_passing_coverage(self):
        report = audit(SPEC, PLAN, DECLARATIONS, trx("Failed"))
        self.assertEqual("failing_or_uncompleted_test_evidence", report["requirements"][0]["status"])
        self.assertEqual(0, report["summary"]["passed"])
        self.assertFalse(report["summary"]["suite_passed"])
    def test_green_rows_in_an_aborted_run_do_not_make_a_passing_suite(self):
        for outcome in ("Failed", "Aborted", "InProgress", "Unknown"):
            report = audit(SPEC, PLAN, DECLARATIONS, trx(run_outcome=outcome, run_error=True))
            self.assertEqual(1, report["summary"]["passed"])
            self.assertFalse(report["summary"]["successful_terminal_run"])
            self.assertFalse(report["summary"]["suite_passed"])
            self.assertEqual("failing_or_uncompleted_run_evidence", report["requirements"][0]["status"])
            self.assertEqual("failing_or_uncompleted_run_evidence", report["named_gates"][1]["status"])
    def test_terminal_errors_and_missing_run_outcome_cannot_pass(self):
        report = audit(SPEC, PLAN, DECLARATIONS, trx(run_error=True))
        self.assertFalse(report["summary"]["suite_passed"])
        report = audit(SPEC, PLAN, DECLARATIONS, trx().replace(b'executed="1"', b'executed="1" error="1"'))
        self.assertFalse(report["summary"]["suite_passed"])
        self.assertEqual(["error"], report["summary"]["run_counter_errors"])
        with self.assertRaises(ValueError): audit(SPEC, PLAN, DECLARATIONS, trx().replace(b' outcome="Completed"', b''))
    def test_duplicate_execution_cannot_increase_evidence_count(self):
        with self.assertRaises(ValueError): audit(SPEC, PLAN, DECLARATIONS, trx(duplicate=True, total=2))
    def test_counter_mismatch_and_missing_declaration_are_rejected(self):
        with self.assertRaises(ValueError): audit(SPEC, PLAN, DECLARATIONS, trx(total=2))
        changed = {**DECLARATIONS, "tests": DECLARATIONS["tests"][1:]}
        with self.assertRaises(ValueError): audit(SPEC, PLAN, changed, trx())
    def test_unknown_requirement_and_xml_entities_are_rejected(self):
        with self.assertRaises(ValueError): audit(b"| FR-073 | Footprint. |\n", PLAN, DECLARATIONS, trx())
        with self.assertRaises(ValueError): audit(SPEC, PLAN, DECLARATIONS, b'<!DOCTYPE x [<!ENTITY y "data">]>'+trx())

if __name__ == "__main__": unittest.main()
