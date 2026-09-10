#!/usr/bin/env bash
set -euo pipefail

host="${COCKROACH_HOST:-cockroachdb:26257}"
until cockroach sql --insecure --host="$host" --execute 'SELECT 1' >/dev/null 2>&1; do
  sleep 2
done

cockroach sql --insecure --host="$host" --file=/sql/000_bootstrap.cockroach.sql
for file in /sql/0[0-9][0-9]_*.sql; do
  case "$file" in */000_bootstrap.*) continue ;; esac
  cockroach sql --insecure --host="$host" --database=projecty --file="$file"
done
