package projecty.billing

import java.sql.Connection
import java.sql.SQLException
import java.util.UUID
import javax.sql.DataSource

/** O que aconteceu com uma entrega de `rental.closed`. */
enum class Outcome {
    /** A nota foi emitida agora, e o evento de saída ficou no outbox. */
    ISSUED,

    /** Esta MENSAGEM já havia sido tratada. O inbox a reconheceu. */
    DUPLICATE_MESSAGE,

    /** Mensagem nova, aluguel já faturado. O índice único da nota a recusou. */
    ALREADY_SETTLED,
}

/**
 * A nota, a linha do inbox e o evento de saída numa transação só.
 *
 * É aqui que o ADR 0009 deixa de ser prosa. O `SqlInboxProcessor` do
 * rental-core reserva a mensagem, roda o handler FORA da transação da reserva e
 * a conclui depois -- o que deixa uma janela: cair entre o efeito e a conclusão
 * republica o efeito. Ele documenta essa janela e vive com ela porque o handler
 * dele escreve em outro lugar.
 *
 * O billing não tem essa desculpa: o efeito é uma linha no MESMO banco da linha
 * do inbox. Então as duas entram no mesmo COMMIT, e não existe instante em que
 * uma valha sem a outra. Nenhuma reserva com prazo, nenhum handler externo,
 * nenhuma janela -- o preço é que só serve para efeitos que moram no banco, que
 * é exatamente o caso de uma fatura.
 */
class Invoices(private val dataSource: DataSource) {
    companion object {
        const val CONSUMER = "billing-v1"
        private const val UNIQUE_VIOLATION = "23505"
        private const val SERIALIZATION_FAILURE = "40001"

        /**
         * O CockroachDB roda em SERIALIZABLE e devolve 40001 quando duas
         * transações não podem ser ordenadas. Repetir é obrigação do cliente:
         * sem isto, duas réplicas tratando mensagens vizinhas veriam uma delas
         * falhar por um motivo que não é erro nenhum.
         */
        fun <T> retrying(
            attempts: Int = 5,
            body: () -> T,
        ): T {
            var last: SQLException? = null
            repeat(attempts) {
                try {
                    return body()
                } catch (error: SQLException) {
                    if (error.sqlState != SERIALIZATION_FAILURE) throw error
                    last = error
                    Thread.sleep(20L * (it + 1))
                }
            }
            throw last!!
        }
    }

    data class Issued(val outcome: Outcome, val invoiceId: UUID?)

    fun issue(
        messageId: String,
        rental: ClosedRental,
        settled: Settlement.Settled,
    ): Issued =
        retrying {
            dataSource.connection.use { connection ->
                connection.autoCommit = false
                try {
                    if (!claimMessage(connection, messageId)) {
                        connection.rollback()
                        return@use Issued(Outcome.DUPLICATE_MESSAGE, null)
                    }
                    val invoiceId = UUID.randomUUID()
                    try {
                        insertInvoice(connection, invoiceId, rental, settled)
                    } catch (error: SQLException) {
                        if (error.sqlState != UNIQUE_VIOLATION) throw error
                        // Mensagem nova para um aluguel já faturado: um replay a
                        // partir do offset zero, ou uma republicação com id novo.
                        // O inbox não cobre isso -- ele guarda mensagens, não
                        // aluguéis -- e é para isso que one_invoice_per_rental
                        // existe. A mensagem fica registrada como tratada,
                        // porque o efeito que ela pedia já está no banco.
                        connection.rollback()
                        recordHandled(connection, messageId)
                        connection.commit()
                        return@use Issued(Outcome.ALREADY_SETTLED, null)
                    }
                    insertOutbox(connection, invoiceId, rental, settled)
                    connection.commit()
                    Issued(Outcome.ISSUED, invoiceId)
                } catch (error: Exception) {
                    connection.rollback()
                    throw error
                } finally {
                    connection.autoCommit = true
                }
            }
        }

    /** true quando esta réplica passou a ser a dona do tratamento da mensagem. */
    private fun claimMessage(
        connection: Connection,
        messageId: String,
    ): Boolean =
        connection.prepareStatement(
            """
            INSERT INTO inbox (message_id, consumer, status)
            VALUES (?, ?, 'completed')
            ON CONFLICT (message_id, consumer) DO NOTHING
            """.trimIndent(),
        ).use { statement ->
            statement.setString(1, messageId)
            statement.setString(2, CONSUMER)
            statement.executeUpdate() == 1
        }

    private fun recordHandled(
        connection: Connection,
        messageId: String,
    ) {
        claimMessage(connection, messageId)
    }

    private fun insertInvoice(
        connection: Connection,
        invoiceId: UUID,
        rental: ClosedRental,
        settled: Settlement.Settled,
    ) = connection.prepareStatement(
        """
        INSERT INTO invoices (id, rental_id, rider_id, currency, plan_days, days_used,
                              agreed_minor, adjustment_minor, total_minor, reason, rider_name)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """.trimIndent(),
    ).use { statement ->
        statement.setObject(1, invoiceId)
        statement.setObject(2, UUID.fromString(rental.rentalId))
        statement.setString(3, rental.riderId)
        statement.setString(4, rental.currency)
        statement.setInt(5, settled.planDays)
        statement.setInt(6, settled.daysUsed)
        statement.setLong(7, settled.agreedMinor)
        statement.setLong(8, settled.adjustmentMinor)
        statement.setLong(9, settled.totalMinor)
        statement.setString(10, settled.reason)
        statement.setString(11, rental.riderName)
        statement.executeUpdate()
    }

    private fun insertOutbox(
        connection: Connection,
        invoiceId: UUID,
        rental: ClosedRental,
        settled: Settlement.Settled,
    ) = connection.prepareStatement(
        """
        INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, trace_parent)
        VALUES ('invoice', ?, 'invoice.issued', 'invoice.issued', ?, ?)
        """.trimIndent(),
    ).use { statement ->
        // aggregate_id é a chave de partição no Kafka, e topics.json diz que
        // invoice.issued é particionado por rental_id. A relay não escolhe:
        // publica o que a transação deixou aqui.
        statement.setString(1, rental.rentalId)
        statement.setBytes(2, InvoiceEvents.issued(invoiceId, rental, settled).toByteArray())
        statement.setString(3, rental.traceParent)
        statement.executeUpdate()
    }
}
