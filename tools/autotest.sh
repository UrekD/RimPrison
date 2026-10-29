#!/usr/bin/env bash
# Run the in-game Rim Prison autotest headless and print a summary.
# Usage: tools/autotest.sh [days] [extra game args...]     (default 2 days)
# Env: RW_TIMEOUT (default 1500 s), RW_RUNNER (default ../colony-scale/tools/headless/run-game.sh),
#      RW_GAME_DIR (game install to run; default the Windows build under Wine), RW_SAVEDATA, RW_LOG
# Output: .work/run/autotest.log (full log), .work/run/autotest.summary (metrics + result).
# Exit: 0 PASS, 1 FAIL, 2 no result (crash/timeout).
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
days=${1:-2}; shift || true
runner=${RW_RUNNER:-$root/../colony-scale/tools/headless/run-game.sh}
log=${RW_LOG:-$root/.work/run/autotest.log}
summary=${log%.log}.summary
mkdir -p "$root/.work/run"
# One machine-wide RimWorld slot for the whole run (see rimworld-slot.sh).
source "$root/tools/rimworld-slot.sh"
take_rimworld_slot
# setsid: returning to the main menu (saveload plan) signals the game's whole process group, which would also kill
# this script and the runner's log pipe.
RIMWORLD_DIR=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")} RW_SAVEDATA=${RW_SAVEDATA:-$root/.work/savedata} RW_LOG=$log \
  RW_TIMEOUT=${RW_TIMEOUT:-1500} RW_WAIT_FOR='RimPrison:test\] (PASS|FAIL)' \
  setsid --wait "$runner" "-rimprison-test=$days" "$@" < /dev/null
{
  grep -E "^[0-9:]+ +(INFO|WARN|ERROR)" "$log" | grep -vE "Resolution too small|Steamworks|textures have no pixels|Stub! You may not" \
    | grep -E "RimPrison|ERROR|Exception" | cut -c1-600
  grep -E "Config error|XML error|Could not resolve" "$log" | sort -u | sed 's/^/[defs] /' 
} > "$summary"
cat "$summary"
if grep -qE "Config error|XML error|Could not resolve cross-reference" "$log"; then echo "[autotest] def errors at load" >&2; exit 1; fi
grep -q "RimPrison:test\] PASS" "$log" && exit 0
grep -q "RimPrison:test\] FAIL" "$log" && exit 1
exit 2
