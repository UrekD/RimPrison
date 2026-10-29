#!/usr/bin/env bash
# Windowed UI tour with screenshots: runs the game (Xvfb + lavapipe) with -rimprison-test plan uitour and saves a
# PNG per UI step to .work/shots/. Needs ../colony-scale's windowed run-game.sh (RW_WINDOWED=1).
# Usage: tools/uitour.sh [penitentiary]   (the tour in the state penitentiary instead of the inherited facility; pictures in .work/shots-penitentiary/)
# RIMPRISON_TOUR=handover tools/uitour.sh [penitentiary]: only the old warden's note (the welcome letter of a prison handed over with its past), the Intel screen, the chronicle and the overview.
# RIMPRISON_TOUR=evacuation tools/uitour.sh: only the Deployment screen's evacuation block - two assembly points marked, then a drill running with the accountability list.
# For pictures of the prison itself (the overview, a cell block, the canteen at lunch, ...) see tools/showcase-shots.sh.
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# colony-scale is a sibling of this repo (or of the repo's worktree folder): walk up to find it.
colony=${COLONY_SCALE:-$root/../colony-scale}
if [[ ! -d $colony/tools/headless ]]; then
  d=$root
  while [[ $d != / ]]; do
    [[ -d $d/../colony-scale/tools/headless ]] && { colony=$(cd "$d/../colony-scale" && pwd); break; }
    d=$(dirname "$d")
  done
fi
runner=${RW_RUNNER:-$colony/tools/headless/run-game.sh}
facility=${1:-}
extra=()
shots=$root/.work/shots
if [[ $facility == penitentiary ]]; then
  extra=(-rimprison-facility=penitentiary)
  shots=$root/.work/shots-penitentiary
fi
savedata=$root/.work/savedata-ui
log=$root/.work/run/uitour.log
mkdir -p "$shots" "$savedata/Config" "$root/.work/run"
# One machine-wide RimWorld slot for the whole run (see rimworld-slot.sh).
source "$root/tools/rimworld-slot.sh"
take_rimworld_slot
rm -f "$shots"/*.png "$log"
# The mod list the game loads (Harmony and this mod: without them nothing loads): RW_MODSCONFIG, else the first ModsConfig.xml of a test savedata folder of this checkout or of the main one.
if [[ ! -f $savedata/Config/ModsConfig.xml ]]; then
  main=$(cd "$(git -C "$root" rev-parse --git-common-dir 2>/dev/null)/.." 2>/dev/null && pwd || echo "$root")
  for cand in "${RW_MODSCONFIG:-}" "$root"/.work/savedata*/Config/ModsConfig.xml "$main"/.work/savedata*/Config/ModsConfig.xml; do
    [[ -n $cand && -f $cand ]] && { cp "$cand" "$savedata/Config/"; break; }
  done
  [[ -f $savedata/Config/ModsConfig.xml ]] || { echo "no ModsConfig.xml found: set RW_MODSCONFIG to one that lists Harmony and Rim Prison" >&2; exit 1; }
fi
cat > "$savedata/Config/Prefs.xml" <<'P'
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <screenWidth>1280</screenWidth>
  <screenHeight>720</screenHeight>
  <fullscreen>False</fullscreen>
  <customCursorEnabled>False</customCursorEnabled>
  <runInBackground>True</runInBackground>
  <langFolderName>English</langFolderName>
  <devMode>True</devMode>
</PrefsData>
P
RIMWORLD_DIR=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")} RW_WINDOWED=1 RW_RES=1280x720 RW_SAVEDATA=$savedata RW_LOG=$log \
  RW_TIMEOUT=${RW_TIMEOUT:-1500} "$runner" -rimprison-test=99 -rimprison-plan=uitour "${extra[@]}" &
runner_pid=$!
fb=$(dirname "$log")/Xvfb_screen0
taken=""
while kill -0 $runner_pid 2>/dev/null; do
  sleep 3
  [[ -f $log ]] || continue
  while read -r n name; do
    [[ " $taken " == *" $n "* ]] && continue
    sleep 14   # let the window draw a few frames (software Vulkan is slow)
    python3 "$colony/tools/headless/xwd2png.py" "$fb" "$shots/$(printf %02d "$n")-$name.png" 2>/dev/null \
      && echo "[uitour] shot $n $name" >&2
    taken="$taken $n"
  done < <(grep -oE "\[RimPrison:ui\] step [0-9]+ [a-z0-9-]+" "$log" | awk '{print $3, $4}')
done
wait $runner_pid
ls "$shots"
