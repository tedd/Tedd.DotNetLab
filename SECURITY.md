# Security and vulnerability reporting

Tedd.DotNetLab operates under the developer's local operating-system account. Use a private local workspace and trusted application builds. Workspace grants limit controller actions; they do not isolate an application or defend against another process that can modify the same account's files. A registered scenario or normal-build confirmation executes application code, including startup and cleanup. Review those effects and the resolved environment before granting execution.

Diagnostic traces, scenario inputs, symbols, and retained notebook records may contain sensitive information. Inspect the export inclusion list and privacy policy before sharing evidence. A filtered export can omit raw traces; it does not redact an included NetTrace in place. Keep workspace permissions restricted and do not include credentials or raw diagnostic files in a public report.

Report a suspected authorization bypass, unsafe native acquisition, incorrect restoration, or fabricated evidence privately to the repository maintainer. Use the repository's private vulnerability-reporting facility when available, or a verified private contact channel. Include the exact source revision, compatibility report, action/error codes, and a minimal disposable reproduction. Share only the evidence needed to reproduce the problem after reviewing it for sensitive data.

Stop using the affected capability while an integrity or restoration defect is unresolved. Preserve its operation, mutation journal, and artifact manifests. An uncertain mutation requires reconciliation; repeated application is not a recovery procedure. A tainted target requires the documented recovery or developer-directed restart. Continue interpreting retained evidence according to its quality and availability status.

Dependency/runtime changes require locked restoration and capability qualification. The package's notices inventory records license metadata and content hashes; it is not a vulnerability scan. Authenticode observations and archive hashes are separate from runtime qualification. Authenticate a distribution's hash through an independent trusted channel before extraction or execution.

See the [threat model](docs/threat-model.md), [native qualification and recovery](docs/native-qualification.md), and [evidence export/retention](docs/evidence-management.md).
