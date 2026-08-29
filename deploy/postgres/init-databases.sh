#!/usr/bin/env bash
set -Eeuo pipefail

create_database() {
    local database_name="$1"
    local database_exists

    if [[ -z "$database_name" ]]; then
        echo "A service database name is empty." >&2
        exit 1
    fi

    database_exists="$(
        psql --username "$POSTGRES_USER" --dbname postgres \
            --set=database_name="$database_name" --tuples-only --no-align <<'EOSQL'
SELECT 1 FROM pg_database WHERE datname = :'database_name';
EOSQL
    )"

    if [[ "$database_exists" == "1" ]]; then
        echo "Database $database_name already exists; skipping."
        return
    fi

    psql --username "$POSTGRES_USER" --dbname postgres \
        --set=database_name="$database_name" --set=ON_ERROR_STOP=1 <<'EOSQL'
SELECT format('CREATE DATABASE %I', :'database_name') \gexec
EOSQL
}

create_database "$AUTH_GATE_POSTGRES_DB"
create_database "$MOTO_HUB_POSTGRES_DB"
create_database "$RIDER_MANAGER_POSTGRES_DB"
