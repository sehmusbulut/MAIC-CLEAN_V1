from pathlib import Path
import sys, tomllib, re

root = Path(__file__).resolve().parents[1]
required = [
    'AGENTS.md','process.md','memory/STATE.md','memory/DECISIONS.md',
    'docs/ARCHITECTURE.md','docs/ROADMAP.md','.codex/config.toml',
    'backend/AGENTS.md','apps/AGENTS.md',
    '.agents/skills/maic-orchestrate/SKILL.md',
    '.agents/skills/gitnexus-graph/SKILL.md',
]
errors=[]
for rel in required:
    p=root/rel
    if not p.exists() or p.stat().st_size == 0: errors.append(f'missing/empty: {rel}')

state=(root/'memory/STATE.md').read_text(encoding='utf-8').splitlines()
if len(state)>120: errors.append(f'STATE too long: {len(state)}/120')

road=(root/'docs/ROADMAP.md').read_text(encoding='utf-8')
next_count=len(re.findall(r'^Status:\s*NEXT\s*$', road, flags=re.M))
if next_count!=1: errors.append(f'ROADMAP must have exactly one NEXT, found {next_count}')

for p in sorted((root/'.codex/agents').glob('*.toml')):
    try:
        data=tomllib.loads(p.read_text(encoding='utf-8'))
        for key in ('name','description','developer_instructions'):
            if not str(data.get(key,'')).strip(): errors.append(f'{p.relative_to(root)} missing {key}')
    except Exception as e: errors.append(f'invalid TOML {p.relative_to(root)}: {e}')

try:
    cfg=tomllib.loads((root/'.codex/config.toml').read_text(encoding='utf-8'))
    gn=cfg.get('mcp_servers',{}).get('gitnexus',{})
    if not gn: errors.append('GitNexus MCP missing from .codex/config.toml')
    env=gn.get('env',{})
    if env.get('GITNEXUS_MCP_READ_ONLY')!='1': errors.append('GitNexus MCP should be read-only')
    if int(env.get('GITNEXUS_MCP_DEFAULT_MAX_TOKENS','0'))>2500: errors.append('GitNexus default token budget too high')
except Exception as e: errors.append(f'invalid .codex/config.toml: {e}')

if errors:
    print('HARNESS INVALID')
    for e in errors: print('-', e)
    sys.exit(1)
print('HARNESS OK')
print(f'STATE lines: {len(state)}/120')
print(f'Custom agents: {len(list((root/".codex/agents").glob("*.toml")))}')
print(f'Skills: {len(list((root/".agents/skills").glob("*/SKILL.md")))}')
print('ROADMAP NEXT count: 1')
