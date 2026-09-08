-- O que o billing precisa: a nota, e nada além dela.
--
-- Mesmo subconjunto portátil de 001 e 002 -- o CI aplica este arquivo ao
-- CockroachDB e ao PostgreSQL, e o glob de scripts/verify-schema-portability.sh
-- o inclui por existir, sem que ninguém precise citá-lo.
--
-- O billing divide `inbox` e `outbox` com o rental-core, de propósito. A coluna
-- `consumer` do inbox é o que separa os dois consumidores da MESMA mensagem, e
-- `aggregate_type` no outbox é o que separa quem publica o quê. Duas tabelas
-- paralelas dariam o mesmo resultado ao custo de duas implementações do mesmo
-- padrao para manter em sincronia.

-- A liquidação em unidades menores, e não em DECIMAL(12,2) como `rentals`.
--
-- O motivo é o contrato: rental.closed carrega agreed_total_minor como inteiro,
-- e invoice.issued devolve total_minor como inteiro. Guardar DECIMAL no meio
-- criaria duas conversões -- uma na entrada, outra na saída -- e portanto dois
-- lugares onde arredondar. A nota é o registro de um evento que já chegou em
-- centavos; ela fica em centavos.
CREATE TABLE IF NOT EXISTS invoices (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rental_id        UUID NOT NULL,
    rider_id         TEXT NOT NULL,
    currency         TEXT NOT NULL,
    plan_days        INT NOT NULL CHECK (plan_days > 0),
    days_used        INT NOT NULL CHECK (days_used >= 0),
    agreed_minor     BIGINT NOT NULL CHECK (agreed_minor >= 0),
    adjustment_minor BIGINT NOT NULL,
    total_minor      BIGINT NOT NULL CHECK (total_minor >= 0),
    reason           TEXT NOT NULL,
    rider_name       TEXT,
    issued_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (total_minor = agreed_minor + adjustment_minor)
);

-- A segunda garantia, independente do inbox.
--
-- O inbox impede que a MESMA mensagem seja tratada duas vezes. Isto impede que
-- o mesmo aluguel seja faturado duas vezes por mensagens DIFERENTES -- uma
-- republicação com id novo, um replay a partir do offset zero depois de o
-- inbox ter sido varrido pela retenção. As duas falham de formas diferentes e
-- só uma delas o inbox cobre.
CREATE UNIQUE INDEX IF NOT EXISTS one_invoice_per_rental ON invoices (rental_id);

CREATE INDEX IF NOT EXISTS invoices_by_rider ON invoices (rider_id, issued_at DESC);

GRANT USAGE ON SCHEMA public TO billing;
GRANT SELECT, INSERT ON TABLE invoices TO billing;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE inbox, outbox TO billing;
