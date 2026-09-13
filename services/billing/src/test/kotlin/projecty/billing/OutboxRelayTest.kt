package projecty.billing

import org.junit.jupiter.api.Tag
import java.time.OffsetDateTime
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * A relay das notas, contra o outbox de verdade.
 *
 * O Kafka é trocado por um sender que pode ser desligado, atrasado ou morto no
 * meio do envio. O que estes testes provam é a metade do contrato que mora no
 * banco: o que é reivindicado, o que é marcado, e o que sobrevive a uma falha.
 *
 * Cada teste usa um `aggregate_type` próprio, e é isso que o deixa dividir a
 * tabela com as notas que os outros testes deixam pendentes.
 */
class OutboxRelayTest {
    private val dataSource = TestDatabase.dataSource

    /** Sem a reivindicação, as duas selecionam o mesmo lote e toda linha sai duas vezes. */
    @Test
    @Tag("ADR-0009#leased-outbox-relay")
    fun `duas relays publicam cada linha uma vez`() {
        val aggregate = isolated()
        val start = OffsetDateTime.now().minusMinutes(5)
        repeat(250) { insert(aggregate, UUID.randomUUID().toString(), start.plusNanos(it * 1_000_000L), it) }

        val sender = FakeSender(delayMs = 20)
        drainWithTwo(aggregate, sender)

        assertEquals(250, sender.published.size)
        assertEquals(
            250,
            sender.published
                .map { it.id }
                .toSet()
                .size,
        )
        assertEquals(0, pending(aggregate))
    }

    @Test
    @Tag("ADR-0009#leased-outbox-relay")
    fun `os eventos de um agregado saem em ordem mesmo com duas relays`() {
        val aggregate = isolated()
        val rental = UUID.randomUUID().toString()
        val start = OffsetDateTime.now().minusMinutes(5)
        repeat(4) { insert(aggregate, rental, start.plusSeconds(it.toLong()), it) }

        val sender = FakeSender(delayMs = 10)
        drainWithTwo(aggregate, sender)

        assertEquals(listOf(0, 1, 2, 3), sender.published.map { it.payload.single().toInt() })
    }

    /**
     * O processo morre com o envio em voo. Nada é marcado, nada é devolvido, e
     * a reivindicação fica na linha até o lease vencer.
     */
    @Test
    @Tag("ADR-0009#leased-outbox-relay")
    fun `uma relay morta no meio do envio nao prende as linhas`() {
        val aggregate = isolated()
        insert(aggregate, UUID.randomUUID().toString(), OffsetDateTime.now().minusMinutes(1), 0)

        val killed = OutboxDispatcher(dataSource, aggregate, lease = java.time.Duration.ofSeconds(1))
        assertFailsWith<InterruptedException> { killed.dispatchOnce { throw InterruptedException("killed") } }
        assertEquals(1, claimed(aggregate))

        val survivor = FakeSender()
        val next = OutboxDispatcher(dataSource, aggregate)
        assertEquals(0, next.dispatchOnce(survivor).claimed)

        Thread.sleep(1_500)
        assertEquals(1, next.dispatchOnce(survivor).published)
        assertEquals(0, pending(aggregate))
    }

    /** O broker confirma parte do lote. O confirmado fica marcado; o resto volta sem esperar o lease. */
    @Test
    @Tag("ADR-0009#leased-outbox-relay")
    fun `uma recusa do broker devolve so o que ele recusou`() {
        val aggregate = isolated()
        val start = OffsetDateTime.now().minusMinutes(1)
        repeat(4) { insert(aggregate, UUID.randomUUID().toString(), start.plusNanos(it * 1_000_000L), it) }

        val pass = OutboxDispatcher(dataSource, aggregate).dispatchOnce(FakeSender(refuse = 2))

        assertEquals(4, pass.claimed)
        assertEquals(2, pass.published)
        assertNotNull(pass.failure)
        assertEquals(2, pending(aggregate))
        assertEquals(0, claimed(aggregate))
        assertEquals(2, OutboxDispatcher(dataSource, aggregate).dispatchOnce(FakeSender()).published)
    }

    /**
     * O piso de vazão do ADR 0009, como teste.
     *
     * O sender não custa nada, então o que se mede é o banco: uma reivindicação
     * e uma marcação por lote, não duas idas e voltas por linha.
     */
    @Test
    @Tag("ADR-0009#leased-outbox-relay")
    fun `duas relays drenam no piso declarado`() {
        val aggregate = isolated()
        val events = 5_000
        insertMany(aggregate, events)
        // O que a estatística automática faz depois de uma rajada. Sem ela o
        // otimizador espera um outbox vazio e compara cada linha pendente com
        // todas as outras -- uma propriedade de uma tabela com segundos de vida,
        // não da reivindicação.
        dataSource.connection.use { connection -> connection.createStatement().use { it.execute("ANALYZE outbox") } }

        val sender = FakeSender()
        val started = System.nanoTime()
        drainWithTwo(aggregate, sender)
        val seconds = (System.nanoTime() - started) / 1e9

        assertEquals(events, sender.published.size)
        val rate = events / seconds
        println("$events events in ${"%.2f".format(seconds)} s: ${rate.toInt()} events/s")
        assertTrue(
            rate >= MINIMUM_DRAIN_RATE,
            "drained at ${rate.toInt()} events/s, below the floor of $MINIMUM_DRAIN_RATE",
        )
    }

    // ------------------------------------------------------------------ apoio

    private fun isolated() = "invoice-test-" + UUID.randomUUID()

    /** Até as duas acharem o outbox vazio três vezes seguidas: uma vez só pode ser a outra segurando o resto. */
    private fun drainWithTwo(
        aggregate: String,
        sender: OutboxSender,
    ) {
        val failures = ConcurrentLinkedQueue<Throwable>()
        List(2) {
            thread {
                try {
                    val dispatcher = OutboxDispatcher(dataSource, aggregate)
                    var idle = 0
                    while (idle < 3) {
                        val pass = dispatcher.dispatchOnce(sender)
                        pass.failure?.let { throw it }
                        if (pass.claimed == 0) {
                            idle++
                            Thread.sleep(20)
                        } else {
                            idle = 0
                        }
                    }
                } catch (error: Throwable) {
                    failures.add(error)
                }
            }
        }.forEach { it.join() }
        failures.peek()?.let { throw it }
    }

    private class FakeSender(
        private val delayMs: Long = 0,
        private val refuse: Int = 0,
    ) : OutboxSender {
        private val queue = ConcurrentLinkedQueue<OutboxRow>()
        val published: List<OutboxRow> get() = queue.toList()

        override fun send(batch: List<OutboxRow>): List<Throwable?> {
            if (delayMs > 0) Thread.sleep(delayMs)
            return batch.mapIndexed { index, row ->
                if (index >= batch.size - refuse) {
                    IllegalStateException("broker refused")
                } else {
                    queue.add(row)
                    null
                }
            }
        }
    }

    private fun insert(
        aggregateType: String,
        aggregateId: String,
        occurredAt: OffsetDateTime,
        marker: Int,
    ) = dataSource.connection.use { connection ->
        connection
            .prepareStatement(
                """
                INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, occurred_at)
                VALUES (?, ?, 'invoice.issued', 'invoice.issued', ?, ?)
                """.trimIndent(),
            ).use { statement ->
                statement.setString(1, aggregateType)
                statement.setString(2, aggregateId)
                statement.setBytes(3, byteArrayOf(marker.toByte()))
                statement.setObject(4, occurredAt)
                statement.executeUpdate()
            }
    }

    private fun insertMany(
        aggregateType: String,
        count: Int,
    ) = dataSource.connection.use { connection ->
        connection
            .prepareStatement(
                """
                INSERT INTO outbox (aggregate_type, aggregate_id, event_type, topic, payload, occurred_at)
                SELECT ?, gen_random_uuid()::TEXT, 'invoice.issued', 'invoice.issued', decode('01', 'hex'),
                       now() - INTERVAL '5 minutes' + (n * INTERVAL '1 millisecond')
                  FROM generate_series(1, ?) AS n
                """.trimIndent(),
            ).use { statement ->
                statement.setString(1, aggregateType)
                statement.setInt(2, count)
                statement.executeUpdate()
            }
    }

    private fun pending(aggregateType: String) =
        count("SELECT count(*) FROM outbox WHERE aggregate_type = ? AND published_at IS NULL", aggregateType)

    private fun claimed(aggregateType: String) =
        count("SELECT count(*) FROM outbox WHERE aggregate_type = ? AND claim_token IS NOT NULL", aggregateType)

    private fun count(
        sql: String,
        argument: String,
    ): Int =
        dataSource.connection.use { connection ->
            connection.prepareStatement(sql).use { statement ->
                statement.setString(1, argument)
                statement.executeQuery().use { rows -> if (rows.next()) rows.getInt(1) else 0 }
            }
        }

    private companion object {
        /** O piso declarado no ADR 0009 para duas relays contra um banco de um nó. */
        const val MINIMUM_DRAIN_RATE = 500
    }
}
