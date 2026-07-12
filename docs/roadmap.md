# Orkis Roadmap

Capabilities that have been discussed and designed but are **not yet built**. This
is a living catalogue of intent, not a commitment or a schedule. Items are grouped by
readiness so it is clear which are ready to implement versus which still need design.

**Status tags**
- `[scaffold]` — a seam or partial mechanism is already in the codebase.
- `[abstraction]` — an interface exists, but no implementation.
- `[idea]` — discussed only; no code yet.

## Guiding principles

The through-lines these ideas are meant to respect:

1. **Abstractions with swappable implementations, DI-first.** Every capability is an
   interface in the abstractions package with concrete backends chosen at the
   composition root. New backends never depend on each other.
2. **Supervision is the single choke point for capability grants.** Sandbox isolation
   level, network reach, and artifact promotion/staging are three facets of
   one trust lattice. Each is granted per run, and every grant is auditable in the
   trace. Adding a capability means adding a facet the supervisor can grant — never an
   ambient default.
3. **Separate state from compute; storage is sandbox-local.** Every sandbox type keeps
   persistent per-workload storage in its own native representation (host directory,
   ext4 image) — built as `SandboxExecutionRequest.WorkspaceKey`. Storage never crosses
   isolation levels; files move between levels only through explicit, supervisable
   artifact promotion. Compute stays disposable: a dead VM loses only memory state,
   because the disk survives it. This deleted the portable-workspace design's hard
   problems (representation conversion, sync semantics, distributed locking) — the one
   residual is that a rw image must not be attached to two VMs at once, handled by
   serializing executions per workspace image.
4. **Eviction is a first-class, typed outcome, not a crash.** Retention is a host
   policy. The model references things (workspaces, sessions, checkpoints) by id and
   never owns their lifecycle; operating on a gone one returns "no longer exists" so the
   orchestration can recreate it.
5. **Verify against reality.** Sandbox and model behaviours are confirmed by running
   real VMs and real models, not only unit tests. This has repeatedly caught bugs that
   green tests missed.

## Tier 1 — Near-term (well-specified, clear next steps)

*(Empty — built items graduate out, and the next priorities get promoted here from
Tier 2. NuGet lock files landed once SDK 10.0.3xx fixed lock-file generation for
`.slnx` restore; the graph is pinned and CI restores with `--locked-mode`.)*

## Tier 2 — Medium-term (design mostly clear, larger or dependent)

- **Demo embedding provider** `[idea]` — the demo host wires no
  `IEmbeddingGenerator`, so the built memory and retrieval capabilities are
  library-only there. Wire one for live OpenAI runs (Anthropic exposes no embeddings
  endpoint) or a small local model, so `save_memory`/`search_memories`/`search_corpus`
  become demoable.
- **Vector-native retrieval backends** `[idea]` — pgvector / Qdrant behind the same
  `IChunkStore`/`IRetriever` interfaces, for corpora beyond what `SqliteVectorStore`'s
  full-scan cosine handles (tens of thousands of chunks). pgvector doubles as part of
  the compose stack's Postgres multi-duty story.
- **Ambient retrieval injection** `[idea]` — what remains of "retrieval wired into
  the agent loop" now the `search_corpus` tool is built (agent-directed lookup over
  `IRetriever`, reranking top-N to top-K when an `IReranker` is registered, results
  rendered with chunk ids and source metadata for citation): injecting relevant
  chunks per message as a context-policy feature, instead of on the agent's
  initiative.
- **Execution floor** `[idea]` — a global minimum sandbox level ("everything runs in
  Firecracker"). Distinct from workspace flow policy: this governs where *code* runs, not
  where *data* goes.
- **Daemon clients + protocol growth** `[scaffold]` — the daemon itself is built
  (`Orkis.Daemon`, July 2026): a long-lived composition root owning the stateful side —
  run registry with checkpoint adoption on restart (`RunRegistry` over
  `ICheckpointStore.ListLatestAsync`), background execution, the durable approval inbox,
  and sandboxes — exposed as HTTP/JSON over an owner-only Unix domain socket, with the
  typed run-event stream served as SSE (live fan-out via `RunEventBroker` teeing the
  durable log). The daemon is one composition root among several, never required: the
  libraries stay embeddable in-process. Built since: the `Orkis.Client` typed client
  (wire records, SSE reader with an `UnknownRunEvent` fallback for forward
  compatibility) and the `orkis` CLI (attached runs with inline approval prompts,
  `ps`/`logs -f`/`approvals`/`approve`/`deny`/`resume`/`artifacts`). What remains:
  the TUI (below), the compose-stack web UI, editor integrations, MCP servers in
  the daemon composition, and bearer-token auth over TCP for remote clients.

  **TUI decision (July 2026):** same binary as the CLI — an `orkis dash` verb —
  not a separate tool; both share `Orkis.Client`. The first cut is built:
  Spectre.Console live layout (runs + approvals + rolling event feed) over the
  daemon-wide `GET /v1/events` stream (all runs multiplexed via the broker's
  wildcard subscription; live-only — clients bootstrap from the runs/approvals
  snapshots, so no global resume semantics to maintain), with inline approval
  decisions. Graduate to Terminal.Gui only if interaction depth demands it (pane
  focus, scrollback, per-run drill-down) — that would also be the moment to split
  binaries.

  Instance identity is the endpoint, dockerd-style: clients select a daemon with an
  endpoint flag/env (`--socket`/`ORKIS_SOCKET`, defaulting to the well-known path, and
  growing to accept URLs when TCP auth lands); multiple daemons are just different
  sockets with different data directories, and docker-context-style named endpoints
  can come later as a pure client convenience. The hazard is not two daemons on one
  socket (refused at startup) but two daemons on one *data root* — both could resume
  the same run and double-execute its tool calls. Fix when it matters: an exclusive
  lock on the data root, or run-level leases if shared-store multi-daemon (compose
  stack over Postgres) ever becomes a goal.

  **Protocol decision (July 2026): HTTP/JSON over a Unix domain socket, SSE for the
  event stream** — Kestrel + minimal APIs, not gRPC or SignalR. Rationale: `RunEvent`'s
  JSON-polymorphic form *is* the wire format (no parallel protobuf schema to maintain);
  SSE `Last-Event-ID` maps directly onto `RunEvent.Sequence` +
  `IRunEventLog.ReadAsync(afterSequence)` for gapless reconnect (replay history, then
  live tail); the command surface is plain request/response; browsers consume SSE
  natively (compose-stack web UI) and the future MCP server's Streamable HTTP transport
  can co-host on the same endpoint. .NET clients get typed contracts by sharing the
  abstractions records through a thin client package, not codegen. Auth is staged:
  Unix-socket file permissions now, bearer token over TCP when the compose stack needs
  remote clients. Forward-compat note: `System.Text.Json` polymorphic deserialization
  throws on unknown `$type` discriminators, so clients must skip-and-continue on
  unknown event types from day one.

## Tier 3 — Exploratory (needs more design; security- or complexity-heavy)

- **Artifact staging confidentiality gate** `[idea]` — what remains of the old
  trust-lattice question after sandbox-local storage resolved the integrity half by
  construction (untrusted output rises only via supervised promotion). The residual:
  once artifacts can contain secrets, *staging* one into a low-trust sandbox needs its
  own supervision facet. The orchestrator-mediated staging design already gives it a
  choke point.
- **Long-running sessions (dev servers, REPLs)** `[idea]` — what remains beyond the
  warm-VM guest agent (built): exposing session lifecycle to the *model* (background
  processes, port forwarding, attach/detach). Much softened now that disk state
  survives VM death — the disappearance problem is memory-only.
- **Firecracker networking — Phase 2: domain allowlist** `[idea]` — an SNI-filtering egress
  proxy (no TLS interception) plus DNS control, for per-run domain scoping. The DNS half
  has a concrete first step: guests currently hardcode public resolvers, which fails on
  hosts that restrict outbound DNS. A forwarder bound to the bridge IP
  (`172.30.0.1:53`, provisioned by the network setup script), guest `resolv.conf`
  pointing at it, and one targeted nft exception (guest → host UDP 53 only) ahead of
  the host-block rule lets guest DNS ride the host's own resolution — and the
  forwarder is the natural hook for the eventual allowlist.
- **Sandbox capability advertising** `[idea]` — a `SandboxCapabilities` surface (network
  flag, available commands, notes) shown proactively in the tool description and folded
  into error results, so the model stops thrashing through unavailable commands. Should
  also carry a guest-image version check: deployed rootfs images silently drift behind
  the repo's guest code (`init.sh`/agent), which surfaces as baffling runtime failures —
  missing network config, dirty unmounts — long after the fact. Stamp a version into the
  rootfs at build time, compare at sandbox startup, and warn loudly on mismatch.
- **MCP server** `[idea]` — expose Orkis capabilities to other agents over MCP.
- **Evals** `[idea]` — recorded/replayable model interactions for deterministic tests,
  behavioural regression suites, and LLM-as-judge scoring.
- **Firecracker jailer + teardown hardening** `[idea]` — run Firecracker under its jailer
  and harden per-VM teardown for production use.
- **Batteries-included distribution (compose stack)** `[idea]` — the imagined end state:
  a preconfigured docker-compose-style stack — the daemon, a web UI, Postgres, and an
  S3-compatible object store (e.g. MinIO) — plus a TUI client that connects from outside.
  Postgres pulls multi-duty behind several abstractions (checkpoint store, pgvector
  retrieval, memory, supervision queue); the object store backs the artifact store.
  Depends on the daemon + client protocol. Its real value today is as design pressure:
  every abstraction must eventually have an out-of-process, shared-infrastructure
  backend, so no interface may assume in-memory or single-process state. Note: compose
  here is packaging/deployment only — sandboxing stays bubblewrap/Firecracker, and a
  containerised daemon needs `/dev/kvm` passthrough for the Firecracker path.
