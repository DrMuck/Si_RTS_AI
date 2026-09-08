#!/bin/bash
# Overnight performance and behaviour rounds for the Silica dedicated server.
#
#   bash tools/overnight.sh <plan-file> [log-file]
#
# Plan file: one experiment per line, "name|config-dir|probes|optimizer|koh"
#   config-dir : folder under configs/ holding MelonPreferences.cfg + rtsai.json
#   probes     : on|off   -> rtsai.json perfProbes = 46-probe list | ""
#   optimizer  : on|off   -> Mods/Si_ServerOptimizer.dll present or parked
#   koh        : on|off   -> Mods/Si_KingOfTheHill.dll present or parked
# Each experiment: stop the server, install the config, start the server, wait
# for the round to end (force-end at 60 min) or 75 min, stop the server.
# Everything the morning needs is in UserData/RTSA/round-*.log (budget lines,
# [FORCE] lines) and fps_*.csv; this script only writes the schedule log.

PLAN="$1"
LOG="${2:-$HOME/overnight.log}"
SERVER="E:/Steam/steamapps/common/Silica Dedicated Server"
CONFIGS="C:/Users/schwe/Projects/Si_RTS_AI/configs"
PROBES=$(cat "C:/Users/schwe/AppData/Local/Temp/claude/C--Users-schwe/5114e2a8-c2b5-4a63-b52e-36b03cf3322d/scratchpad/probes46.txt")
PARK="$SERVER/Mods/~disabled"

say() { echo "[$(date +%F' '%T)] $*" | tee -a "$LOG"; }

# ONLY THE DEDICATED SERVER. The server and the game client are both Silica.exe,
# so a bare "taskkill //IM Silica.exe" closes DrMuck's client mid-game.
SERVER_EXE='E:\Steam\steamapps\common\Silica Dedicated Server\Silica.exe'
server_pids() {
  powershell -NoProfile -Command "(Get-Process -Name Silica -ErrorAction SilentlyContinue | Where-Object { \$_.Path -eq '$SERVER_EXE' }).Id" 2>/dev/null | tr -d '\r' | grep -E '^[0-9]+$'
}
stop_server() {
  for pid in $(server_pids); do taskkill //PID "$pid" //F >/dev/null 2>&1; done
  for i in $(seq 1 20); do [ -z "$(server_pids)" ] && break; sleep 2; done
  sleep 4
}

set_mod() {  # set_mod <dll-name> on|off
  local dll="$1" want="$2"
  if [ "$want" = "on" ]; then
    [ -f "$SERVER/Mods/$dll" ] || { [ -f "$PARK/$dll" ] && mv "$PARK/$dll" "$SERVER/Mods/$dll"; }
  else
    [ -f "$SERVER/Mods/$dll" ] && mv "$SERVER/Mods/$dll" "$PARK/$dll"
  fi
}

set_probes() {  # set_probes on|off
  python - "$1" "$PROBES" "$SERVER/UserData/rtsai.json" <<'EOF'
import sys, io, re, json
want, probes, path = sys.argv[1], sys.argv[2], sys.argv[3]
t = io.open(path, encoding="utf-8").read()
val = probes if want == "on" else ""
t2 = re.sub(r'"perfProbes": "[^"]*"', '"perfProbes": "%s"' % val, t)
t2 = re.sub(r'"perfTimers": (true|false)', '"perfTimers": %s' % ("true" if want == "on" else "false"), t2)
json.loads(t2)
io.open(path, "w", encoding="utf-8", newline="\n").write(t2)
EOF
}

newest_round() { ls -t "$SERVER"/UserData/RTSA/round-*-NarakaCity.log 2>/dev/null | head -1; }

run_one() {
  local name="$1" cfg="$2" probes="$3" opt="$4" koh="$5"
  say "== $name: config=$cfg probes=$probes optimizer=$opt koh=$koh"
  stop_server
  cp "$CONFIGS/$cfg/MelonPreferences.cfg" "$SERVER/UserData/MelonPreferences.cfg"
  cp "$CONFIGS/$cfg/rtsai.json" "$SERVER/UserData/rtsai.json"
  set_probes "$probes"
  set_mod Si_ServerOptimizer.dll "$opt"
  set_mod Si_KingOfTheHill.dll "$koh"
  local before; before=$(newest_round)
  powershell -NoProfile -Command "Start-Process -FilePath 'E:\Steam\steamapps\common\Silica Dedicated Server\Silica.exe' -ArgumentList '--melonloader.disablestartscreen' -WorkingDirectory 'E:\Steam\steamapps\common\Silica Dedicated Server'" >/dev/null 2>&1
  local cur="" t0=$SECONDS
  while [ $((SECONDS - t0)) -lt 300 ]; do
    cur=$(newest_round); [ -n "$cur" ] && [ "$cur" != "$before" ] && break; sleep 10
  done
  if [ -z "$cur" ] || [ "$cur" = "$before" ]; then say "   no new round log after 5 min; skipping"; stop_server; return; fi
  say "   round log $(basename "$cur")"
  t0=$SECONDS
  while [ $((SECONDS - t0)) -lt 4500 ]; do
    grep -qE "ForceEndRound: elapsed|Utilisation \(final\)" "$cur" 2>/dev/null && break
    tasklist //FI "IMAGENAME eq Silica.exe" | grep -q Silica.exe || { say "   server process gone"; break; }
    sleep 30
  done
  say "   ended after $(( (SECONDS - t0) / 60 )) min; $(grep -c 'RTSA/PERF\] budget' "$cur") budget lines, $(grep -c '\[FORCE\] .* -> ' "$cur") phase changes"
  stop_server
  echo "$name|$(basename "$cur")" >> "$LOG.rounds"
}

say "overnight plan $PLAN"
while IFS='|' read -r name cfg probes opt koh; do
  [ -z "$name" ] && continue
  case "$name" in \#*) continue;; esac
  run_one "$name" "$cfg" "$probes" "$opt" "$koh"
done < "$PLAN"
# leave the mods as they were
set_mod Si_ServerOptimizer.dll on
set_mod Si_KingOfTheHill.dll on
say "plan complete; server stopped, optimizer and KoH restored"
