# Tedd.DotNetLab

**A scientific .NET profiling, benchmarking, and performance-engineering laboratory for AI coding agents (Codex, Claude Code, Cursor, & Google Antigravity).**

When you ask an AI agent to optimize or debug a .NET application on its own, it typically guesses at bottlenecks, writes throwaway `Stopwatch` loops skewed by JIT tiering and GC noise, and declares victory off a single lucky run.

**`Tedd.DotNetLab`** gives AI agents a real, measurement-grade .NET performance laboratory via **Model Context Protocol (MCP)** and a companion **Agent Skill** (`dotnetlab`):
- **Works with any modern .NET target (`6.0+` through `.NET 11 RC` / `.NET 12+` previews):** Profile, benchmark, inspect JIT assembly, and confirm builds on `.NET 6`, `.NET 8`, `.NET 9`, `.NET 10`, bleeding-edge previews like **`.NET 11 RC2`**, or local `corerun` builds without version locks.
- Captures live **EventPipe CPU, allocation, GC pause, and runtime counter** traces from running .NET processes and parses them in memory-bounded isolated workers.
- Runs **preregistered, randomized block experiments** ($A/B$, multi-variant, $2 \times 2$ factorial, and cluster-bootstrapped p95/p99 tail quantiles) with built-in correctness validation and allocation guardrails.
- Optionally inspects **live JIT x64 assembly**, installs bounded **method probes**, and tests sub-second **live CIL ReJIT** candidates via a native C++ `ICorProfilerInfo12` adapter.
- Confirms winning optimizations in **fresh, diagnostics-disabled OS processes** before changes are promoted to source code, while keeping a **human-in-the-loop CLI grant gate** over every live attach, execution, or mutation.

---

## What Can You Use It For?

| Use Case | What `Tedd.DotNetLab` Does |
| --- | --- |
| **Find Real CPU & Allocation Bottlenecks** | Attaches EventPipe to any running `.NET` process (zero code changes needed), aggregates inclusive/exclusive call-stack hotspots, tracks per-generation GC pauses, and correlates GC overlap with slow windows. |
| **Prove Code Optimizations Scientifically** | Replaces ad-hoc timing scripts with seeded randomized complete block designs, warmup control, semantic output verification, paired log-ratio $t$-intervals, Holm-Bonferroni correction, and TOST equivalence tests. |
| **Test Preview & Bleeding-Edge Runtimes (`.NET 11 RC2`, `.NET 12+`)** | Evaluates new JIT/GC/BCL improvements or runtime regressions across any installed `.NET` shared framework (`6.0+`, `8.0`, `9.0`, `10.0`, `11.0-rc.*`, `12.0-*`) or local `Core_Root` builds with optional exact `target_runtime_version` pinning. |
| **Eliminate Allocations Without Regressing Throughput** | Evaluates multi-objective Pareto vectors (for example, `scenario.ns_per_operation` primary metric + `allocation.thread_bytes` guardrail) so throughput wins that silently bloat GC pressure are caught automatically. |
| **Cut p99 / Tail-Latency Spikes** | Captures per-operation latency vectors and runs cluster-bootstrapped quantile comparisons (p50, p95, p99, p99.9) across independent blocks. |
| **Test Whether Two Optimizations Stack ($2 \times 2$ Factorial)** | Evaluates Factor $A$ (e.g., `Span<T>` parsing) $\times$ Factor $B$ (e.g., `ArrayPool<T>` reuse) to measure main effects *and* interaction ($AB$)—revealing whether two ideas compound or cancel each other out. |
| **Inspect JIT x64 Assembly & Prototype via Live ReJIT** | Disassembles actual tiered JIT x64 machine code (`Iced`), computes instruction diffs, and tests bounded CIL replacements in-memory with automatic rollback on failure. |
| **Localize Regressions & Confirm in Clean Processes** | Preserves immutable hypotheses, baselines, and artifacts in a local SQLite catalog across restarts, and validates final source candidates across 20+ fresh, profiler-free OS processes (`start_confirmation`). |

---

## Prerequisites

- **OS & Architecture:** Windows x64 or Linux x64 (including Ubuntu 24.04 on WSL2).
- **Controller Runtime:** Any **.NET 10.0 or newer** runtime or SDK—including `.NET 10`, **`.NET 11` previews / RCs** (e.g., `.NET 11 RC2`), and `.NET 12+` (`RollForward=Major` + prerelease roll-forward enabled out of the box).
- **Target Workloads:** Any CoreCLR **.NET 6.0 or newer** target process (`.NET 6`, `.NET 8`, `.NET 9`, `.NET 10`, `.NET 11` previews/RCs/GA, `.NET 12+`).
- **AI Client:** [Codex](https://github.com/openai/codex), [Claude Code](https://docs.anthropic.com/en/docs/claude-code), [Cursor](https://cursor.com), and/or [Google Antigravity](https://antigravity.google) (`agy`).

---

## Installation

You can install `Tedd.DotNetLab` for **Codex**, **Claude Code**, **Cursor**, **Google Antigravity**, or **all four at once**—via a one-line shell command, an AI self-install prompt, building from source, or manual configuration.

### Option 1: One-Line Shell Installer (Recommended)

Downloads the latest GitHub Release (verifying SHA-256 checksums, or automatically building from source if no prebuilt release matches), places the `dotnetlab` CLI on your user `PATH`, copies the `dotnetlab` skill, and registers the MCP server across **Codex, Claude Code, Cursor, and Antigravity** without touching your other settings.

**Windows (PowerShell):**
```powershell
irm https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.ps1 | iex
```

**Linux / WSL2 (Bash):**
```bash
curl -fsSL https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.sh | bash
```

<details>
<summary><strong>Installer options (install from source, select specific AI clients, or enable native ReJIT tool)</strong></summary>

**Install directly from a local git clone (from source):**
```powershell
# Windows PowerShell
git clone https://github.com/tedd/Tedd.DotNetLab.git
cd Tedd.DotNetLab
pwsh -NoProfile -File ./tools/install.ps1 -FromSource
```
```bash
# Linux / WSL2 Bash
git clone https://github.com/tedd/Tedd.DotNetLab.git
cd Tedd.DotNetLab
./tools/install.sh --from-source
```

**Customize via environment variables on the one-liner (or script flags locally):**
```powershell
# Configure Cursor and Antigravity only:
$env:DOTNETLAB_CLIENT='cursor,antigravity'; irm https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.ps1 | iex

# Configure Codex only + enable the optional lab.patch native ReJIT tool:
$env:DOTNETLAB_CLIENT='codex'; $env:DOTNETLAB_ENABLE_NATIVE='1'; irm https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.ps1 | iex

# Configure Claude Code only, building from source:
$env:DOTNETLAB_CLIENT='claude'; $env:DOTNETLAB_FROM_SOURCE='1'; irm https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.ps1 | iex
```
</details>

---

### Option 2: Give an Install Prompt Directly to Your AI

Paste either prompt below into **Codex**, **Claude Code**, **Cursor**, or **Antigravity** and let the agent install `Tedd.DotNetLab` for you:

**Prompt A — Install from GitHub Release (with automatic source fallback):**
```text
Install Tedd.DotNetLab from https://github.com/tedd/Tedd.DotNetLab for my AI coding tools.
On Windows PowerShell run:
  irm https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.ps1 | iex
On Linux/WSL bash run:
  curl -fsSL https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.sh | bash
Then run `dotnetlab compatibility` to verify the installation and tell me to reload/restart my session so the `dotnetlab` MCP server and skill load.
```

**Prompt B — Build and install from source:**
```text
Clone https://github.com/tedd/Tedd.DotNetLab.git into a temporary directory, build and install it from source using `./tools/install.ps1 -FromSource` (Windows) or `./tools/install.sh --from-source` (Linux/WSL) so both the `dotnetlab` skill and MCP server are configured, verify with `dotnetlab compatibility`, and remove the temporary clone when finished.
```

---

### Option 3: Built-In `dotnetlab install` CLI

If you already built the repo (`dotnet build Tedd.DotNetLab.sln -c Release`) or extracted a release ZIP, `Tedd.DotNetLab.Host` includes a built-in idempotent installer that bundles `skills/dotnetlab`:

```bash
# Install for all supported clients: Codex, Claude Code, Cursor, & Antigravity (user scope):
dotnet /path/to/Tedd.DotNetLab.Host.dll install all

# Or install for one or more specific clients (comma-separated supported):
dotnet /path/to/Tedd.DotNetLab.Host.dll install codex
dotnet /path/to/Tedd.DotNetLab.Host.dll install claude
dotnet /path/to/Tedd.DotNetLab.Host.dll install cursor
dotnet /path/to/Tedd.DotNetLab.Host.dll install antigravity
dotnet /path/to/Tedd.DotNetLab.Host.dll install cursor,antigravity

# Install scoped to a specific project directory (.codex/, .claude/, .cursor/, .agents/, .mcp.json):
dotnet /path/to/Tedd.DotNetLab.Host.dll install all --scope project --project-dir /path/to/your-app

# Include the optional lab.patch native CIL/ReJIT MCP tool:
dotnet /path/to/Tedd.DotNetLab.Host.dll install all --enable-native-extension

# Uninstall cleanly (preserves all other MCP servers and settings):
dotnet /path/to/Tedd.DotNetLab.Host.dll uninstall all
```

**Or install via NuGet / `.NET Tool` packages (from NuGet or GitHub Release/Actions `.nupkg` artifacts):**
```bash
# Install the dotnetlab CLI + MCP host globally and register skills/MCP for your AI clients:
dotnet tool install -g Tedd.DotNetLab
dotnetlab install all

# Add the cooperative benchmark harness library to a target .NET project:
dotnet add package Tedd.DotNetLab.Harness
```

---

### Option 4: Manual Setup Instructions

<details>
<summary><strong>Manual setup for Codex</strong></summary>

1. **Copy the skill** into `$CODEX_HOME/skills/dotnetlab` (defaults to `~/.codex/skills/dotnetlab`, or `<your-project>/.agents/skills/dotnetlab` for project scope):
   ```bash
   mkdir -p ~/.codex/skills
   cp -R /path/to/Tedd.DotNetLab/skills/dotnetlab ~/.codex/skills/dotnetlab
   ```
2. **Initialize a workspace** for experiment catalogs and trace artifacts:
   ```bash
   dotnet /path/to/Tedd.DotNetLab.Host.dll init ~/.local/share/dotnetlab/workspace
   ```
3. **Register the MCP server** in `~/.codex/config.toml` (use escaped backslashes `\\` on Windows):
   ```toml
   [mcp_servers.dotnetlab]
   command = "dotnet"
   args = ["--roll-forward", "Major", "/absolute/path/to/Tedd.DotNetLab.Host.dll", "connect", "/absolute/path/to/workspace"]

   [mcp_servers.dotnetlab.env]
   DOTNET_ROLL_FORWARD_TO_PRERELEASE = "1"
   ```
   *(Optional: append `"--enable-native-extension"` to `args` to expose `lab.patch`.)*
4. Restart Codex. Invoke `$dotnetlab` explicitly or ask any .NET profiling/performance question.
</details>

<details>
<summary><strong>Manual setup for Claude Code</strong></summary>

1. **Copy the skill** into `~/.claude/skills/dotnetlab` (user-wide) or `<your-project>/.claude/skills/dotnetlab` (project-scoped):
   ```bash
   mkdir -p ~/.claude/skills
   cp -R /path/to/Tedd.DotNetLab/skills/dotnetlab ~/.claude/skills/dotnetlab
   ```
2. **Register the MCP server** via the `claude` CLI:
   ```bash
   claude mcp add --scope user -e DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 dotnetlab -- dotnet --roll-forward Major /absolute/path/to/Tedd.DotNetLab.Host.dll connect /absolute/path/to/workspace
   ```
   Or merge into `~/.claude.json` (user scope) or `<your-project>/.mcp.json` (project scope):
   ```json
   {
     "mcpServers": {
       "dotnetlab": {
         "type": "stdio",
         "command": "dotnet",
         "args": [
           "--roll-forward",
           "Major",
           "/absolute/path/to/Tedd.DotNetLab.Host.dll",
           "connect",
           "/absolute/path/to/workspace"
         ],
         "env": {
           "DOTNET_ROLL_FORWARD_TO_PRERELEASE": "1"
         }
       }
     }
   }
   ```
3. Restart Claude Code. Invoke `/dotnetlab` or describe your .NET optimization goal directly.
</details>

<details>
<summary><strong>Manual setup for Cursor</strong></summary>

1. **Copy the skill** into `~/.cursor/skills/dotnetlab` (user-wide) or `<your-project>/.cursor/skills/dotnetlab` (project-scoped):
   ```bash
   mkdir -p ~/.cursor/skills
   cp -R /path/to/Tedd.DotNetLab/skills/dotnetlab ~/.cursor/skills/dotnetlab
   ```
2. **Register the MCP server** in `~/.cursor/mcp.json` (user-wide) or `<your-project>/.cursor/mcp.json` (project-scoped):
   ```json
   {
     "mcpServers": {
       "dotnetlab": {
         "type": "stdio",
         "command": "dotnet",
         "args": [
           "--roll-forward",
           "Major",
           "/absolute/path/to/Tedd.DotNetLab.Host.dll",
           "connect",
           "/absolute/path/to/workspace"
         ],
         "env": {
           "DOTNET_ROLL_FORWARD_TO_PRERELEASE": "1"
         }
       }
     }
   }
   ```
3. Reload Cursor (**Cursor Settings → Tools & MCP**) so `dotnetlab` tools and skill activate.
</details>

<details>
<summary><strong>Manual setup for Google Antigravity (`agy`)</strong></summary>

1. **Copy the skill** into `~/.gemini/config/skills/dotnetlab` (global user scope) or `<your-project>/.agents/skills/dotnetlab` (workspace scope):
   ```bash
   mkdir -p ~/.gemini/config/skills
   cp -R /path/to/Tedd.DotNetLab/skills/dotnetlab ~/.gemini/config/skills/dotnetlab
   ```
2. **Register the MCP server** in `~/.gemini/config/mcp_config.json` (global user scope) or `<your-project>/.agents/mcp_config.json` (workspace scope):
   ```json
   {
     "mcpServers": {
       "dotnetlab": {
         "type": "stdio",
         "command": "dotnet",
         "args": [
           "--roll-forward",
           "Major",
           "/absolute/path/to/Tedd.DotNetLab.Host.dll",
           "connect",
           "/absolute/path/to/workspace"
         ],
         "env": {
           "DOTNET_ROLL_FORWARD_TO_PRERELEASE": "1"
         }
       }
     }
   }
   ```
3. Start a new Antigravity session (or run `agy mcp list`) to verify `dotnetlab` is connected.
</details>

---

## Example Prompts to Give Your AI

Once installed, restart or reload your AI coding agent so the `dotnetlab` MCP tools (`lab.discover`, `lab.inspect`, `lab.experiment`, `lab.run`, `lab.query`, `lab.compare`) and the `dotnetlab` skill are active.

> [!IMPORTANT]
> **How Human Security Grants Work:** By design, an AI agent cannot silently attach to live processes, execute code, or launch builds without your consent. When the agent discovers your target process or freezes a confirmation plan, it will give you a one-line `dotnetlab grant ...` command to run in your terminal (for example, `dotnetlab grant <workspace> <pid> observe 300 5`). Once you run it, tell the agent **"Granted"** and it will execute the measurement autonomously.

### 1. Profile a Live .NET Application & Find Hotspots (Zero Code Changes)

Start your .NET service or app normally, then prompt your AI:

```text
Use dotnetlab to discover my running OrderService .NET process, capture a 10-second CPU and allocation diagnostic trace, and show me the top inclusive/exclusive CPU hotspots, allocation hotspots, and whether GC pauses are overlapping request spikes. Give me the exact `dotnetlab grant` command when you're ready to capture.
```

### 2. Optimize a Hot Path with Rigorous A/B Benchmarking & Allocation Guardrails

```text
Use dotnetlab to optimize `JsonPayloadParser.ParseBatch` in this repo.
1. Set up a cooperative Tedd.DotNetLab.Harness scenario comparing the current implementation ("baseline") against candidate optimizations with a strict output-equivalence validation check.
2. Freeze a randomized block plan (at least 12 blocks) using `scenario.ns_per_operation` as the primary metric and `allocation.thread_bytes` as a non-regressing guardrail.
3. Formulate hypotheses from the optimization registry, test candidates, and only recommend a change if it achieves a statistically significant improvement AND passes fresh-process normal-build confirmation.
```

### 3. Benchmark & Inspect JIT Codegen on a Preview Runtime (e.g., .NET 11 RC2)

```text
I want to evaluate our `VectorizedHashKernel` on .NET 11 RC2 vs. .NET 10.
Use dotnetlab to launch and pin separate experiment plans using `target_runtime_version` for each installed runtime, compare throughput, allocations, and live JIT x64 assembly instructions (`lab.inspect` `native_diff`), and summarize whether .NET 11 RC2 eliminates bounds checks or improves SIMD codegen on our hot loop.
```

### 4. Investigate and Cut p99 Tail-Latency Spikes

```text
Our message handler has fast average throughput, but p99 latency spikes under burst load. Use dotnetlab to capture per-operation individual latencies and GC suspension timelines, run a cluster-bootstrapped quantile comparison (p50, p95, p99) between the current buffer strategy and a pooled `ArrayPool<byte>` candidate, and report the confidence intervals for p99.
```

### 5. Test Whether Two Optimizations Compound or Interfere ($2 \times 2$ Factorial)

```text
I have two candidate optimizations for `WildcardMatcher`:
- Factor A: SIMD `SearchValues<char>` literal prefiltering
- Factor B: stackalloc small state bitsets instead of heap arrays
Use dotnetlab to run a 2x2 factorial experiment (`00`, `10`, `01`, `11`) and use `lab.compare` with `include_factorial: true` to tell me each main effect and whether A and B have a positive synergy or redundant interaction.
```

### 6. Inspect JIT x64 Disassembly & Prototype via Native ReJIT

*(Requires `--enable-native-extension`; see [Native Qualification](docs/native-qualification.md)).*

```text
Use dotnetlab to inspect the live tiered JIT x64 assembly generated for `ChecksumKernel.Compute`. Check whether bounds checks or missed vectorization are present in Tier-1 code, prototype bounded CIL candidates if helpful, and verify that any speedup holds in a fresh normal build.
```

### 7. Scientific Divide-and-Conquer Regression Localization

```text
Throughput on the `ImportPipeline` benchmark regressed ~25% this week, and we aren't sure whether it's caused by parser allocations, lock contention, or serializer changes. Use dotnetlab to log competing hypotheses in the workspace notebook, run diagnostic cuts to falsify half the pipeline at each step, and pinpoint the exact bottleneck with retained evidence.
```

---

## How It Works at a Glance

1. **Shared Workspace Controller (`connect`):** Concurrent AI sessions connect over stdio to a single per-workspace background controller backed by a WAL-mode SQLite catalog (`catalog.sqlite`) and SHA-256 content-addressed `artifacts/` store.
2. **Isolation & Machine Exclusivity:** A machine-wide named mutex prevents overlapping measurements and trace parses across workspaces. EventPipe `.nettrace` parsing runs inside a memory-capped child `TraceWorker` (Windows Job Object / Linux cgroup v2).
3. **Preregistered Statistics:** Experiment plans freeze variant arms, block counts, warmup, random seeds, primary metrics, and guardrail policies *before* data collection—preventing cherry-picked runs or silent metric switching.
4. **Two-Stage Promotion:** Exploratory harness runs (`ListenAsync`) and native ReJIT trials (`lab.patch`) allow rapid iteration, while source-level promotion requires **fresh normal-build confirmation** (`start_confirmation`), launching clean, profiler-free processes per randomized block arm.

---

## Documentation (`docs/`)

Detailed architectural, statistical, security, and operational documentation lives in [`docs/`](docs/):

| Document | Contents |
| --- | --- |
| [**Architecture & Runtime Integration**](docs/architecture-and-runtime.md) | Shared controller topology, named pipes, machine-wide measurement mutex, `TraceWorker` Job Object / cgroup v2 containment, oversized MCP chunking, and runtime compatibility matrix. |
| [**CLI & MCP Reference**](docs/cli-and-mcp-reference.md) | Complete reference for `dotnetlab` CLI commands, human security grants, all 6+1 MCP tools (`lab.discover`, `lab.inspect`, `lab.experiment`, `lab.run`, `lab.query`, `lab.compare`, `lab.patch`), and JSON Schemas. |
| [**Harness Integration Guide**](docs/harness-integration.md) | Embedding `Tedd.DotNetLab.Harness` (`ISynchronousScenario`, `IAsynchronousScenario`, `ApplicationThreadDispatcher`, `ListenAsync`, and `RunOwnedAsync`). |
| [**Statistical Contract**](docs/statistical-contract.md) | Paired log-ratio inference, Holm-Bonferroni multiplicity, TOST equivalence, $2 \times 2$ factorial contrasts, cluster-bootstrapped quantiles, and simulation qualification. |
| [**Native Qualification & Confirmation**](docs/native-qualification.md) | CoreCLR `ICorProfilerInfo12` startup/attach scope, x64 disassembly snapshots, method probes, bounded CIL ReJIT safety/restoration, and fresh-process confirmation rules. |
| [**Evidence Management & Retention**](docs/evidence-management.md) | Privacy-filtered vs. raw bundle export, quarantined bundle import, artifact pinning, reference release, deletion grants, and SQLite backup/recovery. |
| [**Building, Packaging & Verification**](docs/building-and-verification.md) | Building managed & C++ components from source, reproducible Windows distribution packaging/verification, and running the requirement-evidence, dual-runtime, and WSL2 Linux suites. |
| [**Threat Model**](docs/threat-model.md) | Trust boundaries, capability grants, IPC peer verification, reparse-point defenses, and non-goals. |

---

## License

Licensed under the [Apache License, Version 2.0](LICENSE). See [SECURITY.md](SECURITY.md) for security boundaries and reporting guidance.
