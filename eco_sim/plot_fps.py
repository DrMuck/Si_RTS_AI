"""
Plot server FPS trajectory over the round.

Usage:
  python plot_fps.py                      # plots most recent
  python plot_fps.py fps_XXXX.csv         # specific
"""

import glob
import os
import sys
from pathlib import Path

try:
    import matplotlib.pyplot as plt
    import matplotlib.ticker as mticker
except ImportError:
    print("pip install matplotlib")
    sys.exit(1)

CSV_DIR = Path("E:/Steam/steamapps/common/Silica Dedicated Server/UserData/RTSA")


def latest():
    files = sorted(glob.glob(str(CSV_DIR / "fps_*.csv")),
                   key=os.path.getmtime, reverse=True)
    if not files:
        print(f"No fps_*.csv in {CSV_DIR}")
        sys.exit(1)
    return files[0]


def load(path):
    rows = []
    with open(path) as f:
        header = next(f).strip().split(",")
        for line in f:
            parts = line.strip().split(",")
            if len(parts) != len(header):
                continue
            r = {}
            for i, k in enumerate(header):
                v = parts[i]
                try:
                    r[k] = float(v) if "." in v else int(v)
                except ValueError:
                    r[k] = v
            rows.append(r)
    return rows


def plot(rows, path):
    t = [r["t_sec"] for r in rows]
    fps = [r["smoothed_fps"] for r in rows]
    peak_dt = [r["peak_dt_ms"] for r in rows]
    frames = [r["frames_in_window"] for r in rows]
    # Instantaneous "worst frame" FPS = 1000 / peak_dt_ms
    worst_fps = [1000.0 / max(dt, 0.1) for dt in peak_dt]

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(14, 8), sharex=True)

    # Top: smoothed FPS + worst frame FPS per second
    ax1.plot(t, fps, "b-", linewidth=2, label="smoothed serverFps (EMA)")
    ax1.plot(t, worst_fps, "r-", linewidth=0.8, alpha=0.6,
             label="worst-frame fps (1000/peak_dt) per 1s")
    ax1.axhline(240, color="green", linestyle=":", alpha=0.5, label="240 fps target")
    ax1.axhline(60, color="orange", linestyle=":", alpha=0.4, label="60 fps")
    ax1.set_ylabel("FPS")
    ax1.set_title(f"{os.path.basename(path)}: server FPS trajectory")
    ax1.legend(loc="lower left", fontsize=9)
    ax1.grid(True, alpha=0.3)
    ax1.set_ylim(0, max(260, max(fps) * 1.05))

    # Bottom: peak dt per second (spike detection)
    ax2.plot(t, peak_dt, "r-", linewidth=1)
    ax2.axhline(50, color="orange", linestyle=":", alpha=0.5, label="50ms (20 fps)")
    ax2.axhline(16.6, color="green", linestyle=":", alpha=0.5, label="16.6ms (60 fps)")
    ax2.axhline(4.16, color="blue", linestyle=":", alpha=0.5, label="4.16ms (240 fps)")
    ax2.set_xlabel("round time (s)")
    ax2.set_ylabel("worst frame dt in 1s window (ms)")
    ax2.legend(loc="upper left", fontsize=9)
    ax2.grid(True, alpha=0.3)

    # Vertical marker at 7-8 min for reference
    ax1.axvspan(420, 480, color="yellow", alpha=0.15, label="7-8 min window")
    ax2.axvspan(420, 480, color="yellow", alpha=0.15)

    plt.tight_layout()
    out = path.replace(".csv", ".png")
    plt.savefig(out, dpi=100, bbox_inches="tight")
    print(f"saved {out}")
    plt.show()


def main():
    if len(sys.argv) > 1:
        path = sys.argv[1]
        if not os.path.exists(path):
            path = str(CSV_DIR / sys.argv[1])
    else:
        path = latest()
    print(f"loading {path}")
    rows = load(path)
    if not rows:
        print("no rows")
        sys.exit(1)
    print(f"{len(rows)} samples, t = {rows[0]['t_sec']:.0f}s .. {rows[-1]['t_sec']:.0f}s")
    plot(rows, path)


if __name__ == "__main__":
    main()
