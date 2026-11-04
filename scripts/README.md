# Scripts

- `setup-gitnexus.*`: one-time/initial GitNexus index using `--index-only`; it does not rewrite AGENTS or install upstream skills.
- `refresh-gitnexus.*`: refresh graph after meaningful code changes when stale.
- `validate_harness.py`: validates required control files, agent TOMLs, one NEXT roadmap item, state size and GitNexus MCP budget.

The project deliberately uses its own compact GitNexus skill because generated/upstream skills are not required for orchestration and can add context/noise. MCP provides the graph tools.

## Claude Code

- `.mcp.json` registers the same GitNexus MCP server as `.codex/config.toml` (read-only, 1800-token default budget,
  repo `MAIC`); `.claude/settings.json` pre-approves it. On Windows use `"command": "cmd", "args": ["/c", "npx", ...]`
  in a local override if `npx` is not resolved.
- `.claude/hooks/session-start.sh` runs only in Claude Code on the web (`CLAUDE_CODE_REMOTE=true`). It installs the .NET
  SDK if missing, restores NuGet packages, starts the Docker daemon for Testcontainers and builds the GitNexus index
  (`.gitnexus/` is not committed). The MCP tools read that index.
- Keyword (`query` BM25) search needs the LadybugDB FTS extension from `extension.ladybugdb.com`, and optional semantic
  search needs the embedding model from `huggingface.co`. Where the network policy blocks those hosts, `query` returns
  no hits, while `context`, `impact`, `detect_changes`, `route_map`, `trace` and the other graph tools work.

