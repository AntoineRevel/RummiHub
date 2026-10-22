#!/bin/bash
# SessionStart hook for Claude Code cloud sessions (claude.ai/code).
# Brings the container up to what .devcontainer/ gives a local developer, so
# `dotnet build`, `dotnet test`, `aspire run` and the /verify skill all work:
#   - .NET 10 SDK + NuGet restore
#   - Aspire CLI (also backs the `aspire` MCP server declared in .mcp.json)
#   - Docker daemon + the Redis Stack images the AppHost needs
#   - ASP.NET dev certificate
#   - Python Playwright, matched to the Chromium preinstalled in /opt/pw-browsers
# Idempotent: every step is skipped when already done, and the container is
# snapshotted after the first run, so later sessions start fast.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(pwd)}"

log() { echo "[session-start] $*" >&2; }

# ---------------------------------------------------------------- .NET SDK
DOTNET_DIR="$HOME/.dotnet"
# (Checks capture output first: `cmd | grep -q` under pipefail fails when grep exits early.)
sdks="$("$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null || true)"
if ! grep -q '^10\.' <<< "$sdks"; then
  log "Installing .NET 10 SDK..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR" >&2
  rm -f /tmp/dotnet-install.sh
fi

# ---------------------------------------------------------------- Aspire CLI
ASPIRE_DIR="$HOME/.aspire/bin"
if [ ! -x "$ASPIRE_DIR/aspire" ]; then
  log "Installing Aspire CLI..."
  curl -fsSL https://aspire.dev/install.sh -o /tmp/aspire-install.sh
  bash /tmp/aspire-install.sh --install-path "$ASPIRE_DIR" --skip-path >&2
  rm -f /tmp/aspire-install.sh
fi

# MCP servers (.mcp.json → `aspire agent mcp`) are spawned with the base PATH,
# not the one below, so expose both CLIs on it as well.
ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet
ln -sf "$ASPIRE_DIR/aspire" /usr/local/bin/aspire

export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$ASPIRE_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ASPIRE_CLI_TELEMETRY_OPTOUT=1

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  cat >> "$CLAUDE_ENV_FILE" <<EOF
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$DOTNET_DIR/tools:$ASPIRE_DIR:\$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export ASPIRE_CLI_TELEMETRY_OPTOUT=1
EOF
fi

# ---------------------------------------------------------------- NuGet
log "Restoring NuGet packages..."
dotnet restore --verbosity quiet >&2

# ---------------------------------------------------------------- Dev cert
if ! dotnet dev-certs https --check >/dev/null 2>&1; then
  log "Creating ASP.NET dev certificate..."
  # Full trust is impossible on Linux; partial trust is enough (browsers ignore HTTPS errors).
  dotnet dev-certs https --trust >&2 || dotnet dev-certs https >&2
fi

# ---------------------------------------------------------------- Docker + Redis
# Anonymous Docker Hub pulls are rate-limited (429) from the shared egress, so
# docker.io images are fetched through Google's public Docker Hub mirror.
if command -v dockerd >/dev/null 2>&1; then
  if [ ! -f /etc/docker/daemon.json ]; then
    mkdir -p /etc/docker
    echo '{ "registry-mirrors": ["https://mirror.gcr.io"] }' > /etc/docker/daemon.json
  fi
  if ! docker info >/dev/null 2>&1; then
    log "Starting Docker daemon..."
    # A restored snapshot can carry the pid file of a daemon that no longer runs.
    rm -f /var/run/docker.pid
    setsid nohup dockerd > /var/log/dockerd.log 2>&1 < /dev/null &
    for _ in $(seq 1 30); do
      docker info >/dev/null 2>&1 && break
      sleep 1
    done
  fi
  if docker info >/dev/null 2>&1; then
    # The Redis image of RummiHub.AppHost/AppHost.cs; Aspire pulls RedisInsight itself.
    image=docker.io/redis/redis-stack-server:latest
    docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >&2 \
      || log "WARNING: could not pull $image (aspire run will retry)"
  else
    log "WARNING: Docker daemon did not start; see /var/log/dockerd.log (aspire run needs it for Redis)"
  fi
fi

# ---------------------------------------------------------------- Playwright
# Pinned to the release whose Chromium build is preinstalled in $PLAYWRIGHT_BROWSERS_PATH
# (chromium-1194); bump both together.
PLAYWRIGHT_VERSION=1.56.0
if [ "$(python3 -I -c 'import importlib.metadata as m; print(m.version("playwright"))' 2>/dev/null || true)" \
     != "$PLAYWRIGHT_VERSION" ]; then
  log "Installing Python Playwright $PLAYWRIGHT_VERSION..."
  python3 -m pip install --break-system-packages -q "playwright==$PLAYWRIGHT_VERSION" >&2
fi

log "Environment ready."
