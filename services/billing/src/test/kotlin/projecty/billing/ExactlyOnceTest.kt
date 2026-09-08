package projecty.billing

import org.junit.jupiter.api.Tag
import project_y.events.Invoice.InvoiceIssued
import java.sql.Connection
import java.sql.SQLException
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * O efeito "exatamente uma vez" do ADR 0009, contra o banco de verdade.
 *
 * Contra o CockroachDB e não contra o Postgres porque a garantia depende do
 * nível de isolamento: SERIALIZABLE é o padrão lá, e é o que faz duas réplicas
 * competindo pela mesma mensagem terminarem com uma nota, não duas. Em READ
 * COMMITTED este teste passaria por sorte.
 */
class ExactlyOnceTest {
    private val dataSource = TestDatabase.dataSource
    private val invoices = Invoices(dataSource)

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `a mesma mensagem duas vezes emite uma nota so`() {
        val rental = closed()

        val first = invoices.issue(rental.eventId, rental, settle(rental))
        val second = invoices.issue(rental.eventId, rental, settle(rental))

        assertEquals(Outcome.ISSUED, first.outcome)
        assertEquals(Outcome.DUPLICATE_MESSAGE, second.outcome)
        assertEquals(1, invoiceCount(rental.rentalId))
        // E um evento de saída só: publicar duas vezes o mesmo fato seria a
        // mesma duplicata, um passo adiante.
        assertEquals(1, outboxCount(rental.rentalId))
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `mensagem nova para aluguel ja faturado nao emite a segunda nota`() {
        val rental = closed()
        invoices.issue(rental.eventId, rental, settle(rental))

        // O inbox guarda mensagens, não aluguéis: com um id novo ele deixa
        // passar. Quem recusa aqui é one_invoice_per_rental -- a razão de a
        // garantia não depender de uma tabela só.
        val replay = invoices.issue("replay:" + UUID.randomUUID(), rental, settle(rental))

        assertEquals(Outcome.ALREADY_SETTLED, replay.outcome)
        assertNull(replay.invoiceId)
        assertEquals(1, invoiceCount(rental.rentalId))
    }

    /**
     * A propriedade que o rental-core não consegue prometer.
     *
     * O SqlInboxProcessor dele reserva a mensagem, roda o handler fora da
     * transação e conclui depois. Aqui a nota e a linha do inbox estão no mesmo
     * COMMIT: se a nota não entra, a mensagem continua por tratar e a próxima
     * entrega tenta de novo. Não existe estado em que o inbox diga "já tratei"
     * e a nota não exista.
     */
    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `nota recusada pelo banco nao deixa a mensagem marcada como tratada`() {
        val rental = closed()
        val messageId = rental.eventId
        val impossible = settle(rental).copy(planDays = 0) // viola CHECK (plan_days > 0)

        val error = runCatching { invoices.issue(messageId, rental, impossible) }.exceptionOrNull()

        assertTrue(error is SQLException, "a violação do CHECK precisa chegar a quem chamou")
        assertEquals(0, inboxCount(messageId))
        assertEquals(0, invoiceCount(rental.rentalId))
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `o evento de saida carrega a nota e e particionado pelo aluguel`() {
        val rental = closed()
        val settled = settle(rental)

        val issued = invoices.issue(rental.eventId, rental, settled)

        val row = outboxRow(rental.rentalId)
        assertNotNull(row)
        assertEquals("invoice.issued", row.topic)
        assertEquals(rental.rentalId, row.key)
        val event = InvoiceIssued.parseFrom(row.payload)
        assertEquals(settled.totalMinor, event.totalMinor)
        assertEquals(issued.invoiceId.toString(), event.invoiceId)
        assertEquals(InvoiceEvents.eventId(rental.rentalId), event.eventId)
    }

    // ------------------------------------------------------------------ apoio

    private fun closed(): ClosedRental {
        val rentalId = UUID.randomUUID().toString()
        val start = 1_767_225_600_000L
        return ClosedRental(
            eventId = "$rentalId:rental.closed:v1",
            rentalId = rentalId,
            riderId = "rider-" + rentalId.take(8),
            currency = "BRL",
            agreedTotalMinor = 21_000L,
            planDays = 7,
            startedAtMs = start,
            predictedEndAtMs = start + 7 * 86_400_000L,
            endedAtMs = start + 4 * 86_400_000L,
            riderName = "Ada Lovelace",
            traceParent = null,
        )
    }

    private fun settle(rental: ClosedRental) =
        Settlement.settle(
            rental.agreedTotalMinor,
            rental.planDays,
            rental.startedAtMs,
            rental.predictedEndAtMs,
            rental.endedAtMs,
        )

    private class OutboxRow(val key: String, val topic: String, val payload: ByteArray)

    private fun outboxRow(rentalId: String): OutboxRow? =
        query { connection ->
            connection.prepareStatement(
                "SELECT aggregate_id, topic, payload FROM outbox WHERE aggregate_type = 'invoice' AND aggregate_id = ?",
            ).use { statement ->
                statement.setString(1, rentalId)
                statement.executeQuery().use { rows ->
                    if (rows.next()) OutboxRow(rows.getString(1), rows.getString(2), rows.getBytes(3)) else null
                }
            }
        }

    private fun invoiceCount(rentalId: String) =
        count("SELECT count(*) FROM invoices WHERE rental_id = ?::UUID", rentalId)

    private fun outboxCount(rentalId: String) =
        count("SELECT count(*) FROM outbox WHERE aggregate_type = 'invoice' AND aggregate_id = ?", rentalId)

    private fun inboxCount(messageId: String) =
        count("SELECT count(*) FROM inbox WHERE message_id = ? AND consumer = ?", messageId, Invoices.CONSUMER)

    private fun count(
        sql: String,
        vararg arguments: String,
    ): Int =
        query { connection ->
            connection.prepareStatement(sql).use { statement ->
                arguments.forEachIndexed { index, value -> statement.setString(index + 1, value) }
                statement.executeQuery().use { rows -> if (rows.next()) rows.getInt(1) else 0 }
            }
        }

    private fun <T> query(body: (Connection) -> T): T = dataSource.connection.use(body)
}
