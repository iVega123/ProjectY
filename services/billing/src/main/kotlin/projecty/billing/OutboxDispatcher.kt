package projecty.billing

import java.time.Duration
import java.time.OffsetDateTime
import java.util.UUID
import javax.sql.DataSource

/** Uma linha do outbox a caminho do Kafka. */
class OutboxRow(
    val id: UUID,
    val key: String,
    val topic: String,
    val payload: ByteArray,
    val traceParent: String?,
)

/**
 * Entrega um lote de uma vez. Devolve uma falha por linha, na mesma ordem:
 * null é o broker ter confirmado aquela linha.
 */
fun interface OutboxSender {
    fun send(batch: List<OutboxRow>): List<Throwable?>
}

/** O que uma passada fez. [failure] é a primeira recusa do broker, se houve. */
data class RelayPass(
    val claimed: Int,
    val published: Int,
    val failure: Throwable?,
)

/**
 * Reivindica, envia e marca as linhas de um `aggregate_type` do outbox.
 *
 * Três garantias, as mesmas das relays do rental-core e do identity, cada uma
 * com um teste em OutboxRelayTest (ADR 0009):
 *
 * - Duas réplicas publicam cada linha uma vez. A linha é reivindicada antes do
 *   envio, num UPDATE sobre `FOR UPDATE SKIP LOCKED`: a outra réplica pula o
 *   que esta segura em vez de esperar ou enviar de novo.
 * - Os eventos de um agregado saem em ordem. Uma linha só é reivindicável
 *   quando nenhuma linha anterior do mesmo agregado está pendente, reivindicada
 *   ou não. Por isso um lote tem no máximo a cabeça de cada agregado, e pode ir
 *   inteiro ao broker de uma vez sem trocar a ordem de nada.
 * - Uma relay que morre no meio do envio não prende as linhas. A reivindicação
 *   é um lease; vencido, a próxima passada as leva.
 *
 * O lease é maior que o envio mais longo que o produtor permite
 * (`delivery.timeout.ms`) somado ao registry: vencer com a linha ainda em voo
 * a entregaria a outra réplica, e ela sairia duas vezes.
 */
class OutboxDispatcher(
    private val dataSource: DataSource,
    private val aggregateType: String,
    private val lease: Duration = CLAIM_LEASE,
) {
    /**
     * Marcar como publicado vem DEPOIS do envio. Cair entre os dois republica o
     * mesmo evento com o mesmo `event_id`, que é o que o inbox de quem consumir
     * reconhece. Marcar antes perderia o evento em silêncio.
     */
    fun dispatchOnce(sender: OutboxSender): RelayPass {
        val token = UUID.randomUUID()
        val batch = claim(token)
        if (batch.isEmpty()) return RelayPass(0, 0, null)

        val results =
            try {
                sender.send(batch)
            } catch (stopped: InterruptedException) {
                // Parada no meio do envio: nada é marcado nem devolvido, e as
                // linhas esperam o lease como esperariam se o processo morresse.
                throw stopped
            } catch (error: Exception) {
                release(token)
                throw error
            }

        val failures = batch.indices.map { results.getOrElse(it) { IllegalStateException("No result for this row") } }
        val sent = batch.filterIndexed { index, _ -> failures[index] == null }.map { it.id }
        mark(token, sent)
        val failure = failures.firstOrNull { it != null }
        // Devolvida, e não presa ao lease: a próxima passada tenta de novo já.
        if (failure != null) release(token)
        return RelayPass(batch.size, sent.size, failure)
    }

    private fun claim(token: UUID): List<OutboxRow> =
        dataSource.connection
            .use { connection ->
                connection.prepareStatement(CLAIM).use { statement ->
                    statement.setString(1, aggregateType)
                    statement.setInt(2, BATCH_SIZE)
                    statement.setObject(3, token)
                    statement.setString(4, "${lease.toMillis()} milliseconds")
                    statement.executeQuery().use { rows ->
                        buildList {
                            while (rows.next()) {
                                val row =
                                    OutboxRow(
                                        rows.getObject(1, UUID::class.java),
                                        rows.getString(2),
                                        rows.getString(3),
                                        rows.getBytes(4),
                                        rows.getString(5),
                                    )
                                add(rows.getObject(6, OffsetDateTime::class.java) to row)
                            }
                        }
                    }
                }
            }
            // RETURNING não promete a ordem da CTE; a ordem de envio volta aqui.
            .sortedBy { it.first }
            .map { it.second }

    /**
     * O lote numa instrução só. A condição sobre `claim_token` é o que impede
     * uma relay que perdeu o lease de marcar a linha que outra tomou.
     */
    private fun mark(
        token: UUID,
        ids: List<UUID>,
    ) {
        if (ids.isEmpty()) return
        dataSource.connection.use { connection ->
            connection
                .prepareStatement(
                    """
                    UPDATE outbox
                       SET published_at = now(), claim_token = NULL, claimed_until = NULL
                     WHERE claim_token = ? AND published_at IS NULL AND id = ANY(?)
                    """.trimIndent(),
                ).use { statement ->
                    statement.setObject(1, token)
                    statement.setArray(2, connection.createArrayOf("uuid", ids.toTypedArray()))
                    statement.executeUpdate()
                }
        }
    }

    private fun release(token: UUID) =
        dataSource.connection.use { connection ->
            connection
                .prepareStatement(
                    "UPDATE outbox SET claim_token = NULL, claimed_until = NULL WHERE claim_token = ? AND published_at IS NULL",
                ).use { statement ->
                    statement.setObject(1, token)
                    statement.executeUpdate()
                }
        }

    companion object {
        const val BATCH_SIZE = 100
        val CLAIM_LEASE: Duration = Duration.ofSeconds(30)

        // O filtro por aggregate_type é o que permite dividir a tabela com o
        // rental-core e o identity sem que uma relay publique os eventos da outra.
        private val CLAIM =
            """
            WITH candidates AS (
                SELECT candidate.id
                  FROM outbox AS candidate
                 WHERE candidate.published_at IS NULL
                   AND candidate.aggregate_type = ?
                   AND (candidate.claimed_until IS NULL OR candidate.claimed_until < now())
                   AND NOT EXISTS (
                        SELECT 1 FROM outbox AS earlier
                         WHERE earlier.aggregate_type = candidate.aggregate_type
                           AND earlier.aggregate_id = candidate.aggregate_id
                           AND earlier.published_at IS NULL
                           AND earlier.occurred_at < candidate.occurred_at)
                 ORDER BY candidate.occurred_at
                 LIMIT ?
                 FOR UPDATE SKIP LOCKED)
            UPDATE outbox
               SET claim_token = ?, claimed_until = now() + ?::INTERVAL
              FROM candidates
             WHERE outbox.id = candidates.id
            RETURNING outbox.id, outbox.aggregate_id, outbox.topic, outbox.payload,
                      outbox.trace_parent, outbox.occurred_at
            """.trimIndent()
    }
}
