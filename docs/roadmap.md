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

- **Firecracker guest agent + VM reuse** `[idea]` — a small agent inside the guest
  (vsock exec protocol: run command, stream stdout/stderr/exit) so one VM per
  (workspace, sandbox type) serves many commands: boot latency amortised, in-memory
  state (installed packages, warm caches) persists between commands, and concurrent
  commands become ordinary in-OS concurrency with error messages models already
  understand. Idle timeout destroys the VM; the workspace image survives, so loss is
  memory-only. Replaces today's boot-per-command model when present.
- **Firecracker networking — Phase 1: restricted egress** `[scaffold]` — TAP + NAT with
  a hardened nftables ruleset that blocks the host, RFC1918 ranges, link-local, and the
  cloud metadata address, allowing only public egress. Unlocks `curl`/`pip`. `[scaffold]`
  because `NetworkPolicy` exists (None-only). Note: needs privileged host setup
  (`CAP_NET_ADMIN`) and pairs with jailer hardening.
- **Additional document parsers (PDF, HTML)** `[idea]` — only plain text/Markdown is
  parsed today; incremental ingestion coverage.

## Tier 2 — Medium-term (design mostly clear, larger or dependent)

- **Tool catalogue / tool search (progressive disclosure)** `[idea]` — `IToolCatalog`
  (search + resolve), a `search_tools` meta-tool, a stub tier, and active tool ids in run
  state. Scales tool sets past what fits in context without churning the prompt prefix.
- **Per-run tool scoping / dynamic tool sets** `[idea]` — the tool set is currently fixed
  at runner construction. Per-run scoping is a prerequisite for both the catalogue and MCP.
- **AI supervisor** `[idea]` — an LLM-based approval policy behind `ISupervisor`,
  composable with escalation (e.g. AI approves low-risk, escalates the rest).
- **Agent-written memory: implementation + loop wiring** `[abstraction]` — `IMemoryStore`
  exists; needs a backend and a design for how the loop reads/writes memory. Overlaps
  context management.
- **Context management** `[idea]` — token budgeting, compaction, and summarisation as the
  transcript grows; deciding what retrieved material enters the window.
- **Persistent vector stores** `[idea]` — SQLite / pgvector / Qdrant backends for
  retrieval; in-memory is the only one today. Incremental, one backend at a time.
- **Retrieval wired into the agent loop** `[idea]` — expose retrieval as a tool or a
  context source; depends on the context-management design.
- **MCP client** `[idea]` — consume Model Context Protocol servers as Orkis tools;
  couples with per-run tool scoping.
- **Resilience** `[idea]` — retry/backoff/rate-limit handling for model and tool calls.
- **Execution floor** `[idea]` — a global minimum sandbox level ("everything runs in
  Firecracker"). Distinct from workspace flow policy: this governs where *code* runs, not
  where *data* goes.
- **Daemon host + client protocol** `[idea]` — a long-lived host process that owns the
  stateful side (run registry, checkpoint adoption on restart, supervision inbox,
  workspace/sandbox lifetimes, privileged network setup), with thin UIs — CLI, web,
  editor — talking to it over a wire protocol (gRPC over a Unix socket or SSE/SignalR).
  The dockerd/containerd shape. The daemon is one composition root among several, never
  required: the libraries stay embeddable in-process. The key design surface is the
  typed run-event stream (turn progress, tool calls, cost, pending approvals), which
  doubles as the observability and eval-recording surface. Its prerequisites are now
  built: the durable checkpoint store (`FileCheckpointStore`) and the queue-based
  supervisor (`QueueSupervisor` over `IApprovalInbox`) make run state detachable.

## Tier 3 — Exploratory (needs more design; security- or complexity-heavy)

- **Artifact staging confidentiality gate** `[idea]` — what remains of the old
  trust-lattice question after sandbox-local storage resolved the integrity half by
  construction (untrusted output rises only via supervised promotion). The residual:
  once artifacts can contain secrets, *staging* one into a low-trust sandbox needs its
  own supervision facet. The orchestrator-mediated staging design already gives it a
  choke point.
- **Long-running sessions (dev servers, REPLs)** `[idea]` — what remains beyond the
  Tier 1 guest agent: exposing session lifecycle to the *model* (background processes,
  port forwarding, attach/detach). Much softened now that disk state survives VM death —
  the disappearance problem is memory-only.
- **Firecracker networking — Phase 2: domain allowlist** `[idea]` — an SNI-filtering egress
  proxy (no TLS interception) plus DNS control, for per-run domain scoping.
- **Sandbox capability advertising** `[idea]` — a `SandboxCapabilities` surface (network
  flag, available commands, notes) shown proactively in the tool description and folded
  into error results, so the model stops thrashing through unavailable commands.
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

## Polish

- **`MaxCost` with no pricing** `[idea]` — warn or fail fast at startup when a run sets
  `MaxCost` while the null cost calculator is active, since the budget can never trigger.
