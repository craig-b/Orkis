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
   level, network reach, and (future) cross-level workspace mounts are three facets of
   one trust lattice. Each is granted per run, and every grant is auditable in the
   trace. Adding a capability means adding a facet the supervisor can grant — never an
   ambient default.
3. **Separate state from compute.** Durable, mountable workspaces keep compute
   disposable (the security win of per-execution sandboxes) while state lives somewhere
   any sandbox can mount. Build storage before long-running sessions; durable-backed
   state largely dissolves the "my environment disappeared" problem.
4. **Eviction is a first-class, typed outcome, not a crash.** Retention is a host
   policy. The model references things (workspaces, sessions, checkpoints) by id and
   never owns their lifecycle; operating on a gone one returns "no longer exists" so the
   orchestration can recreate it.
5. **Verify against reality.** Sandbox and model behaviours are confirmed by running
   real VMs and real models, not only unit tests. This has repeatedly caught bugs that
   green tests missed.

## Tier 1 — Near-term (well-specified, clear next steps)

- **Persistent/durable checkpoint store** `[scaffold]` — durable execution currently
  only survives in-process (`InMemoryCheckpointStore`). A restart-surviving store makes
  pause/resume genuinely durable. Small, clear.
- **Queue-based asynchronous supervisor** `[scaffold]` — the `Pending` verdict and
  checkpoint-on-pause already exist, but `ConsoleSupervisor` blocks inline (so a human's
  thinking time counts against `MaxDuration`). A supervisor that returns `Pending` and
  resumes on an out-of-band decision is the shape a real approval UI needs.
- **Workspaces — Layer 1: durable, mountable working directory** `[idea]` — an
  `IWorkspace` that outlives a single execution; the sandbox mounts it instead of a
  throwaway scratch. Backends: host directory (bind-mount), block image (Firecracker
  `/dev/vdb`), object-storage-backed. The "build first" item; the largest in this tier.
  Open sub-decisions: virtio-fs vs image-sync for cross-sandbox portability; the
  concurrency/locking model (one rw image can't be co-mounted).
- **Firecracker networking — Phase 1: restricted egress** `[scaffold]` — TAP + NAT with
  a hardened nftables ruleset that blocks the host, RFC1918 ranges, link-local, and the
  cloud metadata address, allowing only public egress. Unlocks `curl`/`pip`. `[scaffold]`
  because `NetworkPolicy` exists (None-only). Note: needs privileged host setup
  (`CAP_NET_ADMIN`) and pairs with jailer hardening.
- **Reranker implementation** `[abstraction]` — `IReranker` exists with no backend;
  completes the two-stage retrieval story (cross-encoder or API-based).
- **Additional document parsers (PDF, HTML)** `[idea]` — only plain text/Markdown is
  parsed today; incremental ingestion coverage.
- **Confabulation guardrail** `[idea]` — a system-prompt/policy discipline against the
  model presenting remembered content as tool output. Trivial to add, high value.

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

## Tier 3 — Exploratory (needs more design; security- or complexity-heavy)

- **Workspace information-flow / trust-lattice policy** `[idea]` — workspaces carry a
  trust label and a flow policy (isolated / may-flow-down / …). Integrity (don't let
  untrusted output rise to trusted contexts) and confidentiality (don't let secrets sink
  into untrusted code) pull in opposite directions; default to compartmentalisation with
  explicit, supervision-granted cross-level mounts.
- **Long-running sessions (warm compute)** `[idea]` — create/run/destroy sandbox tools for
  state that lives in memory rather than on disk (dev servers, REPLs). Hardest lifecycle
  problem; the disappearance problem bites most here.
- **Artifacts** `[idea]` — curated outputs promoted out of a workspace, with a "publish"
  step. Distinct from the workspace scratch.
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

## Polish

- **`MaxCost` with no pricing** `[idea]` — warn or fail fast at startup when a run sets
  `MaxCost` while the null cost calculator is active, since the budget can never trigger.
