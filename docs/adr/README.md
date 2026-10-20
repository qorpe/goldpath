# ADR Index — The Goldpath Constitution

These decisions are the constitution of Goldpath (see [foundation](../strategy/foundation.md) §2 —
the first ten; 0011+ extend it by the same process). If one of them changes, the product has
effectively been redesigned; a change requires a new superseding ADR. Format: MADR-lite.
Statuses: `proposed | accepted | superseded-by-XXXX`.

| ADR | Title | Status |
|---|---|---|
| [0001](ADR-0001-manifest-single-source-of-truth.md) | The manifest is the single source of truth | accepted |
| [0002](ADR-0002-industry-standard-specs.md) | Spec formats are industry standards, no custom DSL | accepted |
| [0003](ADR-0003-configure-not-wrap-microsoft.md) | The Microsoft layer is configured, not wrapped | accepted |
| [0004](ADR-0004-deterministic-agentic-split.md) | Deterministic/agentic split | accepted |
| [0005](ADR-0005-executable-standards.md) | Standards are executable | accepted |
| [0006](ADR-0006-ai-lives-in-the-dev-layer.md) | AI lives in the development layer, not in the library | accepted |
| [0007](ADR-0007-skills-versioned-with-standards.md) | Skills live in the same repo/version as the standards and are eval'd | accepted |
| [0008](ADR-0008-first-click-run-is-proven.md) | "Runs with one click" is a proven feature | accepted |
| [0009](ADR-0009-docs-are-generated.md) | Docs are generated, not handwritten | accepted |
| [0010](ADR-0010-human-gates-are-fixed.md) | Human gates are fixed | accepted |
| [0011](ADR-0011-runtime-ai-is-an-opt-in-module.md) | Runtime AI is an opt-in module — the core stays AI-free | accepted |
| [0012](ADR-0012-goldpath-is-a-platform.md) | Goldpath is a platform — product modules ride the published core | accepted |
| [0013](ADR-0013-messaging-bus-is-a-swappable-engine.md) | The message bus is a swappable engine behind a Goldpath-owned seam (scoped amendment of 0003) | accepted |
| [0014](ADR-0014-agent-verified-changes-merge-automatically.md) | Agent-verified changes merge automatically — except decisions, releases and the gates (scoped amendment of 0010) | proposed |
