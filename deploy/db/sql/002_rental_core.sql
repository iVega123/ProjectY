-- O que o rental-core precisa além do núcleo transacional de 001_schema.sql.
--
-- 001 descreve o modelo: motos, aluguéis, outbox e inbox, com a restrição
-- parcial que impede a dupla reserva. Este arquivo acrescenta o que o serviço
-- em si precisa para deixar de usar o MongoDB — e nada além disso.
--
-- A partir daqui o schema do rental-core tem UM dono: estes arquivos. As
-- migrações do EF Core que o MotoHub carregava foram absorvidas aqui em vez de
-- reimplementadas -- cada garantia que elas fecharam está marcada abaixo pelo
-- nome da migração que a introduziu, para que a perda de uma seja visível.
--
-- Mesmo subconjunto portátil de 001: o CI aplica os dois arquivos no
-- CockroachDB e no PostgreSQL.

-- AddMotorcycleRetirement. A aposentadoria é o par (quando, por quê): sem o
-- motivo, uma moto some do catálogo sem que ninguém possa dizer se foi decisão
-- administrativa ou consequência de uma migração de dados legados.
ALTER TABLE motorcycles ADD COLUMN IF NOT EXISTS retirement_reason TEXT;

-- AddMotorcyclePaginationIndex. A paginação por cursor varre a lista ativa em
-- ordem de id; sem este índice a varredura é a tabela inteira a cada página.
CREATE INDEX IF NOT EXISTS motorcycles_active_page
    ON motorcycles (id)
    WHERE retired_at IS NULL;

-- O nome do piloto é uma cópia, não uma referência: o rental-core não é dono
-- do piloto, e o evento rental.closed precisa carregar o nome que valia quando
-- o aluguel foi criado. Buscá-lo na hora de fechar traria de volta a chamada
-- de rede que a projeção de #133 existe para eliminar.
ALTER TABLE rentals ADD COLUMN IF NOT EXISTS rider_name TEXT;

-- A liquidação produz um número e uma frase. A frase é o que o cliente lê.
ALTER TABLE rentals ADD COLUMN IF NOT EXISTS status_message TEXT NOT NULL DEFAULT '';
ALTER TABLE rentals ADD COLUMN IF NOT EXISTS additional_costs DECIMAL(12, 2) NOT NULL DEFAULT 0;

-- Um aluguel devolvido continua bloqueando a moto até a data prevista, porque
-- é assim que a agenda funciona. A consulta de sobreposição filtra por moto,
-- situação e as duas pontas do período.
CREATE INDEX IF NOT EXISTS rentals_motorcycle_schedule
    ON rentals (motorcycle_id, status, starts_at, predicted_ends_at);

-- Inbox: 001 registra o que já foi tratado. Falta o meio-termo -- a mensagem
-- que ESTÁ sendo tratada agora, por uma réplica que pode morrer no meio. Sem a
-- reserva com prazo, duas réplicas processam a mesma mensagem em paralelo e o
-- efeito "exatamente uma vez" do ADR 0009 vira "pelo menos uma vez".
ALTER TABLE inbox ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'completed';
ALTER TABLE inbox ADD COLUMN IF NOT EXISTS claim_token TEXT;
ALTER TABLE inbox ADD COLUMN IF NOT EXISTS claimed_until TIMESTAMPTZ;

-- Projeção do piloto (#133): o estado que permite decidir sobre um aluguel sem
-- perguntar ao serviço de identidade. verified_at_ms é o critério de quem
-- vence: o fato mais novo ganha por carimbo próprio, não por ordem de chegada.
CREATE TABLE IF NOT EXISTS rider_projection (
    rider_id       TEXT PRIMARY KEY,
    verified       BOOLEAN NOT NULL,
    verified_at_ms BIGINT NOT NULL,
    rider_name     TEXT
);

-- Snapshots das projeções de risco e preço, para que uma réplica que reinicia
-- não volte cobrando a tabela conservadora empacotada no binário.
CREATE TABLE IF NOT EXISTS projection_snapshots (
    id       TEXT PRIMARY KEY,
    topic    TEXT NOT NULL,
    payload  BYTEA NOT NULL,
    at_ms    BIGINT NOT NULL
);

-- AddTransactionalOutbox e ClaimOutboxMessages.
--
-- Este é o outbox do RabbitMQ, e é uma tabela diferente do `outbox` de 001,
-- que é o do Kafka. Os dois existem de propósito: um evento de domínio vai
-- para o Kafka, uma atualização de placa vai para a fila que o media-guard
-- escuta. O que os dois têm em comum é o que importa -- a linha é gravada na
-- mesma transação da mudança de negócio.
--
-- Os identificadores são citados em PascalCase porque este é o mesmo formato
-- que o AuthGate usa, e o relay compartilhado (Shared/Messaging/OutboxRelay.cs)
-- traz a consulta de reserva escrita à mão contra esses nomes. Alinhar os dois
-- serviços vale mais do que a coerência de estilo dentro deste arquivo.
CREATE TABLE IF NOT EXISTS "OutboxMessages" (
    "Id"                UUID PRIMARY KEY,
    "AggregateType"     VARCHAR(100) NOT NULL,
    "AggregateId"       VARCHAR(200) NOT NULL,
    "AggregateSequence" BIGINT NOT NULL DEFAULT 0,
    "EventType"         VARCHAR(200) NOT NULL,
    "Destination"       VARCHAR(200) NOT NULL,
    "Payload"           TEXT NOT NULL,
    "TraceParent"       VARCHAR(55),
    "TraceState"        VARCHAR(512),
    "OccurredAtUtc"     TIMESTAMPTZ NOT NULL,
    "PublishedAtUtc"    TIMESTAMPTZ,
    "PublishAttempts"   INT NOT NULL DEFAULT 0,
    "NextAttemptAtUtc"  TIMESTAMPTZ,
    "ClaimToken"        UUID,
    "ClaimedUntilUtc"   TIMESTAMPTZ,
    "LastError"         VARCHAR(2000)
);

-- A ordem por agregado é o que o relay preserva: dois eventos da mesma moto
-- não podem sair trocados. Este índice é o que torna essa busca barata.
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_AggregateType_AggregateId_AggregateSequence"
    ON "OutboxMessages" ("AggregateType", "AggregateId", "AggregateSequence");

CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_PendingClaim"
    ON "OutboxMessages" ("PublishedAtUtc", "ClaimedUntilUtc", "NextAttemptAtUtc", "OccurredAtUtc");

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE rider_projection, projection_snapshots TO rental_core;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "OutboxMessages" TO rental_core;
