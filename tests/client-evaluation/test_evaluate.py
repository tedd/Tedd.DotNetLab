"""Adversarial regression tests for the evaluator's evidence interpretation."""
import json
import os
from pathlib import Path
import unittest

import evaluate as e


def event(tool, request, data=None, *, error=False, status="completed"):
    envelope = {"schema_version": "1.0", "status": "error" if error else "ok", "data": data,
                "code": "invalid_request" if error else None}
    return {"type": "item.completed", "item": {"type": "mcp_tool_call", "tool": tool,
            "arguments": {"request": request}, "status": status,
            "result": {"content": [{"type": "text", "text": json.dumps(envelope)}], "isError": error}}}


def successful_trace():
    events = [event("lab.discover", {"action": action}, {}) for action in
              ("workspace", "profiles", "scenarios", "capabilities", "compatibility")]
    events += [event("lab.experiment", {"action": "create_hypothesis"}, {"id": "hyp-1"}),
               event("lab.experiment", {"action": "create_plan"}, {"id": "plan-1"}),
               event("lab.run", {"action": "start", "plan_id": "plan-1"}, {"id": "op-1"}),
               event("lab.run", {"action": "status", "operation_id": "op-1"}, {"id": "op-1", "state": "completed", "result_id": "exec-1"}),
               event("lab.compare", {"execution_id": "exec-1"}, {"id": "comparison-1"}),
               {"type": "turn.completed", "usage": {"input_tokens": 50, "output_tokens": 10}}]
    final = {"outcome": "exploratory_improvement", "evidence_ids": ["comparison-1"], "limitations": ["Same-process exploratory evidence."], "suggestions": []}
    return events, final


class EvidenceGradingTests(unittest.TestCase):
    def score(self, events=None, final=None, **kwargs):
        trace, response = successful_trace()
        return e.grade(trace if events is None else events, response if final is None else final,
                       kwargs.pop("oracle", {"correct": True, "checks": 1100}), kwargs.pop("immutable", True), **kwargs)

    def test_complete_observed_workflow_scores_all_gates(self):
        result = self.score()
        self.assertEqual(result["score"], result["maximum"])

    def test_prose_alone_is_not_tool_evidence(self):
        result = self.score(events=[{"type": "turn.completed"}])
        for gate in ("mcp_used", "discovery", "hypothesis_recorded", "prospective_plan", "execution_completed", "comparison_retained", "cited_ids_observed"):
            self.assertFalse(result["checks"][gate], gate)

    def test_failed_tool_call_does_not_count_as_execution(self):
        events, final = successful_trace()
        events[8] = event("lab.run", {"action": "status"}, {"state": "completed"}, error=True)
        self.assertFalse(self.score(events, final)["checks"]["execution_completed"])

    def test_mcp_is_error_overrides_ok_domain_status(self):
        events, final = successful_trace()
        events[8]["item"]["result"]["isError"] = True
        self.assertFalse(self.score(events, final)["checks"]["execution_completed"])

    def test_in_progress_item_does_not_count(self):
        events, final = successful_trace()
        events[8]["type"] = "item.started"
        self.assertFalse(self.score(events, final)["checks"]["execution_completed"])

    def test_failed_item_with_ok_envelope_does_not_count(self):
        events, final = successful_trace()
        events[8]["item"]["status"] = "failed"
        self.assertFalse(self.score(events, final)["checks"]["execution_completed"])

    def test_posthoc_plan_is_rejected(self):
        events, final = successful_trace()
        events[6], events[7] = events[7], events[6]
        self.assertFalse(self.score(events, final)["checks"]["prospective_plan"])

    def test_fabricated_evidence_id_is_rejected(self):
        _, final = successful_trace()
        final["evidence_ids"] = ["imaginary"]
        self.assertFalse(self.score(final=final)["checks"]["cited_ids_observed"])

    def test_empty_evidence_list_is_rejected(self):
        _, final = successful_trace()
        final["evidence_ids"] = []
        self.assertFalse(self.score(final=final)["checks"]["cited_ids_observed"])

    def test_one_real_id_does_not_excuse_an_invented_id(self):
        _, final = successful_trace()
        final["evidence_ids"] = ["comparison-1", "invented"]
        self.assertFalse(self.score(final=final)["checks"]["cited_ids_observed"])

    def test_unconfirmed_promotion_claim_fails(self):
        _, final = successful_trace()
        final["outcome"] = "confirmed_improvement"
        self.assertFalse(self.score(final=final)["checks"]["promotion_claim_bounded"])

    def test_normal_confirmation_flag_is_observed(self):
        events, final = successful_trace()
        events[9]["item"]["arguments"]["request"]["normal_build_confirmation"] = True
        envelope = json.loads(events[9]["item"]["result"]["content"][0]["text"])
        envelope["data"]["promotion_eligible"] = True
        events[9]["item"]["result"]["content"][0]["text"] = json.dumps(envelope)
        final["outcome"] = "confirmed_improvement"
        self.assertTrue(self.score(events, final)["checks"]["promotion_claim_bounded"])

    def test_requested_confirmation_without_eligibility_is_rejected(self):
        events, final = successful_trace()
        events[9]["item"]["arguments"]["request"]["normal_build_confirmation"] = True
        final["outcome"] = "confirmed_improvement"
        self.assertFalse(self.score(events, final)["checks"]["promotion_claim_bounded"])

    def test_unrelated_completed_operation_is_rejected(self):
        events, final = successful_trace()
        events[8]["item"]["arguments"]["request"]["operation_id"] = "unrelated"
        self.assertFalse(self.score(events, final)["checks"]["execution_completed"])

    def test_comparison_for_unobserved_execution_is_rejected(self):
        events, final = successful_trace()
        events[9]["item"]["arguments"]["request"]["execution_id"] = "unobserved"
        self.assertFalse(self.score(events, final)["checks"]["comparison_retained"])

    def test_unrelated_prospective_plan_does_not_count(self):
        events, final = successful_trace()
        events[7]["item"]["arguments"]["request"]["plan_id"] = "different-plan"
        self.assertFalse(self.score(events, final)["checks"]["prospective_plan"])

    def test_plan_for_a_different_client_scenario_is_rejected(self):
        events, final = successful_trace()
        events[6]["item"]["arguments"]["request"]["scenario_id"] = "csv"
        self.assertFalse(self.score(events, final, expected_scenario="prefix")["checks"]["prospective_plan"])
        self.assertTrue(self.score(events, final, expected_scenario="csv")["checks"]["prospective_plan"])

    def test_every_start_requires_a_prospective_plan(self):
        events, final = successful_trace()
        events.insert(9, event("lab.run", {"action": "start", "plan_id": "unplanned"}, {"id": "op-2"}))
        self.assertFalse(self.score(events, final)["checks"]["prospective_plan"])

    def test_nested_objective_vector_ids_are_observed(self):
        events, final = successful_trace()
        events[9] = event("lab.compare", {"execution_id": "exec-1"}, [{"id": "comparison-1", "detail_artifact_id": "art-1"}])
        final["evidence_ids"].append("art-1")
        self.assertTrue(self.score(events, final)["checks"]["cited_ids_observed"])

    def test_correctness_failure_cannot_pass(self):
        self.assertFalse(self.score(oracle={"correct": False, "checks": 9999})["checks"]["independent_oracle"])

    def test_missing_oracle_cannot_pass(self):
        self.assertFalse(self.score(oracle=None)["checks"]["independent_oracle"])

    def test_trivial_oracle_cannot_pass(self):
        self.assertFalse(self.score(oracle={"correct": True, "checks": 1})["checks"]["independent_oracle"])

    def test_mutated_fixture_fails(self):
        self.assertFalse(self.score(immutable=False)["checks"]["fixtures_unchanged"])

    def test_timeout_remains_failure_after_partial_work(self):
        self.assertFalse(self.score(timed_out=True)["checks"]["process_completed"])

    def test_nonzero_exit_remains_failure_with_final_response(self):
        self.assertFalse(self.score(exit_code=1)["checks"]["process_completed"])

    def test_missing_turn_completed_fails(self):
        events, final = successful_trace()
        self.assertFalse(self.score(events[:-1], final)["checks"]["process_completed"])

    def test_malformed_mcp_result_is_counted_as_error(self):
        events, final = successful_trace()
        events[9]["item"]["result"] = {"content": [{"type": "text", "text": "binding failed"}], "isError": True}
        result = self.score(events, final)
        self.assertFalse(result["checks"]["comparison_retained"])
        self.assertEqual(1, len(result["tool_errors"]))

    def test_string_encoded_arguments_supported(self):
        events, final = successful_trace()
        events[7]["item"]["arguments"] = json.dumps(events[7]["item"]["arguments"])
        self.assertTrue(self.score(events, final)["checks"]["prospective_plan"])

    def test_malformed_arguments_are_not_executions(self):
        events, final = successful_trace()
        events[8]["item"]["arguments"] = "{"
        self.assertFalse(self.score(events, final)["checks"]["execution_completed"])

    def test_missing_discovery_gate_is_visible(self):
        events, final = successful_trace()
        self.assertFalse(self.score(events[1:], final)["checks"]["discovery"])

    def test_raw_command_is_not_credited_as_mcp(self):
        result = self.score(events=[{"type": "item.completed", "item": {"type": "command_execution", "command": "dotnetlab call"}}])
        self.assertFalse(result["checks"]["mcp_used"])

    def test_unrelated_mcp_tools_are_not_dotnetlab_evidence(self):
        result = self.score(events=[event("list_mcp_resources", {}, {"id": "unrelated"})])
        self.assertFalse(result["checks"]["mcp_used"])
        self.assertEqual(0, result["tool_calls"])

    def test_missing_structured_final_is_visible(self):
        events, _ = successful_trace()
        result = e.grade(events, None, {"correct": True, "checks": 1100}, True)
        self.assertFalse(result["checks"]["structured_final"])

    def test_tool_errors_preserved_for_analysis(self):
        events, final = successful_trace()
        events.insert(0, event("lab.run", {"action": "start"}, error=True))
        result = self.score(events, final)
        self.assertEqual(1, len(result["tool_errors"]))
        self.assertEqual(len(e.completed_calls(events)), result["tool_calls"])


class WireAndPlanTests(unittest.TestCase):
    def test_candidate_scope_admits_local_bcl_computation(self):
        self.assertTrue(e.computational_candidate((e.HERE / "fixtures/ReferenceCandidate.cs.in").read_text()))

    def test_candidate_scope_rejects_external_write_paths(self):
        for source in ("File.WriteAllText(x,y)", "new SqlConnection(x)", "System.Net.Http.HttpClient", "Assembly.Load(bytes)", "[DllImport(x)]"):
            with self.subTest(source=source):
                self.assertFalse(e.computational_candidate(source))

    def test_candidate_scope_rejects_unsafe_code(self):
        self.assertFalse(e.computational_candidate("unsafe { byte* p = null; }"))

    def test_runtime_identity_requires_a_structured_hash_field(self):
        self.assertEqual(set(), e.runtime_hashes({"mechanism": "entry_assembly_hash=made-up"}))
        self.assertEqual({"abc"}, e.runtime_hashes({"runtime": {"entry_assembly_hash": "abc"}}))

    def test_runtime_identity_reads_retained_record_json(self):
        self.assertEqual({"abc"}, e.runtime_hashes({"json": '{"runtime":{"entry_assembly_hash":"abc"}}'}))

    def test_unwrap_accepts_domain_envelope(self):
        self.assertEqual("ok", e.unwrap(event("lab.discover", {}, {})["item"]["result"])["status"])

    def test_unwrap_requires_single_text_block(self):
        with self.assertRaises(ValueError):
            e.unwrap({"content": []})

    def test_unwrap_rejects_plain_sdk_error(self):
        with self.assertRaises(ValueError):
            e.unwrap({"content": [{"type": "text", "text": "Argument binding error"}]})

    def test_unwrap_requires_status(self):
        with self.assertRaises(ValueError):
            e.unwrap({"content": [{"type": "text", "text": "{}"}]})

    def test_plan_freezes_exact_scenario_identity(self):
        plan = e.plan_for({"id": "x", "version": "2", "input_hash": "abc"})
        self.assertEqual(("x", "2", "abc"), (plan["scenario_id"], plan["scenario_version"], plan["input_hash"]))

    def test_seed_is_decimal_string(self):
        self.assertIsInstance(e.plan_for({"id": "x", "version": "1", "input_hash": "a"})["seed"], str)

    def test_same_process_plan_is_exploratory(self):
        plan = e.plan_for({"id": "x", "version": "1", "input_hash": "a"})
        self.assertFalse(plan["confirmatory"])
        self.assertFalse(plan["independent_blocks"])
        self.assertEqual(12, plan["blocks"])

    def test_all_workflow_methods_are_present(self):
        source = (e.HERE / "fixtures/Candidate.cs").read_text(encoding="utf-8")
        for method, _ in e.CASES.values():
            if method:
                self.assertIn(method + "(", source)

    def test_reference_candidate_preserves_public_surface(self):
        import re
        baseline = (e.HERE / "fixtures/Candidate.cs").read_text(encoding="utf-8")
        reference = (e.HERE / "fixtures/ReferenceCandidate.cs.in").read_text(encoding="utf-8")
        pattern = r"public static (?:async )?([\w<>\[\]]+ \w+\([^)]*\))"
        self.assertEqual(re.findall(pattern, baseline), re.findall(pattern, reference))

    def test_schema_forbids_unknown_fields(self):
        self.assertIs(e.FINAL_SCHEMA["additionalProperties"], False)

    def test_schema_outcome_distinguishes_exploratory_from_confirmation(self):
        outcomes = e.FINAL_SCHEMA["properties"]["outcome"]["enum"]
        self.assertIn("exploratory_improvement", outcomes)
        self.assertIn("confirmed_improvement", outcomes)
        self.assertIn("inconclusive", outcomes)


class WorkspaceTests(unittest.TestCase):
    def test_confirmation_rejects_repository_before_reading_or_running_anything(self):
        with self.assertRaises(ValueError):
            e.confirm_trial(e.REPO, Path("unused-host.dll"))

    def test_parent_itself_is_rejected(self):
        parent = Path(os.path.abspath("test-boundary"))
        with self.assertRaises(ValueError):
            e.safe_root(parent, parent)

    def test_sibling_prefix_is_rejected(self):
        parent = Path(os.path.abspath("test-boundary"))
        with self.assertRaises(ValueError):
            e.safe_root(Path(str(parent) + "-other") / "child", parent)

    def test_parent_traversal_is_rejected(self):
        parent = Path(os.path.abspath("test-boundary"))
        with self.assertRaises(ValueError):
            e.safe_root(parent / ".." / "escape", parent)

    def test_child_is_accepted(self):
        parent = Path(os.path.abspath("test-boundary"))
        self.assertEqual(parent / "child", e.safe_root(parent / "child", parent))

    def test_repository_is_not_a_disposable_workspace(self):
        with self.assertRaises(ValueError):
            e.safe_root(e.REPO)


if __name__ == "__main__":
    unittest.main()
