---
name: hearthpulse-ship
description: Use when the user hands over a Hearthpulse task and wants it carried from request to a working feature in the app without confirmations - split into sessions, verify, merge into main, push, build, restart the app, delete the finished sessions. Triggers - "/hearthpulse-ship", "доведи до приложения", "от задачи до готовой функции", "сделай и залей", "без подтверждений", or any feature/fix request while this flow is the agreed default.
---

# From a task to a working feature in the app

The owner said (2026-10-27): "я даю тебе задачу, ты разделяешь её на сессии, проверяешь всё ли сделано корректно,
делаешь пуш, после чего собираешь приложение и удаляешь сессию — я устал кучу раз подтверждать да коммит да слить".

**Calling this flow is the explicit permission** for: commits on `claude/*` branches, merging them into `main`,
`git push origin main`, rebuilding and restarting the local app, deleting the finished sessions, and removing their
merged worktrees and branches. Do not ask "commit?", "merge?", "push?" along the way. Report once at the end.

Still stop and ask only for:

- a release: version bump, tag `v*`, GitHub release (see [hearthpulse-release](../hearthpulse-release/SKILL.md));
- a `hearthpulse-legal` Blocker the owner has not already accepted;
- a choice that changes what the feature does (two reasonable behaviours, the request does not say which);
- tests or build still red after a real fix attempt; deleting anything that is not merged;
- `--render-ui` / `render.py` - only when the user asks for a render in words (the PC is used for playing).

Related: [hearthpulse-parallel-work](../hearthpulse-parallel-work/SKILL.md) (how to split by files),
[hearthpulse-review](../hearthpulse-review/SKILL.md), [hearthpulse-testing](../hearthpulse-testing/SKILL.md).

Paths: repo `D:\HeartLens-main\HeartLens-main` (`$repo`), worktrees `D:\HeartLens-main\wt-<name>`.

## 1. Split into sessions

- One session per independent area (files do not overlap - see parallel-work §1-3). A small task (a handful of
  edits) is one session; never do the feature in the coordinating session itself.
- Per session create the worktree yourself (the session folder `D:\HeartLens-main` is not a git repo):
  `git -C $repo worktree add D:\HeartLens-main\wt-<name> -b claude/<name> main`.
- `start_session` with `initiation: "user_asked"`, `cwd` = the worktree, `use_worktree: false`, `context: "fresh"`.
- Brief: goal in the user's words, owned files, both UIs (WPF + Avalonia) when user-facing, build `-m:2`,
  tests `--filter` while working and one full run at the end, no render, **commit to `claude/<name>` only, no push,
  no merge**, final report = commits + test counts + what was not checked.

## 2. Verify each finished session

When a session reports, check it yourself before merging - its note is not proof:

- `git -C $repo log --oneline main..claude/<name>` and `git -C $repo diff --stat main...claude/<name>`; read the
  diff of the risky parts (shared files, game memory, settings, data).
- Does it do what the user asked, in both UIs? Clutter, debug code, leftover TODOs?
- Missing piece or bug -> `SendMessage` the session to fix it in its branch, wait, check again.
- Touches game memory, input, third-party data/code, paid features or user data -> run the `hearthpulse-legal` agent.

## 3. Merge, build, test

In `$repo` on `main` (clean tree, `git pull --ff-only` first):

```powershell
git merge --no-edit refs/heads/claude/<name>        # one branch at a time, fewest conflicts first
Get-Process Hearthpulse -ErrorAction SilentlyContinue | Stop-Process -Force   # the exe locks bin\
dotnet build Hearthpulse.slnx -c Release -m:2
dotnet test tests\Hearthpulse.Tests -c Release --no-build
dotnet test tests\Hearthpulse.Presentation.Tests -c Release --no-build
```

- Build after **every** merge (API drift compiles only after the merge). Conflicts: keep both sides' intent;
  a combination fix is its own commit.
- 0 errors and all tests green, else fix (or send back to the session) - never push red.
- Commit messages: write to a file as UTF-8 **without BOM** (`[IO.File]::WriteAllText($f, $msg,
  (New-Object Text.UTF8Encoding($false)))`), then `git commit -F $f`; end with the Co-Authored-By line.

## 4. Push and check CI

`git push origin main`, then `gh run list --repo Zulut30/Hearthpulse_App --branch main -L 3` and
`gh run watch <id> --repo Zulut30/Hearthpulse_App --exit-status` in the background. Always pass `--repo`: without it
`gh` picks the `upstream` remote (Dev-NotAqua/HeartLens). Red CI -> find the cause, fix, push again; a CI-only flake is still a bug to fix.

## 5. Restart the app

```powershell
$exe = "$repo\src\Hearthpulse.App\bin\Release\net10.0-windows\Hearthpulse.exe"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
```

After ~10 s: the process `Responding`, and the tail of `%APPDATA%\Hearthpulse\hearthpulse.log` has no new errors.

## 6. Clean up

For each merged branch:

- `delete_session` for its session (only after its branch is in pushed `main`);
- `git -C $repo worktree remove D:\HeartLens-main\wt-<name>` (refuses a dirty tree - then look, do not force);
- `git -C $repo branch -d claude/<name>` (`-d`, not `-D`: it refuses unmerged work).

## 7. Report (once, in Russian, short)

What now works in the app, the commits on `main`, build/test counts, CI result, what was not checked (e.g. no render,
no live game), and anything left for later. Nothing else to confirm unless one of the stop cases above came up.
