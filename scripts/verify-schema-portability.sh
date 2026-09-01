#!/usr/bin/env bash
#
# Aplica o schema do núcleo transacional a um engine e prova que a invariante
# de reserva dupla vale nele.
#
# O ADR 0004 afirma que o mesmo schema roda em qualquer store compatível com o
# protocolo Postgres. Como o engine local e o gerenciado são os dois CockroachDB,
# nada mais no repositório exercita essa afirmação — este script é o que a
# mantém honesta.
#
# Uso: verify-schema-portability.sh <admin-dsn> <app-dsn> <arquivo-de-bootstrap>

set -euo pipefail

ADMIN_DSN="${1:?informe o DSN administrativo}"
APP_DSN="${2:?informe o DSN do banco da aplicação}"
BOOTSTRAP="${3:?informe o arquivo de bootstrap do engine}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SQL_DIR="$ROOT/deploy/db/sql"
TEST_DIR="$ROOT/deploy/db/tests"

# NOTICEs de "already exists" são esperados na reaplicação e só poluem o log.
export PGOPTIONS="-c client_min_messages=warning"

apply() {
    psql -v ON_ERROR_STOP=1 --quiet "$1" -f "$2" >/dev/null
}

echo "==> bootstrap do engine"
apply "$ADMIN_DSN" "$BOOTSTRAP"

echo "==> aplicando o schema portátil"
apply "$APP_DSN" "$SQL_DIR/001_schema.sql"

echo "==> reaplicando (o schema precisa ser idempotente)"
apply "$APP_DSN" "$SQL_DIR/001_schema.sql"

echo "==> primeiro aluguel ativo — deve ser aceito"
apply "$APP_DSN" "$TEST_DIR/double_booking_setup.sql"

echo "==> segundo aluguel na mesma placa — deve ser recusado pelo banco"
if psql -v ON_ERROR_STOP=1 --quiet "$APP_DSN" \
        -f "$TEST_DIR/double_booking_conflict.sql" >/dev/null 2>&1; then
    echo "FALHA: o engine aceitou dois aluguéis ativos para a mesma placa." >&2
    echo "       O índice único parcial não está valendo aqui." >&2
    exit 1
fi
echo "    recusado, como esperado"

echo "==> devolvida a moto, alugar de novo — deve ser aceito"
apply "$APP_DSN" "$TEST_DIR/double_booking_release.sql"

echo "==> limpando"
psql -v ON_ERROR_STOP=1 --quiet "$APP_DSN" \
     -c "DELETE FROM rentals WHERE license_plate = 'CI0T35T';" \
     -c "DELETE FROM motorcycles WHERE license_plate = 'CI0T35T';" >/dev/null

echo "OK"
