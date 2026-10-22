#!/bin/sh
# Headless Linux GUI validation. Xorg/dummy is preinstalled in the cloud image.
set -eu
cd "$(dirname "$0")/.."
export LOCALAPPDATA="$PWD/.runtime"
mkdir -p "$LOCALAPPDATA"
if [ -n "${DISPLAY:-}" ]; then
    PYTHONPATH=. .venv/bin/python tests/gui_smoke.py
    PYTHONPATH=. .venv/bin/python tests/replica_gui_smoke.py
    exit
fi
export DISPLAY=:98
if [ -e /tmp/.X98-lock ]; then
    echo 'Display :98 is in use. Set DISPLAY to an available running display.' >&2
    exit 1
fi
Xorg "$DISPLAY" -config "$PWD/scripts/cloud-display.conf" -logfile "$LOCALAPPDATA/xorg.log" -nolisten tcp -noreset > "$LOCALAPPDATA/xorg-console.log" 2>&1 &
vision_display_pid=$!
trap 'kill "$vision_display_pid" 2>/dev/null || true; wait "$vision_display_pid" 2>/dev/null || true' EXIT INT TERM
# Readiness check, bounded to 5 seconds. No unverified installation or root access.
.venv/bin/python - <<'PY'
import time
import tkinter as tk
for attempt in range(50):
    try:
        root = tk.Tk()
        root.destroy()
        break
    except tk.TclError:
        time.sleep(0.1)
else:
    raise SystemExit('Virtual display failed; inspect .runtime/xorg.log')
PY
PYTHONPATH=. .venv/bin/python tests/gui_smoke.py
PYTHONPATH=. .venv/bin/python tests/replica_gui_smoke.py
