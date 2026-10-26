namespace Tedd.DotNetLab.Contracts;

public static class RecoveryHints
{
    public const string Version="error-recovery/v1";
    public const int MaximumLength=2048;
    public static string ForCode(string code)=>code switch
    {
        "invalid_request" or "invalid_plan" or "invalid_profile" =>
            "Correct the request against the published action schema, profile list, and plan constraints before submitting it.",
        "schema_unsupported" =>
            "Use a schema/runtime combination explicitly supported by this controller; do not reinterpret the rejected document as the current schema.",
        "capability_unsupported" or "method_unsupported" =>
            "Read the target capability states and reasons. Use retained/offline evidence where available; live inspection or mutation requires an explicitly qualified target and method. Do not invoke a method to obtain code.",
        "permission_denied" =>
            "Have the human operator verify target access and issue a valid grant for the exact action, target, content, and budget. Discovery does not grant permission; do not elevate automatically.",
        "record_not_found" =>
            "Locate an existing record in this workspace's bounded history or use the ID returned by the accepted operation; do not substitute an unrelated record.",
        "invalid_cursor" =>
            "Restart pagination for the requested view/result, using only its returned cursor. Notebook history can change between pages; a cursor does not freeze that history.",
        "revision_conflict" or "stale_revision" =>
            "Read the current revision, review the changed record, and submit a new intentional update with that revision.",
        "stale_epoch" or "target_identity_changed" or "target_gone" =>
            "Rediscover the target and verify PID/start identity, runtime, and epoch. Revalidate plans and grants; do not redirect the original request to a reused PID.",
        "target_busy" or "lease_conflict" =>
            "Inspect the owning operation and wait for verified terminal cleanup. Recheck target identity, epoch, and grant scope before any new operation.",
        "target_tainted" or "recovery_unverified" or "restore_unknown" =>
            "Inspect the durable operation and mutation journal. Keep the target isolated; use explicit qualified recovery only with matching evidence and grants, otherwise dispose of the target. Do not resume measurements automatically.",
        "profiler_timeout" or "profiler_disconnected" or "invalid_profiler_response" =>
            "Inspect operation state and the durable mutation journal before acting. Submission may have occurred; do not resend a mutation or assume restoration. Keep an uncertain target isolated.",
        "artifact_expired" or "artifact_missing" =>
            "Read retained summaries where available. A new raw-dependent analysis requires the original verified artifact; absence cannot be replaced with a zero or reconstructed evidence.",
        "artifact_corrupt" or "integrity_failure" =>
            "Preserve the failure evidence and obtain a verified source artifact. Do not analyze, overwrite, or trust the corrupt payload.",
        "resource_limit" =>
            "Inspect the specific limit and operation state. Use bounded pages or smaller prospective budgets where supported; do not silently change a frozen profile or infer completeness from capped evidence.",
        "idempotency_conflict" =>
            "Retrieve the original operation for this key. Use a new key only for an intentional distinct request; do not replay changed input under the old key.",
        _ => "Inspect the error, retained operation state, and available capability reasons. Resolve the cause before an intentional new request; do not automatically repeat execution or mutation."
    };
}
