#!/usr/bin/env bash
# Installs the UI-testing toolchain — Python Playwright driving a headless Chromium —
# so the Blazor UI can be exercised end-to-end inside the container (screenshots,
# animations, game flows). Used by agents and developers alike; the recipe that
# consumes it lives in .claude/skills/verify/SKILL.md.
#
# NOTE: the apt "chromium-browser" package is a snap stub that does not work in
# containers — Playwright's own Chromium download is the only option that runs here.
set -euo pipefail

sudo apt-get update
sudo apt-get install -y --no-install-recommends python3-pip

python3 -m pip install --break-system-packages playwright

# Headless shell only: enough to drive/screenshot the UI, ~100 MB smaller than full
# Chromium. --with-deps pulls the shared libraries the browser needs via apt.
python3 -m playwright install --with-deps --only-shell chromium
