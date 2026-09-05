#!/usr/bin/env bash
set -euo pipefail
# PowerShell 7 is also installed on the GitHub Ubuntu runners.
exec pwsh -NoProfile -File "$(dirname "$0")/Invoke-Chaos.ps1" "$@"
