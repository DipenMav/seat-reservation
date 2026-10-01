#!/usr/bin/env bash
# One-command on-sale stampede + verification against any deployment.
#   ./burst.sh <BASE_URL> [--requests 20000] [--concurrency 500] [--seats 2000] [--hot-seats 5] [--admin-token admin]
set -euo pipefail

if [[ $# -lt 1 || "$1" == --* ]]; then
  echo "usage: ./burst.sh <BASE_URL> [--requests N] [--concurrency C] [--seats S] [--hot-seats H] [--admin-token T]" >&2
  exit 2
fi

cd "$(dirname "$0")"
DOTNET="$(command -v dotnet || true)"
[[ -z "$DOTNET" && -x "$HOME/.dotnet/dotnet" ]] && DOTNET="$HOME/.dotnet/dotnet"

if [[ -n "$DOTNET" ]]; then
  exec "$DOTNET" run -c Release --project tools/Burst -- "$@"
fi

# No .NET SDK installed: run the same tool inside the SDK container.
echo "dotnet not found; running the burst tool in mcr.microsoft.com/dotnet/sdk:10.0" >&2
exec docker run --rm --network host -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run -c Release --project tools/Burst -- "$@"
