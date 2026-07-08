---
name: verify
description: How to live-verify RummiHub changes end-to-end — launch via Aspire, drive the Blazor UI with Playwright, and observe game flows in a real browser.
---

# Verifying RummiHub live

## Launch

```bash
aspire run   # background it; takes ~30–60s until all resources are Healthy
```

Find the web frontend URL with the Aspire MCP tool `list_resources` (resource
`webfrontend`, local endpoint is usually `http://localhost:5126`, which redirects to
`https://localhost:7178` with a dev cert — browsers must ignore HTTPS errors).

## Drive the UI (Playwright, python)

The devcontainer preinstalls the toolchain (`.devcontainer/install-ui-testing.sh`:
pip + playwright + headless Chromium). If it's ever missing, run that script — do NOT
apt-install `chromium-browser`, it's a snap stub that cannot run in this container.

Gotchas that cost time:

- `new_page(ignore_https_errors=True)` — dev cert.
- After `page.goto`, **wait ~2.5s for the Blazor circuit** (WebSocket) before filling
  inputs; earlier keystrokes never reach the server and buttons silently no-op.
- Lobby flow: `/lobby` → fill `#playerName` → click `.btn-action` (creates game) →
  `.btn-add-ai` → `.btn-start` → lands on `/game/{id}?player=Name`.
- To get a guaranteed-playable selection, click the `Auto-pick` helper button
  (`.helper-toggle button`) and wait for `.selected-tile-wrapper`. First plays need
  30+ points, so early turns often have **nothing playable** — if no tiles get
  pre-selected after `.solver-status.ready`, click `.btn-draw` and retry next turn.
- Useful selectors: `.turn-banner.my-turn` (my turn), `.btn-play` / `.btn-play-review`
  / `.btn-replay` / `.btn-clear` / `.btn-draw`, `.tiles-container.readonly`,
  `.player-chip.current .player-name`, `.turn-chip`.
- To check animations are actually running: `page.evaluate("document.getAnimations().length")`.
- Keyboard shortcuts go to `.game-container` (it holds focus): `.press("Enter")`.

A working end-to-end script (create game vs AI, play & review, replay, confirm,
play & continue) was `scratchpad/verify_review.py` in session 206d59ce — reuse its
helpers (`wait_my_turn`, `select_solver_tiles`) as a template.
