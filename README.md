# Orkis

An agentic AI system for .NET.

Orkis provides the building blocks for LLM-driven agents — retrieval-augmented generation, reranking, tool calling, and sandboxed execution — as composable libraries with first-class dependency injection support.

> **Status:** early development. APIs are unstable and everything is subject to change.

## Features

- **Agentic tool calling** — an orchestration loop that lets models plan, call tools, and act on results
- **RAG** — retrieval-augmented generation over your own data, with pluggable vector store backends
- **Reranking** — second-stage relevance scoring to sharpen retrieval results before they reach the model
- **Sandboxed execution** — run model-generated code and untrusted tool operations in isolation

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
