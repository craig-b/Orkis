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
  Orkis.Host              Composition root and demo entry point (CLI, in-process)
  Orkis.Daemon            Composition root as a long-lived service (HTTP over a Unix socket)
  Orkis.Client            Typed client for the daemon protocol (commands + event stream)
  Orkis.Cli               `orkis` — thin command-line client over Orkis.Client
  Orkis.Web               Network gateway: web UI assets, auth, /v1 proxy to the socket
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
reboot). With dnsmasq installed, the setup also runs a DNS forwarder on the
bridge gateway, so guests resolve through the host's own resolution instead of
depending on public resolvers (which some hosts block):

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

MCP servers plug in — local over stdio, or remote over Streamable HTTP — and
contribute their tools through the searchable catalogue, so large servers cost no
context until the model actually needs them. Tool annotations are not trusted by
default, so MCP tools pass supervision. The daemon reads the same variable:

```sh
ORKIS_MCP_SERVER="npx -y @modelcontextprotocol/server-everything" dotnet run --project src/Orkis.Host -- ...
ORKIS_MCP_SERVER="https://example.com/mcp" dotnet run --project src/Orkis.Host -- ...
```

With `--ai` (live runs only), the model itself is the first-line reviewer: it
approves routine actions — optionally requiring a sandbox — denies clear policy
violations with a reason the agent sees, and escalates anything it is unsure of
into the same approval inbox for a human.

On live OpenAI runs — the wired provider with an embeddings endpoint — agent
memory switches on automatically, in the CLI host and the daemon alike:
`save_memory` and `search_memories` persist to a SQLite store (`ORKIS_MEMORY_DB`
overrides the path), and relevant memories are recalled into each new run.
Pointing `ORKIS_CORPUS_DIR` at a directory of documents (.txt/.md/.html/.pdf)
indexes it at startup into a persistent vector store and adds a `search_corpus`
tool, its results reranked by the chat model. `ORKIS_EMBEDDING_MODEL` overrides
the default embedding model (text-embedding-3-small).

## The daemon

`Orkis.Daemon` hosts the same stack as a long-lived process — the dockerd shape.
The daemon owns the stateful side (runs, checkpoints, the approval inbox,
sandboxes and workspaces); thin clients speak HTTP/JSON over a Unix domain
socket. It is configured by the same environment variables as the CLI host, so
the two composition roots are interchangeable over shared state:

```sh
dotnet run --project src/Orkis.Daemon -- --offline     # or live, with an API key
```

**Providers and models** are declared in a JSONC config file — `$ORKIS_CONFIG`,
else `$XDG_CONFIG_HOME/orkis/config.json`, else `~/.config/orkis/config.json` (see
[docs/config.example.json](docs/config.example.json)). A *provider* is an endpoint
+ credentials + kind; a *model* is a per-run key pointing at a provider and its
model id. `openai` kind with a `baseUrl` covers any OpenAI-compatible endpoint —
OpenRouter, Together, Groq, a local Ollama/vLLM server, Azure. Secrets are inline
(`apiKey`) or an env-var reference (`apiKeyEnv`). Without a config file, the daemon
falls back to the legacy `ANTHROPIC_API_KEY`/`OPENAI_API_KEY`/`ORKIS_MODEL`
environment variables. `orkis run --model <key>` selects one; the key is
checkpointed, so a resumed run reconnects to the same model.

The same file carries the daemon's other boot settings — `dataDir` (state root),
`socket`, `sandbox`, `workspace`, `mcpServer`, and `corpus` (a directory indexed for
`search_corpus`). Each is optional with a sensible default, and its `ORKIS_*` env var
still overrides the file (env → file → default), so a deployment is one file instead
of a systemd unit full of variables.

The `orkis` CLI is the thin client (`--socket` or `ORKIS_SOCKET` selects a daemon;
the well-known path is the default). `orkis run` attaches to the run's event
stream and, when the run pauses for supervision, prompts for the decision right
in the terminal — approve on the host, approve sandboxed, or deny — then resumes
and keeps streaming:

```sh
alias orkis="dotnet run --project src/Orkis.Cli --"
orkis run "Roll 3 dice."                 # attach; prompt inline on approvals
orkis run "..." --supervisor yolo        # or: --detach to just print the run id
orkis ps                                 # registry (adopts checkpoints on restart)
orkis logs <run-id> -f                   # replay history, then tail live
orkis approvals                          # pending decisions
orkis approve <run> <call> --sandbox standard --resume
orkis deny <run> <call> --reason "not like this" --resume
orkis artifacts
orkis info                               # capabilities: supervisors, models, tools
orkis dash                               # live TUI: runs, approvals, event feed
orkis chat "hello"                       # interactive multi-turn chat
orkis schedules                          # list scheduled runs
orkis schedules add "0 7 * * *" "Summarize overnight CI failures." \
  --supervisor yolo --continuity sharedStorageWithHandoff
```

Schedules are cron-triggered run templates (Cronos syntax; a sixth field adds
seconds), fired by the daemon and persisted across restarts. Each firing is a
fresh run — missed firings while the daemon was down are skipped, not caught up,
and an overlapping firing is skipped. Continuity across firings is chosen per
schedule: `fresh`, `sharedStorage` (a persistent workspace and memory scope), or
`sharedStorageWithHandoff` (the previous firing's closing note seeds the next).
Bound a schedule's autonomy by capability — `--supervisor yolo` with a tool
restriction — rather than parking every action for approval.

A chat is a run whose turns end awaiting your next message instead of
terminating: one growing transcript, one budget, one working context — each chat
gets its own persistent workspace and memory scope (`chat-<run-id>`), so files
and memories accumulate per conversation — with supervision applying to every
tool call along the way. Leave with an empty message and rejoin later —
even after a daemon restart — with `orkis chat --run <run-id>`; over the wire
it is `POST /v1/runs/{id}/messages` plus `GET /v1/runs/{id}/transcript`.

`orkis dash` follows the daemon-wide event stream (`GET /v1/events`) — every run's
events multiplexed live over one connection — on top of runs/approvals snapshots.
Press `a` to decide pending approvals without leaving the dashboard, `q` to quit.

Supervision is queue-based by default (`--supervisor` selects `yolo` or, on live
runs, `ai`), so every risky action is a pending approval on the wire. Deciding an
approval is the whole interaction — the daemon continues the run itself, so an
unattended or scheduled run resumes the moment you approve from any client (no
separate resume step), reconstructing the run's own workspace and memory scope.

The protocol is plain HTTP/JSON over the socket — everything the CLI does works
from `curl` too. A run's history streams as Server-Sent Events whose payloads are
the run's typed events — the same self-describing JSON the durable log stores,
with the SSE id carrying the event sequence, so `Last-Event-ID` reconnection
replays exactly the missed events:

```sh
SOCK=${XDG_RUNTIME_DIR:-~/.local/share/orkis}/orkis/orkis.sock
curl --unix-socket "$SOCK" http://d/v1/runs -X POST \
  -H 'content-type: application/json' -d '{"prompt":"Roll 3 dice."}'
curl -N --unix-socket "$SOCK" "http://d/v1/runs/<run>/events?follow=true"
```

Access control is the socket itself (owner-only permissions); the daemon refuses
to start when another instance already listens on its socket. The daemon has no
network listener by design — network exposure is a capability owned by a separate
gateway:

```sh
dotnet run --project src/Orkis.Web       # http://127.0.0.1:7420
```

The web UI (TypeScript + Lit, deliberately three npm dependencies) builds once
with Node and is then plain static files:

```sh
cd ui && npm ci && npm run build     # → ui/dist
ORKIS_WEB_ASSETS=ui/dist dotnet run --project src/Orkis.Web
```

It gives you runs with a live event feed, the approval inbox (same grant
buttons as the CLI prompt), multi-turn chat, and the capabilities page — all
over the public JSON+SSE protocol, so the UI stays disposable.

`Orkis.Web` serves the web UI assets and reverse-proxies `/v1/*` over the
daemon's socket (SSE streams through unbuffered). It owns auth: loopback is
exempt (trust is local, like the socket), while remote requests need the bearer
token — from `ORKIS_TOKEN`, or generated once and persisted owner-only in the
data root — either as a header or exchanged at `/auth/session` for a cookie
session (which is how the browser logs in). Clients pass the gateway URL instead
of a socket path (`--socket http://host:7420 --token …`, or
`ORKIS_HOST`/`ORKIS_TOKEN`). `ORKIS_WEB_LISTEN` binds beyond loopback; TLS
and HTTP/2 belong to a reverse proxy in front. For SSE (the live event stream) to
stream through nginx, disable proxy buffering and use HTTP/1.1 upstream:

```nginx
location /v1/events { proxy_pass http://gateway; proxy_buffering off; proxy_http_version 1.1; }
```

## Development

Formatting is enforced in CI with [CSharpier](https://csharpier.com). Enable the
pre-commit hook once per clone so commits format themselves:

```sh
git config core.hooksPath .githooks
```

## License

[Apache-2.0](LICENSE)
