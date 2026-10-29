#!/usr/bin/env bash
# Pictures of a whole prison for the README: runs the game windowed (Xvfb + lavapipe, as tools/uitour.sh) with plan "shots" on the
# given facility and saves a PNG per step to .work/shots-showcase/<facility>/ (overview, cell block, gate, reception, landing area,
# infirmary, control room, canteen at lunch, workshop, yard, visitation, library, chapel, the night lockdown).
# Usage: tools/showcase-shots.sh [penitentiary|inherited]      Env: RW_RUNNER, RW_RES (default 1920x1080), RW_GAME_DIR, COLONY_SCALE
# RIMPRISON_TOUR=handover: the prison is handed over with its past (the old warden's secrets sown) and the first picture is the old warden's note, the welcome letter.
set -uo pipefail
facility=${1:-penitentiary}
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# The windowed runner and the screenshot converter live in the colony-scale repo (a sibling of this repo or of the repo's worktree folder).
colony=${COLONY_SCALE:-}
if [[ -z $colony ]]; then
  d=$root
  while [[ $d != / ]]; do
    [[ -d $d/../colony-scale/tools/headless ]] && { colony=$(cd "$d/../colony-scale" && pwd); break; }
    d=$(dirname "$d")
  done
fi
[[ -n $colony ]] || { echo "colony-scale repo not found; set COLONY_SCALE" >&2; exit 1; }
runner=${RW_RUNNER:-$colony/tools/headless/run-game.sh}
shots=$root/.work/shots-showcase/$facility
savedata=$root/.work/savedata-shots
log=$root/.work/run/shots-$facility.log
mkdir -p "$shots" "$savedata/Config" "$root/.work/run"
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
# (A mod list without this mod starts the game at the main menu and waits out the timeout with no pictures.)
grep -qi "urekd.rimprison" "$savedata/Config/ModsConfig.xml" || { echo "[showcase-shots] $savedata/Config/ModsConfig.xml does not list urekd.rimprison (set RW_MODSCONFIG)" >&2; exit 1; }
res=${RW_RES:-1920x1080}
cat > "$savedata/Config/Prefs.xml" <<P
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <screenWidth>${res%x*}</screenWidth>
  <screenHeight>${res#*x}</screenHeight>
  <fullscreen>False</fullscreen>
  <customCursorEnabled>False</customCursorEnabled>
  <runInBackground>True</runInBackground>
  <langFolderName>English</langFolderName>
  <devMode>True</devMode>
</PrefsData>
P
extra=()
[[ $facility == penitentiary ]] && extra=(-rimprison-facility=penitentiary -rimprison-plan=shots) || extra=(-rimprison-plan=shots)
# The game to run: RW_GAME_DIR, else a native Linux build next to the Windows install (RimWorld-linux), else the
# Windows install under Wine (the runner's default).
RIMWORLD_DIR=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")}
[[ -n ${RW_GAME_DIR:-} || ! -x $(dirname "$RIMWORLD_DIR")/RimWorld-linux/RimWorld ]] || RW_GAME_DIR=$(dirname "$RIMWORLD_DIR")/RimWorld-linux
RIMWORLD_DIR=$RIMWORLD_DIR RW_GAME_DIR=${RW_GAME_DIR:-} RW_WINDOWED=1 RW_RES=$res RW_SAVEDATA=$savedata RW_LOG=$log \
  RW_TIMEOUT=${RW_TIMEOUT:-2400} "$runner" -rimprison-test=99 "${extra[@]}" &
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
      && echo "[showcase-shots] shot $n $name" >&2
    taken="$taken $n"
  done < <(grep -oE "\[RimPrison:ui\] step [0-9]+ [a-z0-9-]+" "$log" | awk '{print $3, $4}')
done
wait $runner_pid
ls "$shots"
