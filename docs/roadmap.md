# Orkis Roadmap

A forward-looking catalogue of capabilities discussed and designed but **not yet
built** — intent, not a schedule. Items graduate out as they land: this file tracks only
what remains. What already exists lives in the code and the README, which is the record
of shipped work — not here.

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

- **Sandbox capability advertising** `[idea]` — a `SandboxCapabilities` surface (network
  flag, available commands, notes) shown proactively in the tool description and folded
  into error results, so the model stops thrashing through unavailable commands. Should
  also carry a guest-image version check: deployed rootfs images silently drift behind
  the repo's guest code (`init.sh`/agent), surfacing as baffling runtime failures long
  after the fact. Stamp a version into the rootfs at build time, compare at sandbox
  startup, and warn loudly on mismatch.
- **Web Push / installable PWA** `[scaffold]` — the daemon already ships the notification
  primitive (the event stream) and the web UI toasts + fires a browser Notification on
  `run_paused`. Next: Web Push via the gateway (service worker + VAPID; subscriptions in
  gateway state next to sessions; the gateway holds its own subscription to the daemon's
  stream and pushes on filtered events) — which also makes the UI an installable PWA,
  likely covering mobile (Android and iOS ≥ 16.4 support PWA push). A daemon-only
  deployment brings its own delivery; a generic webhook is a footnote for M2M.
- **Budget polish** `[scaffold]` — an optional per-turn budget cap for chats, and a
  `write_handoff` tool for scheduled runs if the current final-text-as-handoff proves
  mushy.

## Tier 2 — Medium-term (design mostly clear, larger or dependent)

- **Runtime-object audit & authz** `[scaffold]` — `mcpAllowlist` is a policy floor, not
  identity. Attaching a stdio MCP server is arbitrary command execution, so the
  privileged runtime-mutation routes want a dedicated audit trail (who/when/what, via
  `SO_PEERCRED` on the socket) and per-operation authz at the gateway, so they do not
  ride the loopback auth exemption that reads do.
- **Vector-native retrieval backends** `[idea]` — pgvector / Qdrant behind the same
  `IChunkStore`/`IRetriever` interfaces, for corpora beyond what `SqliteVectorStore`'s
  full-scan cosine handles (tens of thousands of chunks). pgvector doubles as part of
  the compose stack's Postgres multi-duty story.
- **Ambient retrieval injection** `[idea]` — inject relevant chunks per message as a
  context-policy feature, complementing the agent-directed `search_corpus` tool.
- **Execution floor** `[idea]` — a global minimum sandbox level ("everything runs in
  Firecracker"). Distinct from workspace flow policy: this governs where *code* runs, not
  where *data* goes.
- **Web UI remaining surfaces** `[scaffold]` — an audit view over supervision decisions
  (already in the event log) and prompt templates.
- **Passkey login (WebAuthn)** `[idea]` — enroll from an authenticated session, log in
  with a touch thereafter (`Fido2NetLib`), the bearer token demoted to break-glass
  recovery. The token→cookie session it builds on is done; passkeys need TLS for remote
  origins.
- **Editor integrations** `[idea]` — surface runs, approvals, and chat from an editor
  over the same public JSON+SSE protocol.
- **Multi-daemon data-root safety** `[idea]` — two daemons on one *data root* could both
  resume the same run and double-execute its tool calls (two on one socket is already
  refused at startup). Fix when it matters: an exclusive lock on the data root, or
  run-level leases if shared-store multi-daemon (compose stack over Postgres) ever
  becomes a goal.

## Tier 3 — Exploratory (needs more design; security- or complexity-heavy)

- **Artifact staging confidentiality gate** `[idea]` — sandbox-local storage resolved the
  integrity half by construction (untrusted output rises only via supervised promotion).
  The residual: once artifacts can contain secrets, *staging* one into a low-trust
  sandbox needs its own supervision facet. The orchestrator-mediated staging design
  already gives it a choke point.
- **Long-running sessions (dev servers, REPLs)** `[idea]` — beyond the warm-VM guest
  agent (built): exposing session lifecycle to the *model* (background processes, port
  forwarding, attach/detach). Softened now that disk state survives VM death — the
  disappearance problem is memory-only.
- **Firecracker networking — Phase 2: domain allowlist** `[scaffold]` — an SNI-filtering
  egress proxy (no TLS interception) plus DNS control, for per-run domain scoping. The
  DNS half's first step is built (a dnsmasq forwarder on the gateway with a targeted nft
  exception); dnsmasq is the natural hook for the per-run allowlist.
- **Orkis as an MCP server** `[idea]` — expose Orkis's own capabilities to other agents
  over MCP (the Streamable HTTP transport can co-host on the daemon's endpoint).
- **Evals** `[idea]` — recorded/replayable model interactions for deterministic tests,
  behavioural regression suites, and LLM-as-judge scoring.
- **Firecracker jailer + teardown hardening** `[idea]` — run Firecracker under its jailer
  and harden per-VM teardown for production use.
- **Batteries-included distribution (compose stack)** `[scaffold]` — the packaging skeleton
  is built (`docker/`): self-contained daemon + gateway images published to GHCR on a `v*`
  tag, a dev (`build:`) and a deploy (`image:`) compose file sharing one bind-mounted config
  and the daemon's Unix socket, and Firecracker-in-container behind a `/dev/kvm` opt-in (the
  daemon auto-falls-back otherwise). What remains is the "batteries" — the out-of-process,
  shared-infrastructure backends the stack exists to host: Postgres multi-duty behind several
  abstractions (checkpoint store, pgvector retrieval, memory, supervision queue) and an
  S3-compatible object store (e.g. MinIO) for the artifact store. Its lasting value is design
  pressure: every abstraction must eventually have a shared-infrastructure backend, so no
  interface may assume single-process state.
