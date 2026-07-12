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

- **Chats: multi-turn runs** `[scaffold]` — built (July 2026) as designed: a chat is a
  long-lived run (`AgentRunRequest.Conversational`), whose turns end in
  `RunStatus.AwaitingUser` instead of terminating; `AgentRunner.ContinueAsync` appends
  the next user message and runs another segment. Supervision, budgets (chat-level:
  the cap gates the next turn, never claws back the last reply), events
  (`run_continued`/`turn_completed`), model keys, and context compaction all apply
  unchanged; `POST /v1/runs/{id}/messages` + `GET /v1/runs/{id}/transcript` on the
  wire, `orkis chat` interactively (one SSE stream spans turns and daemon restarts
  adopt chats like any run). Chats are durable working contexts: the daemon builds a
  per-run `AgentRunner` (`RunnerFactory` — the composition root's prerogative, no tool
  ABI change), so a chat's shell/artifact/memory tools are scoped to
  `chat-<runId>` workspace and memory scope, deterministically reconstructed on
  resume, while one-shot runs share the default workspace and global memory.
  Remaining: an optional per-turn budget cap.
- **Scheduled runs** `[scaffold]` — built (July 2026): cron (Cronos; a sixth field
  adds seconds) + a run template (prompt, model key, supervisor key, budget, tool
  restriction via `AgentRunRequest.ToolNames` — autonomy bounded by *capability*, e.g.
  yolo with read-only tools). The third dockerd-style runtime object: `Schedule` +
  `IScheduleStore`/`FileScheduleStore`, `GET/POST/DELETE /v1/schedules`, `orkis
  schedules [add|rm]`, fired by the `ScheduleRunner` background service — persisted in
  daemon state, adopted on restart. Missed firings are *skipped* (LastFiredAt advances
  to now, so the next future occurrence is computed rather than catching up), and a
  firing still running when the next is due is skipped with a log line (no overlapping
  firings on a shared workspace). Firings carry `Origin = schedule:<id>` (checkpointed,
  surfaced in run summaries and `/v1/runs`) for grouping. Continuity is per-schedule:
  *fresh*; *shared storage* (workspace + memory scope `sched-<id>`, via
  `RunnerFactory.CreateForScope`, same mechanism chats use); or *shared storage +
  handoff*, where the previous firing's final response text is injected verbatim into
  the next firing's prompt — a deterministic baton, not left to semantic recall. Full
  chat continuation across firings is deliberately *not* offered (unbounded transcripts
  are compaction debt with no audit benefit). Handoff capture is event-driven
  (`ScheduleHandoffService` on the run-event stream), so a firing that parks for
  approval and resumes still contributes its note. Remaining: a `write_handoff` tool if
  the final-text-as-handoff proves mushy, and PATCH/enable-disable over the wire (the
  model supports it; only create/list/delete are exposed).
- **Web UI** `[idea]` — the compose-stack UI, and the thing that makes the daemon
  usable day-to-day. **Design settled (July 2026): the daemon has no face and no
  network.** The daemon listens on its Unix socket only; a separate `Orkis.Web`
  gateway owns everything network — the TCP bind, auth, TLS (or the reverse proxy in
  front), the UI assets — and reverse-proxies `/v1/*` over the socket. Clients are
  unchanged: a URL endpoint now means the gateway. Auth is staged: loopback is exempt
  (like the socket), remote requires the bearer token (generated and persisted by the
  gateway, or `ORKIS_TOKEN`) — exchanged once at a login page for a cookie session
  (cookies ride `EventSource`, unlike Authorization headers), with **passkeys
  (WebAuthn, `Fido2NetLib`) as the v2 login**: enroll from an authenticated session,
  log in with a touch thereafter, the token demoted to break-glass recovery. Secure
  context caveat: passkeys work on localhost but need TLS for remote origins. UI
  stack: TypeScript + Lit + esbuild — three pinned npm dependencies, no more, with
  the lockfile discipline mirrored (`npm ci`, Dependabot npm ecosystem). The UI
  speaks only the public JSON+SSE protocol, so it stays disposable. Surfaces, roughly
  in order:
  runs + live event feed (`/v1/events`), approval inbox with grant buttons (the same
  sandbox/network lattice as the CLI prompt), capabilities/status, chat view (once
  chats land), cost accounting, artifact browser (needs a content endpoint —
  `GET /v1/artifacts/{name}` doesn't exist yet), audit view over supervision
  decisions (already in the event log), MCP/schedule management (the runtime-object
  APIs), prompt templates. Prerequisite endpoints are all built: transcript and
  artifact content (`GET /v1/artifacts/{name}`, `orkis artifacts <name> [-o]`).
- **Notifications** `[scaffold]` — the daemon ships the notification *primitive* (the
  event stream); delivery-to-a-human is presentation, done web-side. Tier 1 is built:
  the web UI toasts + fires a browser Notification on `run_paused` off the shell's
  single `/v1/events` subscription, with a live pending badge on the approvals tab —
  zero new backend. (Note: live SSE to the browser needs HTTP/2 over TLS to be robust
  under HTTP/1.1's per-host connection limit; the server side is correct — `no-store`,
  unbuffered, `: ping` heartbeat, infinite proxy timeout — verified via curl.)
  Tier 2: Web Push via the gateway (service worker + VAPID;
  subscriptions in gateway state next to sessions; the gateway holds its own client
  subscription to the daemon's stream and pushes on filtered events) — also the step
  that makes the UI an installable PWA, which may cover mobile entirely (Android and
  iOS ≥ 16.4 support PWA push). Tier 3, if a native app ever exists: the same
  gateway grows APNs/FCM. Accepted limitation: no gateway, no push — a daemon-only
  deployment consumes raw events and brings its own delivery. A daemon-side generic
  webhook remains a footnote for machine-to-machine integration if the need appears.
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
- **Configuration & runtime objects** `[scaffold]` — configuration has three lifetimes,
  and conflating them is the trap. (1) *Per-run choices* follow the keyed-supervisor
  pattern: registered under keys, selected by the run request, checkpointed so resume
  reconnects — models are this (built: keyed `IChatClient` registrations with
  `AgentRunRequest.ModelKey`), and it enables reviewer ≠ actor model splits. (2)
  *Runtime-mutable daemon objects*, dockerd-style API resources rather than config
  reloads — MCP servers are built as the first case: `GET/POST/DELETE /v1/mcp-servers`
  over an `McpServerRegistry` backed by a `MutableToolCatalog`, so a server connects or
  disconnects on a live daemon and its tools join or leave the catalogue (active tools
  are name-referenced and re-resolved per segment, so detachment degrades to a typed
  "tool no longer exists"). Boot servers seed the registry; the CLI (`orkis mcp
  list/add/rm`) and web UI drive it. Caveat recorded now: attaching a stdio MCP server
  is arbitrary command execution on the host, so runtime mutation is a privileged
  operation — it currently rides the same loopback/token trust as every other endpoint;
  a dedicated audit trail and per-operation authz is still owed. Config changes are events.
  Mutated objects that should survive restart live in daemon *state* (adopted on boot,
  like runs), never in the config file. (3) *Boot-only config* (sandbox plumbing,
  socket, data roots, auth): restart is the honest lifecycle; a daemon config file
  (models × providers × keys outgrow env vars) covers this, and file-config never
  overlaps API-mutated state — no reconciliation. The precursor is built:
  `GET /v1/capabilities` (and `orkis info`) enumerates registered models, supervisor
  keys, sandbox, tools, and memory/retrieval status, backed by `OrkisRegistrations`
  since keyed services cannot be enumerated from the container. The daemon config
  file (JSONC) is built for its first surface: `providers` (endpoint + credentials +
  kind — `openai` kind + `baseUrl` covers any OpenAI-compatible endpoint like
  OpenRouter or a local server) and `models` (a per-run key → provider + model id),
  plus the default model and the embedding model; secrets inline or via
  `apiKeyEnv`. Legacy env vars remain a fallback. Remaining: migrate the other
  boot-only config (dirs, sandbox, socket, mcp, corpus) folded into the same file, and
  the gateway reads its own `web` section from it too. Runtime MCP management
  (`GET/POST/DELETE /v1/mcp-servers`) is built. Remaining: gateway config beyond the
  `web` section (none needed yet), and audit/authz for the privileged runtime mutations.
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
  `ps`/`logs -f`/`approvals`/`approve`/`deny`/`resume`/`artifacts`). MCP servers now
  join the daemon composition too (`ORKIS_MCP_SERVER`, tools in the searchable
  catalogue), and remote clients are served by an optional bearer-token TCP listener
  (`ORKIS_LISTEN`/`ORKIS_TOKEN`; the Unix socket stays permission-authenticated, and
  TLS belongs to a reverse proxy). What remains: the compose-stack web UI and editor
  integrations.

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
- **Firecracker networking — Phase 2: domain allowlist** `[scaffold]` — an SNI-filtering
  egress proxy (no TLS interception) plus DNS control, for per-run domain scoping. The
  DNS half's first step is built: the network setup script provisions a dnsmasq
  forwarder on the gateway (`172.30.0.1:53`) with a targeted nft exception ahead of the
  host block, and guests list the gateway first with public fallbacks (musl queries in
  parallel, so nothing depends on the forwarder being present). Guest DNS thereby rides
  the host's own resolution wherever the forwarder is provisioned — and dnsmasq is the
  natural hook for the eventual per-run domain allowlist.
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
