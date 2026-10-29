# Machine-wide RimWorld slots, shared with other projects' test runners on this box (each game takes ~4.5 GB):
# take one of /tmp/rimworld-slot-{0..4}.lock for the whole run (released when the process exits, so a crash cannot
# leave a slot stuck), and do not start while MemAvailable is under 6 GB. Source this, then call take_rimworld_slot.
take_rimworld_slot() {
  local i
  while :; do
    local avail_kb
    avail_kb=$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo)
    if (( avail_kb >= 6 * 1024 * 1024 )); then
      for i in 0 1 2 3 4; do
        exec {RW_SLOT_FD}>"/tmp/rimworld-slot-$i.lock"
        if flock -n "$RW_SLOT_FD"; then
          echo "[slot] took /tmp/rimworld-slot-$i.lock" >&2
          return 0
        fi
        exec {RW_SLOT_FD}>&-
      done
    fi
    echo "[slot] no free RimWorld slot or under 6 GB available; waiting 30 s" >&2
    sleep 30
  done
}
