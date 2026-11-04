# MAIC — Daribase Product Parity

MAIC is a multi-tenant AI customer-support SaaS developed through **Codex + specialist agents + GitNexus**. The active target is Daribase-equivalent customer journeys and screens, plus explicit platform-owner dynamic management, under the MAIC brand.

Read [product target](docs/PRODUCT.md), [parity specification](docs/PRODUCT_PARITY.md) and the current task in [roadmap](docs/ROADMAP.md) only when needed. The historical foundation is complete; new parity tasks are planned, not implemented. Public site, tenant console and platform administration have distinct data/permission boundaries.

The operating goals are:

- preserve architecture instead of improvising per task,
- use specialist agents only in their own domains,
- use GitNexus as the structural/codebase brain,
- use a tiny resume state as the execution brain,
- avoid recursive repository rereads,
- keep reviews independent,
- finish one bounded roadmap task at a time,
- make `Start MAIC.` and `Continue MAIC.` sufficient for normal development.

## Two brains

**GitNexus** answers structural questions: where code lives, what calls what, impact/blast radius, routes, symbols, and changed-flow impact.

**`memory/STATE.md`** answers execution questions: what is currently being built, what already finished, the exact next action, files already touched, last verification, blockers, and which review gates remain.

Neither replaces the other.

## First use

1. Open or clone this repository.
2. Use project prerequisites: Git, Node.js 24 LTS/npm 11 per app READMEs, .NET 9 SDK, Docker/Desktop or local PostgreSQL.
3. From the repository root run `scripts/setup-gitnexus.ps1` on Windows or `scripts/setup-gitnexus.sh` on macOS/Linux.
4. Open the repository root in VS Code/Codex.
5. Verify the GitNexus MCP server appears in Codex. Project MCP configuration is already in `.codex/config.toml`.
6. Say: **`Start MAIC.`**
7. Afterwards say only: **`Continue MAIC.`**

If a session ends halfway through a task, `Continue MAIC.` must resume from `memory/STATE.md`; it must not rediscover the repository from scratch.

See `START_HERE.md` for the exact workflow.
# MAIC-CLEAN_V1
