# How Rim Prison was made and tested

Rim Prison is a **proof of concept and a work in progress**. AI coding agents wrote it over about two weeks in
October 2026 (16–28), directed by one person. This page says who did what, how it was checked, and what was not
checked.

## Who did what

**The maintainer (human)** set the goal and the direction. The goal: Prison Architect on the rim, a prison the player
designs and runs as warden, with inmates and staff who live out their own stories. The maintainer chose what to
build next, answered the design questions the agents put to them, decided what to publish, and supplied the game
install. They did not write the code by hand. The code has not had a line-by-line human review.

**AI agents** did the design, implementation, testing, reviews, art and documentation:

| Role | Tool and model |
|---|---|
| Design, implementation, fixes, test harness, test runs, docs | [Claude Code](https://claude.com/claude-code) with Claude Opus 5.5 |
| Features built from written specs | Claude Sonnet 5.5 and Claude Opus 5.5, run by a local agent orchestrator in isolated git worktrees (on the last day through OpenCode, when the usual runtime hit its limit) |
| Independent reviews | Claude Fable 5.1 and GPT-6 Astra (OpenAI Codex CLI) reviewed the design; Fable, then Opus, reviewed each feature, with Astra on some of them |
| Gap analyses (what a prison game still lacked, in order) | Claude Fable 5.1, GPT-6 Astra and Claude Opus 5.5 |
| Art | Drawn by code: `tools/gen-art.py` (stdlib Python, redrawn by GPT-6.1 Sol in the OpenAI Codex CLI after a first version by Claude) writes every texture and icon to PNG; `tools/check-art.py` checks them. No image-generation model was used |

The game's code was decompiled locally with ILSpy so the agents could read vanilla methods. The decompiled code was
used only as reference and is not in this repository.

The development log and plans are not published. Code comments that point at "the WORKLOG" refer to that log.
`DESIGN.md` is the design document as it grew, with the reviewers' amendments and the gap analyses' roadmaps. The
public history is squashed and its commits carry no AI attribution, so this page is the record of how it was made.

## Process

1. **Design.** A design document went to both reviewers before any code was written. Their amendments are in
   `DESIGN.md` ("Review amendments").
2. **Milestones.** The prison came first: zones painted as wings, the daily regime per security level, the shuttle
   that brings inmates, guards and staff jobs, needs and tension. Then the systems on top of it: gangs, contraband,
   programs and parole, trials, death row, the letters and their decisions, staff careers and corruption, ready-made
   facilities, and the screens.
3. **Gap analyses.** Seven times a reviewer compared the mod with Prison Architect and with what a warden would
   miss, and the maintainer picked from the list.
4. **Review and fix rounds.** Each feature was reviewed until it passed, often over two to four rounds. The reviews
   found real bugs, for example:
   - a Harmony patch target that did not exist, which aborted every patch after it;
   - guard dogs left without a handler when their handler died;
   - a restraint that read the policy after its own pat-down, so contraband it found gave no time in restraints;
   - a letter whose news no longer applied, which threw at delivery and was lost;
   - promotions that only reached the staff who happened to be on site at midnight.

   Every finding was fixed or explicitly declined with a reason. A fix with a regression check had to be shown to
   fail without the fix (the fix reverted, the game run, the check seen failing) before it counted.

## How it was tested

**Offline def check** (`make lint`, no game process). It loads the real `Assembly-CSharp.dll` and checks every XML
def field and cross-reference of the mod against the game's types, and flags keyed strings defined twice.

**In-game autotests** (`tools/autotest.sh`). The real game runs headless: the native Linux 1.7.4979 build for most
runs, and the Windows 1.7.4980 build under Wine, both offline, with all expansions. Each run builds a starter prison
(or one of the ready-made facilities), runs the given in-game days at full speed and runs its invariants
and feature checks. It fails on any error in the log that is not a known vanilla one. The README lists the plans.
A run without the expansions (Core, Harmony and this mod) was checked too.

**Census runs.** To measure flakiness, batches of about six runs (two of the events plan, saveload, normal, stress,
showcase) ran on the main line of development (numbered up to 85), and **24 stability rounds** fixed what they
turned up: flaky checks, and the product bugs behind some of them. The last batches before publishing passed 6, 5, 5,
6, 6, 3 and 5 of 6 runs. The round after the 3 of 6 fixed its cause; the last failure (the showcase prison's freezer
running warm) is still open.

**UI tour** (`tools/uitour.sh`, `tools/showcase-shots.sh`): a windowed run under Xvfb with software Vulkan that opens
every screen, or walks the prison, and takes a screenshot of each. The screenshots in the README come from it.

## What has not been tested

- **Real play.** It has run almost only in the automated harness. It has not been played through on a normal install
  or on a long-running save. Balance (money, tension, how often things happen) is tuned from automated runs only.
- **Flaky checks.** Some feature checks still fail now and then in the harness (a few timing-dependent ones are
  known); not every failure has been traced to a cause.
- **Unfinished work.** A planned-escape feature was in progress and an outside work party was planned; neither is
  in this version.
- **Other mods.** Only Harmony was loaded with it. No modded save was tried.
- **Multiplayer.** Not considered.

Use it on a copy of your save, or better, start a new prison from the main menu.
