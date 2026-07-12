# Orkis

An agentic AI system for .NET.

Orkis provides the building blocks for LLM-driven agents — retrieval-augmented generation, reranking, tool calling, and sandboxed execution — as composable libraries with first-class dependency injection support.

> **Status:** early development. APIs are unstable and everything is subject to change.

## Features

This is the feature set Orkis aims for — a statement of intent, not a promise of
current or complete capabilities. What is actually built today versus planned is
tracked in [docs/roadmap.md](docs/roadmap.md), so this list can stay stable as a
description of direction rather than a status checklist.

- **Agentic tool calling** — an orchestration loop that lets models plan, call tools, and act on results
- **RAG** — retrieval-augmented generation over your own data, with pluggable vector store backends and a first-class ingestion pipeline (parsing, chunking, embedding)
- **Reranking** — second-stage relevance scoring to sharpen retrieval results before they reach the model
- **Sandboxed execution** — run model-generated code and untrusted tool operations in isolation, with graduated sandbox levels
- **Workspaces** — persistent per-sandbox working storage scoped to a workload: files survive across commands, runs, and restarts, and cross isolation boundaries only through explicit artifact promotion
- **Supervision** — pluggable approval policies for agent actions: human-in-the-loop, AI supervisor, rules-based, or fully autonomous ("yolo") — with the required sandbox level driven by the supervision decision
- **Durable execution** — agent runs checkpoint after every step and can be resumed after a crash, restart, or long pause (including pauses awaiting supervision decisions)
- **Memory** — long-term, agent-written memory as a first-class concept, distinct from corpus retrieval
- **Context management** — token budgeting, compaction, and control over what enters the model's window
- **Budgets and policies** — per-run limits on tokens, cost, wall-clock time, and tool calls
- **Observability** — OpenTelemetry tracing of the agent loop with token and cost accounting, following the GenAI semantic conventions
- **Evals** — recorded/replayable model interactions for deterministic tests and behavioural regression suites
- **MCP interop** — consume Model Context Protocol servers as tools, and expose Orkis capabilities as an MCP server
- **Tool search** — large tool catalogs scale via progressive disclosure: a small always-on core plus a searchable catalog the model queries on demand, keeping context lean and prompt caches stable
- **Source-generated tools** — attribute a C# method and get its schema, argument binding, and validation generated at compile time; reflection-free and AOT-friendly

## Design principles

- **Idiomatic .NET** — modern C# on .NET 10, following BCL conventions and patterns
- **DI-first** — every component registers through `IServiceCollection` extensions and resolves through standard constructor injection; configuration uses the options pattern with startup validation
- **Abstractions with swappable implementations** — RAG, reranking, and sandboxing are defined as interfaces in a core abstractions package; concrete backends ship as independent packages, and the composition root decides which to use
- **No hidden coupling** — implementation packages depend only on the abstractions, never on each other

## Solution layout

```
src/
  Orkis.Abstractions      Interfaces and shared domain types (minimal dependencies)
  Orkis.Core              Agent loop, orchestration, tool dispatch
  Orkis.Tools.Generator   Source generator for [OrkisTool] methods
  Orkis.Rag.*             Ingestion, vector store, and retrieval implementations
  Orkis.Runs.*            Run-state persistence (checkpoint store, approval inbox)
  Orkis.Sandbox.*         Sandbox execution implementations (process, bubblewrap, Firecracker)
  Orkis.Host              Composition root and demo entry point
tests/
```

Package families written with `.*` (e.g. `Orkis.Rag.*`) hold zero or more concrete
packages today; see the roadmap for which backends exist versus are planned.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Getting started

`Orkis.Host` is a working demo of the full stack: an agent with generated tools, a
sandboxed shell tool, human-in-the-loop supervision at the console, budgets, and
cost tracking.

```sh
# Scripted model, no API key needed — proves the whole pipeline locally:
dotnet run --project src/Orkis.Host -- --offline

# Live against a model provider — set a key and Orkis picks the provider:
export ANTHROPIC_API_KEY=sk-ant-...     # or OPENAI_API_KEY=sk-...
dotnet run --project src/Orkis.Host -- "Roll 3 dice and tell me the total."

# ORKIS_PROVIDER=anthropic|openai and ORKIS_MODEL=<id> override the defaults.

# Unsupervised ("yolo") mode:
dotnet run --project src/Orkis.Host -- --yolo --offline
```

Read-only tools run without ceremony; anything riskier stops at the console for
approval, where the operator can also require sandboxed execution.

The demo auto-selects the strongest available sandbox — Firecracker micro-VMs
(requires KVM; run `scripts/setup-firecracker.sh` once to provision the guest
kernel and rootfs), then bubblewrap, then plain process isolation — overridable
with `ORKIS_SANDBOX=firecracker|bubblewrap|process`.

Sandboxed code has no network by default. Firecracker VMs can be granted
public-internet-only egress — the host, LAN/private ranges, and the cloud
metadata address stay unreachable — after a one-time, auditable host setup
(bridge, a user-owned TAP pool, and nftables rules; idempotent, re-run after
reboot):

```sh
sudo scripts/setup-firecracker-network.sh        # provision (--remove undoes it)
ORKIS_NETWORK=egress dotnet run --project src/Orkis.Host -- "fetch https://example.com and summarize it"
```

Shell commands run in a persistent workspace scoped per sandbox type
(`ORKIS_WORKSPACE` names it; `default` otherwise), so files written by one command
— or one run — are still there for the next. On Firecracker, a rootfs carrying the
Orkis guest agent gets a warm micro-VM per workspace: successive commands reuse one
VM over vsock instead of paying a boot each, until an idle timeout reclaims it
(disk state survives; only memory is lost). Re-run `scripts/setup-firecracker.sh`
once to add the agent to an existing rootfs; without it, execution transparently
falls back to boot-per-command.

Files leave a workspace only through the artifact store: the agent's
`promote_artifact` tool lifts a file out (a supervised trust decision), and
`stage_artifact` copies an artifact back into workspaces. List what has been
promoted with:

```sh
dotnet run --project src/Orkis.Host -- --artifacts
```

Runs checkpoint to disk after every step (under the local application data
directory; override with `ORKIS_CHECKPOINT_DIR`), so an interrupted run — crash,
Ctrl-C, or a kill while a tool call sits awaiting approval — can be picked up
where it left off using the run id printed at the start:

```sh
dotnet run --project src/Orkis.Host -- --resume <run-id>
```

With `--queue`, supervision detaches entirely: instead of prompting inline, risky
tool calls park in a durable approval inbox and the run pauses. The decision can
then come from a different terminal — or a different day — and the run picks up
from its checkpoint:

```sh
dotnet run --project src/Orkis.Host -- --queue        # pauses awaiting approval
dotnet run --project src/Orkis.Host -- --approvals    # inspect the inbox
dotnet run --project src/Orkis.Host -- --approve <call-id> h   # or s; or --deny <call-id> [reason]
dotnet run --project src/Orkis.Host -- --resume <run-id>
```

With `--ai` (live runs only), the model itself is the first-line reviewer: it
approves routine actions — optionally requiring a sandbox — denies clear policy
violations with a reason the agent sees, and escalates anything it is unsure of
into the same approval inbox for a human.

## Development

Formatting is enforced in CI with [CSharpier](https://csharpier.com). Enable the
pre-commit hook once per clone so commits format themselves:

```sh
git config core.hooksPath .githooks
```

## License

[Apache-2.0](LICENSE)
