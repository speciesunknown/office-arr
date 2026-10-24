#!/usr/bin/env python3
"""Create a new persistent agent under ~/office-arr/agents.

Sets up ~/office-arr/agents/<name>/ with outbox/, tmp/, skills/, scripts/, a .claude/skills
link to skills/, notes.md, and a CLAUDE.md that imports ../STANDING.md. Then adds
the agent to office-arr.code-workspace and to daboss's team table. start.sh picks up
new agents automatically.

Example:
  new-agent.py researcher "Web research and summaries; read-only" --title researcher
  new-agent.py writer "Docs and write-ups" --dry-run
"""
import argparse
import json
import re
import sys
from pathlib import Path

AGENTS = Path.home() / "office-arr" / "agents"
WORKSPACE = AGENTS.parent / "office-arr.code-workspace"
BOSS_MD = AGENTS / "daboss" / "CLAUDE.md"
SUBDIRS = ["outbox", "tmp", "skills", "scripts", ".claude"]

CLAUDE_MD = """\
# {name} — {title}

@../STANDING.md

You are **{name}**, a persistent session. Other agents (mainly `daboss`) send you
requests via SendMessage; they arrive as `<cross-session-message from="...">`.

## What you do
- {role}

## Rules
- If a request is missing something you need, ask the sender for it.
- Always reply with SendMessage, using the `from` attribute as `to`. Your plain
  text output is not visible to the sender.
- Send requests and results with the outbox pattern (STANDING.md section 5):
  write the full message in your `outbox/`, send a short pointer to it via SendMessage.

## Your folders
- `outbox/`: every message you send (STANDING.md section 5). Other agents read
  them here. Don't edit sent files.
- `tmp/`: scratch space. Nothing here is kept.
- `skills/`: your skills (one folder per skill, each with `SKILL.md`). Loaded via `.claude/skills`.
- `scripts/`: reusable scripts you write for yourself.
- `notes.md`: your running notes.
"""


def add_to_workspace(name, dry):
    data = json.loads(WORKSPACE.read_text())
    folders = data.setdefault("folders", [])
    path = f"agents/{name}"
    if any(f.get("path") == path for f in folders):
        print(f"workspace: {name} already listed")
        return
    folders.append({"name": name, "path": path})
    print(f"workspace: add {name}")
    if not dry:
        WORKSPACE.write_text(json.dumps(data, indent=2) + "\n")


def add_to_team_table(name, role, dry):
    lines = BOSS_MD.read_text().splitlines(keepends=True)
    if any(l.startswith(f"| `{name}` |") for l in lines):
        print(f"daboss team table: {name} already listed")
        return
    rows = [i for i, l in enumerate(lines) if re.match(r"\| `[^`]+` \| `~/office-arr/agents/", l)]
    if not rows:
        print("daboss team table: no table found, skipped (add the row by hand)", file=sys.stderr)
        return
    lines.insert(rows[-1] + 1, f"| `{name}` | `~/office-arr/agents/{name}` | {role} |\n")
    print(f"daboss team table: add {name}")
    if not dry:
        BOSS_MD.write_text("".join(lines))


def main():
    p = argparse.ArgumentParser(
        prog="new-agent.py",
        description=__doc__.split("\n\n")[0],
        epilog=__doc__.split("\n\n", 1)[1],
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("name", help="agent name: lowercase letters, digits and dashes (also its session name)")
    p.add_argument("role", help="one-line description of what the agent does")
    p.add_argument("--title", default="agent", help='short title for the CLAUDE.md heading, e.g. "bug hunter" (default: agent)')
    p.add_argument("--dry-run", action="store_true", help="show what would be done without changing anything")
    a = p.parse_args()

    if not re.fullmatch(r"[a-z][a-z0-9-]*", a.name):
        p.error("name must be lowercase letters, digits and dashes, starting with a letter")
    home = AGENTS / a.name
    if home.exists():
        p.error(f"{home} already exists")

    print(f"create {home}/ with {', '.join(SUBDIRS)}, CLAUDE.md, notes.md")
    if not a.dry_run:
        for d in SUBDIRS:
            (home / d).mkdir(parents=True)
        (home / ".claude" / "skills").symlink_to("../skills")
        (home / "CLAUDE.md").write_text(CLAUDE_MD.format(name=a.name, role=a.role, title=a.title))
        (home / "notes.md").touch()

    add_to_workspace(a.name, a.dry_run)
    add_to_team_table(a.name, a.role, a.dry_run)
    print(f"done. Edit {home}/CLAUDE.md to flesh out the role, then run ~/office-arr/agents/start.sh")


if __name__ == "__main__":
    main()
