# Orkis

An agentic AI system for .NET.

Orkis provides the building blocks for LLM-driven agents — retrieval-augmented generation, reranking, tool calling, and sandboxed execution — as composable libraries with first-class dependency injection support.

> **Status:** early development. APIs are unstable and everything is subject to change.

## Features

- **Agentic tool calling** — an orchestration loop that lets models plan, call tools, and act on results
- **RAG** — retrieval-augmented generation over your own data, with pluggable vector store backends and a first-class ingestion pipeline (parsing, chunking, embedding)
- **Reranking** — second-stage relevance scoring to sharpen retrieval results before they reach the model
- **Sandboxed execution** — run model-generated code and untrusted tool operations in isolation, with graduated sandbox levels
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
  Orkis.Rag.*             Vector store / retrieval implementations
  Orkis.Rerank.*          Reranker implementations
  Orkis.Sandbox.*         Sandbox execution implementations
  Orkis.Host              Composition root and application entry point
tests/
```

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Getting started

Coming soon — the solution is being scaffolded.

## License

TBD
