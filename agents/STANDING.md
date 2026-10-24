# Standing instructions (all agents)

Every agent follows these. They take precedence over the agent's own CLAUDE.md where the two conflict.

## 1. No command-line gymnastics
Keep shell commands simple. Don't chain a command into pipes, `grep`/`sed`/`awk`
filters, subshells or loops to process its output in one go. Instead, use two turns:

1. **Capture:** run the command and send its output to a temp file with a random name
   in your own `tmp/` folder, printing the path so you know where it went:
   `f=$(mktemp ./tmp/out.XXXXXX) && mytool --list > "$f" 2>&1; echo "$f"`
2. **Process:** in a separate turn, read or work on that file (Read tool, or a simple
   command on the file).

Delete the temp file when you're finished with it.

## 2. New skills go in your own skills folder
Create every new skill in your own `skills/` folder (`~/office-arr/agents/<you>/skills/<skill-name>/SKILL.md`).
Never create or edit skills at machine level (`~/.claude/skills/`) or in another agent's folder.
The user decides whether to promote a skill to machine level, and does the promoting.
When you create a skill, tell the user its name, path and what it's for.

## 3. Every skill ships with a deterministic script
- Each skill comes with a script that handles all the deterministic parts: anything
  that would give the same result every time (fetching, parsing, formatting, file
  layout, validation, calculations). The skill's `SKILL.md` covers only the parts that
  need judgement, and tells the agent to run the script for the rest.
- Keep the script inside the skill folder (`skills/<skill-name>/scripts/`) so it travels
  with the skill if it's promoted.
- The script must support `--help`, printing what it does, its arguments and options,
  and an example. `SKILL.md` should point to `--help` rather than repeat the usage.

## 4. Use your scripts folder for repetitive tasks
- When you notice you're doing the same task, or the same sequence of commands, more
  than once, consider turning it into a script in your own `scripts/` folder.
- Check `scripts/` before starting a task, in case a script for it already exists.
- Scripts here follow the same rule as skill scripts: each supports `--help`.
- At the end of a turn, consider if a script might save tokens later.

## 5. Inter-agent messages: the outbox pattern
Messages between agents are short pointers to a file in the sender's own outbox.
(`@file` attachments don't work across sessions, and long messages bloat everyone's
context.)

**Sending**
1. Write the full message to a file in your own outbox:
   `~/office-arr/agents/<you>/outbox/<YYYYMMDD-HHMMSS>-<you>-<topic>.md`
   Start it with a header, then the body:
   ```
   from: <you>
   to: <recipient>
   re: <outbox file this replies to, or "new">
   subject: <one line>
   ---
   <the request or result: everything the recipient needs, self-contained>
   ```
   Don't edit a file once it's sent; your outbox is the record of what you sent.
2. Send a SendMessage whose first line names the subject and the outbox path, e.g.
   `Review request: ~/office-arr/agents/daboss/outbox/20261023-101500-daboss-auth-diff.md`

**Receiving**
1. When a message points to a file in another agent's outbox, read that file; it's
   the real message. Never edit or move files in another agent's outbox.
2. Do the work, and reply the same way: write to your own outbox with `re:` set to
   the file you got, and send a pointer message.

Only quick one-liners (an acknowledgement, a short clarifying question) can go in
the SendMessage itself.

## 6. Write scripts in Python
Use Python 3 for every new script, in your `scripts/` folder and in skills, unless the
user agrees otherwise.

- Name scripts `<name>.py`, start them with `#!/usr/bin/env python3`, and `chmod +x` them.
- Stick to the standard library where you can. If you need a third-party package, ask
  the user first.
- Use `argparse`: it gives you `--help` for free. Put a description and an example in it.
  ```python
  #!/usr/bin/env python3
  """<What it does, one line.>

  Example:
    <name>.py foo --flag
  """
  import argparse

  def main():
      p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                  epilog=__doc__.split("\n\n", 1)[1],
                                  formatter_class=argparse.RawDescriptionHelpFormatter)
      p.add_argument("target", help="...")
      a = p.parse_args()
      # ...

  if __name__ == "__main__":
      main()
  ```
- Run a script as `./scripts/<name>.py args` from your folder, or by its full path.
