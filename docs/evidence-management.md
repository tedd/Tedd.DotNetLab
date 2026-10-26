# Local evidence management

Exports are local, explicit selections. They do not upload data or authorize source changes. The workspace catalog permits one controller at a time. Raw traces, code, provider text, paths, and application names can contain sensitive information.

Linux catalog workspaces require ext4, XFS, Btrfs, or local overlayfs. Windows-backed WSL/v9fs, network, FUSE, volatile, and unknown filesystem types are rejected before opening SQLite. Catalog and WAL sidecars need the same local shared-memory and locking semantics; [SQLite's WAL contract](https://www.sqlite.org/wal.html) defines this requirement.

## Prepare and authorize

`lab.experiment` with `action=prepare_export` takes `record_ids`, `artifact_ids`, `privacy_filtered`, `maximum_bundle_bytes`, and `maximum_duration_seconds`. The byte limit is an exact decimal Int64 string; defaults are 1 GiB and 300 seconds. The combined inclusion list is distinct and bounded to 1,000 items. Complete selected artifact hashes are verified; selected record revisions and hashes are frozen. Later narrative revisions do not alter the plan. A selection of control grants is rejected.

The default privacy-filtered policy exports record identity/kind/revision/hash metadata and omits record payloads and original artifact bytes. It records every omission in the manifest. This policy does not attempt to remove sensitive strings from arbitrary binary traces, code, or JSON, and it is not an anonymization claim. An artifact omitted by this policy is not silently advertised as included. Source hashes remain distinct from hashes of redacted exported metadata.

The local CLI issues an exact-plan grant:

```text
dotnetlab grant-export <workspace> <plan-id> <seconds> <maximum-operations>
dotnetlab grant-export <workspace> <plan-id> <seconds> <maximum-operations> --include-raw-traces
```

Issue the grant while no workspace controller is running, then restart the MCP controller. The second form explicitly permits the selected original artifact bytes for an unfiltered export. All opaque original artifacts require that consent, regardless of their media-type label; retagging a trace or embedding it in another artifact does not remove the consent boundary. An unfiltered record-only plan requires the ordinary exact-plan export grant. MCP has no grant-issuance action.

## Create and verify

`lab.run` with `action=start_export` takes the plan ID, grant ID, and idempotency key. It returns a durable operation ID promptly. Grants bind workspace, exact plan hash, expiry, and operation budget, using the separate `export_evidence` permission. A repeated key returns the original operation; a changed request hash is rejected. Cancellation, duration, disk reserve, and streaming byte limits are explicit.

The bundle is a content-addressed ZIP artifact. Record entry names are generated hashes, so record IDs cannot create traversal paths. Original artifact entries use their SHA-256 hashes. The manifest contains the schema and policy versions, inclusion/omission lists, source and exported hashes, source record revisions, lengths, media types, and redaction flags. Older bundles can omit the source revision; import preserves that revision as unknown. Only the explicit inclusion list is exported; the bundle does not claim a complete dependency closure for unselected records or artifacts.

Before completion, the writer flushes and closes the archive, reopens it, validates entry uniqueness/layout, verifies the manifest and every member hash/length under a decompressed-byte budget, and seals the bundle in the artifact store. A result is published only after this verification. The export artifact is pinned and the catalog appends an `evidence_exported` audit event. Partial or corrupt input cannot produce a verified result. Artifact-store and catalog paths reject reparse-point ancestors.

`lab.query` with `view=verify_export` and `result_id` verifies the retained archive and returns its manifest. `artifact_slice` exposes bounded reads using the result's artifact ID; no caller-selected destination path or shell command is accepted. The manifest remains independently readable in the ZIP. Verification failure is explicit if the bundle is missing or its bytes differ from the catalog hash.

## Import and query a bundle

The local CLI `import <workspace> <path> application/zip` seals an explicitly supplied bundle in the destination workspace. ZIP input is bounded by the workspace capture-byte limit and at most 2 GiB; other supported file imports retain their 64 MiB cap. Available disk space must preserve the workspace reserve. File ingestion does not load or execute managed code or establish target access. MCP uses the retained artifact ID rather than an arbitrary filesystem path.

`lab.experiment.prepare_bundle_import` takes `artifact_id`, `maximum_decompressed_bytes` as an exact decimal Int64 string (default 1 GiB, maximum 2 GiB), and `maximum_duration_seconds` (default 300, maximum 600). Preparation freezes the bundle hash and manifest hash and validates its declared layout and budget. `lab.run.import_bundle` takes the plan ID and idempotency key and returns a durable operation ID. Ordinary status/cancel actions apply. An identical retry returns its original operation; changed requests conflict.

Import supports the versioned single-disk export ZIP layout with at most 1,000 selected members plus the manifest. Central-directory metadata is bounded before ZIP member enumeration; split/ZIP64 layouts are rejected. The reader rejects duplicate paths, traversal, unsupported schemas/policies, forged control-authority members, inconsistent revisions/privacy/omissions, checksum failures, and actual decompressed bytes exceeding the declared bounds. It writes generated content-addressed artifacts without extracting caller-controlled filenames. Every original or redacted record payload retains its exact bytes; unfiltered record JSON also verifies against its semantic catalog hash.

After complete verification and durable artifact sealing, imported member indexes, artifact manifests/pins, import result, terminal operation status, and owned-lease release publish in one catalog transaction. Failure or cancellation cannot publish a partially successful member set. Staged files from an interruption remain subject to ordinary artifact recovery. Imported source IDs/kinds/revisions are data fields under new local member IDs; they never overwrite local notebook records, grants, targets, candidates, or completed operations. Qualification is `imported_hash_verified_origin_unverified`: hashes establish integrity, not authenticity or certification of the source experiment.

`lab.query.verify_bundle_import` takes the result ID and verifies its retained receipt, input archive, members, and payload hashes. `imported_members` takes `import_id`, a page limit up to 100, and an import-bound cursor. Each member links the original source identity/revision/hash and its local payload artifact. Use ordinary `record` and `artifact_slice` views for bounded review. Redacted records expose metadata only, and omitted raw payloads remain explicitly unavailable. Imported manifests and selected payloads are pinned.

## Reprocess an imported capture

An unfiltered bundle can include a completed capture record and its referenced raw NetTrace artifact. `lab.run.analyze_imported_capture` takes that imported capture member ID and an idempotency key. The job verifies the import, capture payload, supported exact profile, and included raw bytes; no source process or original controller needs to be running. Missing raw bytes or a redacted capture returns `not_collected`. It creates a separate local capture/analysis lineage and preserves the archived process identity for offline interpretation, without publishing a live target or grant.

Use the resulting analysis ID with ordinary GC, counter, metric, and availability queries. Imported-source analyses retain `imported_source_origin_unverified` and partial availability even when parsing succeeds. A later reanalysis produces new derived evidence; previous analysis indexes remain immutable. A requested timing/count metric unsupported by the retained evidence remains `not_collected`; import does not certify exact invocation timing or grant promotion eligibility.

## Retention and deletion

`lab.experiment` with `action=prepare_retention` takes `maximum_raw_trace_bytes` as an exact decimal Int64 string and `maximum_age_days`. It freezes a proposed oldest-first sweep of unprotected NetTrace artifacts. An age of zero makes every existing unprotected raw trace eligible. Summary artifacts and catalog records are preserved. The plan reports retained and projected raw-trace sizes, protected artifact IDs, and quota pressure when pins or owner references prevent reaching the quota. Planning does not remove bytes. Sweeps are explicit operations; the controller does not run an automatic background deletion policy.

`action=prepare_deletion` takes a distinct `artifact_ids` selection and a `reason`, with at most 1,000 artifacts. It can select other artifact types explicitly. Baseline, confirmation, comparison, native recovery, and export pins prevent deletion. `action=pin_evidence` takes an artifact ID and an existing record's `owner_id`; each owner retains an independent indefinite reference. Catalog references can also carry an explicit expiry. Repeating a reference can extend its lifetime but cannot shorten it, and advances its persistent revision. `lab.query` with `view=artifact_retention` reports pins, active owner count, and deletion state. `view=artifact_references` takes the artifact ID, a page limit up to 100, and an optional artifact-bound cursor; it reports owner and reference revisions from a consistent page read. This live view can change between page requests.

After reviewing the plan, the local CLI issues the separate permission:

```text
dotnetlab grant-deletion <workspace> <plan-id> <seconds> <maximum-operations>
```

`lab.run` with `action=start_deletion` takes `plan_id`, `grant_id`, and `idempotency_key`. The grant binds the workspace, complete selection and policy hash, expiry, and operation budget. The catalog rechecks every protection and consumes the grant in the same transaction that commits the complete batch of deletion intents. A new pin or reference established after planning prevents the entire batch from starting.

Cancellation applies before the intent commits. After commitment, the controller finishes file removal; interruption leaves durable pending intents that startup retries without widening the original authorized selection. A committed tombstone blocks later references, pins, reads, and resealing identical content. Physical removal and its audit event are idempotent. The immutable artifact manifest, plan, summaries, and decisions remain available; new raw-dependent reads return `artifact_expired`. Successful deletion results include the exact plan hash and removed byte count. A filesystem failure can leave a pending intent for startup retry and an unsuccessful original operation; it does not fabricate successful completion.

## Release user references

`lab.experiment` with `action=prepare_reference_release` takes `selection` as 1–100 distinct `{artifact_id, owner_id}` pairs and a bounded `reason`. It freezes each reference's revision and expiry together with its owner's record revision and hash. The local CLI issues an exact-plan `delete_evidence` grant:

```text
dotnetlab grant-release <workspace> <plan-id> <seconds> <maximum-operations>
```

`lab.run` with `action=release_references` takes `plan_id`, `grant_id`, and `idempotency_key`. Re-pinning, extending a reference, revising its owner, or releasing and recreating the same pair invalidates the prior selection. The catalog validates the whole batch before committing any release. Grant consumption, reference changes, audit events, the immutable result, terminal status, and owned-lease release share one transaction; a failed attempt leaves them unchanged.

Releasing one owner preserves other owners' references. It does not remove artifact bytes, manifests, owner records, or protected system pins. Raw-byte deletion requires a separate reviewed deletion plan and grant. Persistent reference revisions survive release and re-creation, preventing an old grant from applying to a newly established promise. System baseline, confirmation, comparison, recovery, and export pins cannot be released by this interface.

## Artifact recovery inventory

`lab.run` with `action=recover_artifacts` takes `idempotency_key`, `maximum_entries` (default 10,000), `maximum_verified_bytes` (an exact decimal Int64 string, default 16 GiB), and `maximum_duration_seconds` (default 300). It performs a bounded local inventory and returns a durable operation ID. No target process is invoked and no file is deleted. `lab.query` with `view=artifact_recovery`, `result_id`, `limit`, and an optional cursor pages the sealed report's findings. The index reports recovered manifest count, attempted verification bytes, and incomplete coverage. Entry limits count catalog manifests and visited filesystem entries, including directories. Budget exhaustion is explicit; an incomplete inventory does not certify the whole workspace.

Sealing flushes the artifact, computes its content hash, and flushes a small versioned receipt beside the incoming partial file before moving its bytes. A receipt preserves the exact artifact media type, producer, schema, completion flag, and manifest hash. Recovery registers an orphaned manifest only when the receipt validates, its original partial file is absent, and the sealed content hash and length verify. An incomplete artifact keeps its incomplete flag. Recovery does not infer an owning capture, execution, run, or permission from the receipt. Files without valid receipts remain unclaimed; their bytes may verify without establishing their origin or completion state.

The report distinguishes verified catalog artifacts, missing files, corrupt bytes, authorized expiry, malformed or conflicting receipts, partial writes, unsafe paths, and unavailable verification caused by budgets or filesystem access. A partial file observed beside a receipt does not prove its writer has stopped. Tombstones prevent recovery from restoring deleted content. Reparse entries and unexpected directory layouts are not followed. Receipts, partial files, and bare orphans are preserved for review; this inventory does not authorize their deletion. Findings describe observations during the scan, rather than a filesystem snapshot that remains valid after later changes.

## Catalog migrations, backups, and checkpoints

The catalog supports contiguous schema versions 1 through 4. Packaged SQL migrations define each table, index, primary key, and foreign key explicitly. Opening a catalog checks its recorded history, exact schema definitions, SQLite integrity, foreign-key integrity, and agreement between active references and persistent reference revisions before applying migrations. Future versions, gaps, missing columns, unexpected triggers, and other schema deviations are rejected. Schema upgrades run in one transaction and append their audit event within that transaction. A failed upgrade rolls back all of its steps. SQLite files, sidecars, and backup paths reject reparse points.

Before upgrading a supported older catalog, the controller creates a consistent snapshot under `migration-backups` using SQLite's backup mechanism. When a readable SQLite catalog fails schema validation, it also preserves a snapshot and reports its path in the recovery hint. Snapshots carry flushed SHA-256/length/schema manifests; existing backup destinations cannot be overwritten. Failure to read or back up damaged SQLite storage prevents migration and preserves the source files for manual recovery. The controller does not restore a snapshot over an active catalog automatically.

`lab.run` with `action=backup_catalog` and `idempotency_key` creates a local snapshot job. `lab.query` with `view=verify_catalog_backup` and `backup_id` checks the snapshot hash, size, SQLite integrity, and recorded schema. Snapshots are bounded to 2 GiB and preserve the workspace disk reserve (256 MiB by default). Native SQLite copying runs to quiescence; cancellation is checked before copying and before publishing a successful result. A catalog snapshot contains committed database records, including control records, and stays beneath the local workspace. It does not include raw artifact files or claim a complete workspace restore package.

`lab.run` with `action=checkpoint_catalog` and `truncate` requests a passive or truncating WAL checkpoint. Its result reports the busy flag and log/checkpointed frame counts; an active reader can prevent truncation without invalidating committed data. The CLI exposes the same local maintenance operations:

```text
dotnetlab backup <workspace>
dotnetlab checkpoint <workspace>
dotnetlab checkpoint <workspace> --truncate
```

Catalog writes use one logical writer with a five-second admission bound. SQLite busy/locked failures roll back the attempted transaction and retry at most three times, with 50 and 100 millisecond delays and a one-second SQLite command timeout. Exhaustion returns `catalog_busy`. Replaying a failed request's original idempotency key preserves its identity; retry never commits a duplicate entity or operation. Backup files and their manifests remain available even if later result publication is interrupted.

## Runtime counter windows

`runtime-metrics-context` is a versioned capture profile for `System.Diagnostics.Metrics`, restricted to the `System.Runtime` meter with a one-second refresh interval and bounded time-series/histogram counts. Existing diagnostic profiles retain the separate older `System.Runtime.EventCounters` adapter. The modern adapter preserves meter version, instrument identity/type, scope hash, session, and opaque tags in each series identity. Names, units, kinds, and sources are not silently aliased or combined. Supported modern value schemas are version 2; instrument reporting is version 3, qualified against the installed .NET 10.0.12 assembly and live EventPipe output. Histogram summaries do not become individual latency observations.

`lab.query` with `view=counter_window` takes `analysis_id`, `name`, `start_milliseconds`, and `end_milliseconds`, with optional exact `kind`, `source`, and `series` selectors. Coordinates are in the retained trace time domain. The result names its aggregation, unit, covered duration, reset count, quality warnings, and provenance. More than one matching series or incompatible representation is an explicit ambiguity. The raw trace must remain retained and verified; expiration returns `artifact_expired`.

Gauges containing reported interval means use a duration-weighted aggregation of complete reported intervals. These reported means can originate from polling or sampled measurements and are not an exact continuous-time average. Instantaneous modern gauge values use a separately named arithmetic sample mean and remain partial contextual evidence. Complete interval increments are summed. Qualified per-second rates use a duration-weighted mean; display names containing “rate” do not establish rate semantics. Cumulative totals use nonnegative differences between retained observations. A decrease excludes that reset interval, reports incomplete coverage, and never contributes a negative event count.

The modern EventSource's `rate` field contains an interval delta; it is retained as `interval_increment` alongside the separately identified cumulative total. Collection duration comes from the producer's start/end clock fields. TraceEvent's collection task/opcode names (`Collection/Start`, `Collection/Stop`) are explicitly supported. The trace anchor represents collection publication and is labeled contextual; it is not an exact scenario boundary. Missing metadata, collection boundaries, unsupported schemas, provider errors, and missing observations affect availability rather than manufacturing zero values.

No observation is prorated into an unobserved subinterval. A two-millisecond request cannot receive an exact value from one-second evidence. Partial coverage aggregates only the complete covered observations and identifies the uncovered portion; it does not imply coverage of the full requested interval. A retained observed zero and an absent metric have distinct status/value representations. Counter windows describe process context, not selected-method allocation, invocation counts, CPU attribution, or a causal diagnosis.

Runtime metric definitions and units are documented in [Microsoft's runtime metric reference](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime). The EventPipe transport contract is defined by [the .NET 10 MetricsEventSource implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.DiagnosticSource/src/System/Diagnostics/Metrics/MetricsEventSource.cs).

## Sampled-stack attribution

`lab.run` with `action=analyze_stacks` takes a sampling-enabled `capture_id`, an `idempotency_key`, and an optional `policy`. The offline job verifies the raw trace, converts it with the pinned TraceEvent library, and attributes retained sample stacks. It does not attach to or invoke the original target. Managed names come from retained rundown metadata; external symbol lookup, binary loading, and PDB lookup are disabled. Unresolved native/helper addresses remain unnamed and reduce reported symbol coverage. Frame identities are scoped to the raw trace, process, and recorded module/method identity; unresolved frames retain exact addresses.

The default policy admits 128 MiB of raw trace, 512 MiB of temporary conversion output, 250,000 indexed events, 100,000 selected samples, 256 frames per stack, 10,000 distinct frame/method identities, and 300 seconds. Optional `start_milliseconds` and `end_milliseconds` select a half-open interval in trace coordinates. Source reading checks cancellation and temporary bytes; conversion and iteration honor declared event/sample/depth limits. The conversion pass can scan bookkeeping beyond the indexed-event cap, so raw-byte and duration limits remain separate. Reaching an indexing or sample cap is reported as partial evidence. Cardinality or byte-budget exhaustion fails explicitly. Conversion logs and intermediate indexes are confined to the operation's incoming directory and closed before cleanup. Bounded phase and job failure records are available through notebook history.

The report counts the leaf as self and each distinct method/frame identity once per sample as inclusive presence. Recursion therefore contributes one inclusive presence for a method in that sample. Denominators include all selected observed samples, including samples without retained stacks. Frame-based symbol coverage names its denominator separately; missing and depth-truncated stacks remain explicit. Inclusive shares across nested methods may sum above one and are not renormalized. These counts/shares cannot establish invocation counts, per-call averages, or measured on-CPU time.

`lab.query` with `view=hotspots` takes `result_id`, `limit`, optional `cursor`, and `ranking=self|inclusive`. Pages are bound to the retained report and ranking. `view=sampled_method` takes `result_id` and an exact frame identity or retained method name. A missing selector in a report with observed samples returns zero selected observed counts, while a report with no samples returns unavailable counts. A name matching multiple distinct identities is ambiguous and does not merge their counts. Names are stored evidence, not executable requests.

The sealed report and its quality/provenance index survive restart and are pinned. They remain readable after the raw trace expires; producing a new time-window report requires retained, verified raw evidence and returns `artifact_expired` otherwise. The stack convention and recorded counts are independent of notebook decisions or later model interpretations. The offline stack API is documented by [the TraceEvent library](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/TraceLog.cs).
