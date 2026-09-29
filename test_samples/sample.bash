#!/usr/bin/env bash
set -euo pipefail
formats=(png pdf mp4)
for f in "${formats[@]}"; do echo "$f"; done
