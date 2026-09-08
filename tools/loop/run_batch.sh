#!/bin/bash
# Run N headless rounds of a config, then extract -> stats -> (optionally) propose.
#   bash tools/loop/run_batch.sh <config-name> <rounds> [--propose] [--notes file.md]
# The config lives in configs/<name>/ (MelonPreferences.cfg, rtsai.json, optional
# commander_compositions.csv). The newest built Si_RTS_AI.dll is installed first.
set -u
CFG="$1"; N="${2:-5}"; shift 2
PROPOSE=0; NOTES=""
while [ $# -gt 0 ]; do case "$1" in --propose) PROPOSE=1;; --notes) NOTES="$2"; shift;; esac; shift; done
ROOT="C:/Users/schwe/Projects/Si_RTS_AI"
SERVER="E:/Steam/steamapps/common/Silica Dedicated Server"
RTSA="$SERVER/UserData/RTSA"
SRC="$ROOT/configs/$CFG"
DLL=$(ls -t "$ROOT/Si_RTS_AI/bin/Release"/Si_RTS_AI.dll | head -1)
[ -d "$SRC" ] || { echo "no config $SRC"; exit 1; }
say() { echo "[$(date +%F' '%T)] $*"; }
stop_server() {
  taskkill //IM Silica.exe //F >/dev/null 2>&1
  for i in $(seq 1 20); do tasklist //FI "IMAGENAME eq Silica.exe" | grep -q Silica.exe || break; sleep 2; done
  sleep 4
}
completed() { grep -l "END ROUND SUMMARY" "$RTSA"/round-*.log 2>/dev/null | grep -vE "Loading|Intro|MainMenu" | wc -l; }

say "== batch $CFG x$N with $(basename "$DLL")"
stop_server
cp "$SRC/MelonPreferences.cfg" "$SERVER/UserData/MelonPreferences.cfg"
cp "$SRC/rtsai.json" "$SERVER/UserData/rtsai.json"
[ -f "$SRC/commander_compositions.csv" ] && cp "$SRC/commander_compositions.csv" "$SERVER/UserData/commander_compositions.csv"
cp "$DLL" "$SERVER/Mods/Si_RTS_AI.dll"
before=$(completed)
powershell -NoProfile -Command "Start-Process -FilePath 'E:\Steam\steamapps\common\Silica Dedicated Server\Silica.exe' -ArgumentList '--melonloader.disablestartscreen' -WorkingDirectory 'E:\Steam\steamapps\common\Silica Dedicated Server'" >/dev/null 2>&1
t0=$SECONDS
# STALL WATCHDOG. On 2026-09-08 00:06 the server froze on a map change (Sol under
# the mod, HvH) and sat for 5.6 h with one instance alive and no round log growing.
# If the newest round log has not grown for STALL_MIN minutes, restart the server.
STALL_MIN=12
last_size=0; last_change=$SECONDS
newest_log() { ls -t "$RTSA"/round-*.log 2>/dev/null | grep -vE "Loading|Intro|MainMenu|melon" | head -1; }
while :; do
  done_now=$(( $(completed) - before ))
  if [ "$done_now" -ge "$N" ]; then say "$done_now rounds completed"; break; fi
  if [ $((SECONDS - t0)) -gt $((N * 3900 + 600)) ]; then say "timed out with $done_now rounds"; break; fi
  nl=$(newest_log); sz=0; [ -n "$nl" ] && sz=$(stat -c %s "$nl" 2>/dev/null || echo 0)
  if [ "$sz" != "$last_size" ]; then last_size=$sz; last_change=$SECONDS; fi
  if [ $((SECONDS - last_change)) -gt $((STALL_MIN * 60)) ]; then
    say "stall: no round-log growth for $STALL_MIN min — restarting the server"
    stop_server
    powershell -NoProfile -Command "Start-Process -FilePath 'E:\Steam\steamapps\common\Silica Dedicated Server\Silica.exe' -ArgumentList '--melonloader.disablestartscreen' -WorkingDirectory 'E:\Steam\steamapps\common\Silica Dedicated Server'" >/dev/null 2>&1
    last_change=$SECONDS; last_size=0
  fi
  sleep 60
done
stop_server
cd "$ROOT" && python tools/loop/extract.py --since "$(date +%Y%m%d)" && python tools/loop/stats.py > /dev/null && say "report: analysis/loop/report.md"
if [ "$PROPOSE" = "1" ]; then
  if [ -n "$NOTES" ]; then python tools/loop/propose.py --notes "$NOTES"; else python tools/loop/propose.py; fi
fi
