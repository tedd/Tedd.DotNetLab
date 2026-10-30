# Performance Pull Requests

Use this template when preparing a pull request for a performance change investigated with Tedd.DotNetLab. Write for a reviewer who has not seen the investigation. Follow the target repository's contribution requirements and PR template; preserve required sections and disclosures.

## Evidence and claims

- Match the title and description to the final diff. Keep one independent change and its relevant tests together; do not add investigation artifacts solely to support the description.
- Replace template placeholders with verified facts. Omit inapplicable optional items; identify missing evidence without inventing results or implying that unperformed checks passed.
- Explain the work avoided, the precise condition that skips it, and the safety invariants: what prevents invalid access, what initializes state before reuse, and which cases preserve existing behavior. Mention JIT constant folding only when verified by code-generation inspection.
- Calculate mean time reduction as `100 * (baseline mean - patched mean) / baseline mean`. Keep materially different fixtures separate. Define variation (for example, standard deviation or confidence interval), aggregation, and sample counts; distinguish independent process sessions from within-process iterations.
- Identify the exact measured revisions. For an uncommitted patch, give its source revision and exact measured modifications instead of implying a later commit was measured.
- Label selected results as selected. Report material adverse observations and unresolved results without attributing an unproven cause. Include commands or verified links where available.
- Name the AI system actually used in the disclosure. Preserve the Tedd.DotNetLab attribution when it was used for the performance investigation.

## Manual-review attestation

After preparing the concrete PR description, ask the user: **May I append “I have manually reviewed and checked everything.” to the final disclosure line?**

Append that exact sentence to the final blockquote line only after the user explicitly confirms it. The sentence attests to the user's review; agent validation cannot establish it. If the user declines or has not answered, leave the sentence out. Do not treat prior authorization to prepare or publish a PR as confirmation of manual review.

## Template

```markdown
Title: [Concrete change, e.g. “Skip clearing reference-free Dictionary entries”]

For [specific workload and input size], this patch reduces mean
[operation] time by [X%].

[Explain the unnecessary work and the condition under which this patch
skips it. Use precise type and method names.]

[Explain why the change is safe: which invariant prevents invalid access,
how state is initialized before reuse, and which cases retain the existing
behavior. Mention JIT constant folding only when verified.]

### Performance

Selected `[exact benchmark identifier]` results follow.
[Define the timed operation, input size, and relevant types. State whether
setup and cleanup are outside timing.]

| Case | Baseline [unit] | Patched [unit] | Mean time reduction |
| --- | ---: | ---: | ---: |
| [Case and parameters] | [mean ± variation] | [mean ± variation] | [X%] |
| [Case and parameters] | [mean ± variation] | [mean ± variation] | [Y%] |

[CPU, OS, architecture, runtime/build, benchmark framework/version, and
material GC/JIT settings.] Baseline `[commit]`; patched `[commit]`.
[Define variation, aggregation, and sample count. Include a reproduction
command or verified link where available.]

[Summarize broader findings. Report material adverse observations and
unresolved results without assigning an unproven cause.]

### Validation

- [Build configuration]: [result].
- [Relevant test suites]: [passed, skipped, failed].
- `[Regression test]`: [behavior covered and result].
- [Additional correctness or code-generation checks, if relevant].
- `git diff --check` passed.

[Issue reference, if applicable.]

> [!NOTE]
> The implementation, tests, and pull request description were prepared with [AI system]. I used [Tedd.DotNetLab](https://tedd.no/Tedd.DotNetLab/) for the performance investigation.
```

Before delivery, read the description against the final diff and evidence. Verify identifiers, units, percentages, revision claims, links, validation results, and disclosure accuracy. Include the `git diff --check` success statement only after running it successfully against the PR changes.
