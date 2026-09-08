package projecty.billing

import org.apache.kafka.clients.consumer.ConsumerRecord
import org.apache.kafka.clients.consumer.MockConsumer
import org.apache.kafka.clients.consumer.OffsetResetStrategy
import org.apache.kafka.common.TopicPartition
import org.junit.jupiter.api.Tag
import java.sql.SQLException
import java.time.Duration
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * O laço do consumidor, e as duas falhas que ele não pode confundir.
 *
 * Uma mensagem estragada e um banco fora do ar chegam ao mesmo `catch` se
 * ninguém os separar, e aí só sobram escolhas ruins: ou se descarta a mensagem
 * boa quando o banco pisca -- e a fatura some --, ou se insiste na estragada
 * para sempre -- e a partição inteira para de andar.
 */
class RentalClosedConsumerTest {
    private val partition = TopicPartition(RentalClosedConsumer.TOPIC, 0)

    private class Observed(val position: Long, val committed: Long?)

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `um registro indecifravel e pulado, e o lote segue e confirma`() {
        val issuer = RecordingIssuer()
        val result =
            run(
                issuer,
                // Campo 31 com wire type 7, que não existe: o parser do Protobuf
                // recusa estes bytes em vez de os aceitar como campo desconhecido.
                record(0, byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte())),
                record(1, payload()),
            )

        assertNull(result.escaped, "um registro envenenado não pode derrubar a thread")
        assertEquals(1, issuer.calls.get(), "a mensagem boa do mesmo lote precisa ser faturada")
        assertEquals(2L, result.observed?.committed, "a partição precisa andar por cima do registro pulado")
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `uma falha de banco nao confirma o offset nem mata a thread`() {
        val issuer = FailingIssuer(SQLException("connection refused"))

        val result = run(issuer, record(0, payload()))

        // A thread sai pelo `stopping`, não por exceção: uma queda aqui deixaria
        // de faturar até alguém reiniciar o processo, e /health/live continuaria
        // respondendo 200 enquanto isso.
        assertNull(result.escaped, "uma falha transitória não pode escapar do laço")
        // Nada confirmado: a mensagem continua devida, e é isso que faz a fatura
        // existir quando o banco voltar.
        assertNull(result.observed?.committed)
        assertTrue(issuer.calls.get() >= 1)
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `o lote que falhou volta ao ultimo offset confirmado`() {
        val result = run(FailingIssuer(SQLException("connection refused")), record(0, payload()))

        // Sem o seek, a posição ficaria em 1: o Kafka não rebobina sozinho, e a
        // mensagem só voltaria num rebalanceamento -- que numa réplica só pode
        // nunca acontecer.
        assertEquals(0L, result.observed?.position)
    }

    // ------------------------------------------------------------------ apoio

    private class LoopResult(val escaped: Throwable?, val observed: Observed?)

    /**
     * Roda o laço contra um lote e observa o consumidor antes de ele fechar.
     *
     * A observação acontece dentro de uma tarefa de poll porque o laço fecha o
     * consumidor ao sair, e um MockConsumer fechado recusa `position` e
     * `committed`.
     */
    private fun run(
        issuer: InvoiceIssuer,
        vararg records: ConsumerRecord<String, ByteArray>,
    ): LoopResult {
        val stopping = AtomicBoolean(false)
        var observed: Observed? = null
        val consumer =
            MockConsumer<String, ByteArray>(OffsetResetStrategy.EARLIEST).apply {
                updateBeginningOffsets(mapOf(partition to 0L))
                schedulePollTask {
                    rebalance(listOf(partition))
                    records.forEach { addRecord(it) }
                }
                schedulePollTask {
                    observed = Observed(position(partition), committed(setOf(partition))[partition]?.offset())
                    stopping.set(true)
                }
            }

        var escaped: Throwable? = null
        val thread = Thread(RentalClosedConsumer(issuer, stopping, consumer, Duration.ofMillis(10)), "test-consumer")
        thread.setUncaughtExceptionHandler { _, error -> escaped = error }
        thread.start()
        thread.join(20_000)
        assertTrue(!thread.isAlive, "o laço precisa terminar quando observa stopping")
        return LoopResult(escaped, observed)
    }

    private fun record(
        offset: Long,
        value: ByteArray,
    ) = ConsumerRecord(RentalClosedConsumer.TOPIC, 0, offset, "key-$offset", value)

    private fun payload(): ByteArray {
        val rentalId = UUID.randomUUID().toString()
        val start = 1_767_225_600_000L
        return project_y.events.Rental.RentalEvent
            .newBuilder()
            .setEventId("$rentalId:rental.closed:v1")
            .setRentalId(rentalId)
            .setRiderId("rider-1")
            .setCurrency("BRL")
            .setAgreedTotalMinor(21_000L)
            .setPlanDays(7)
            .setStartedAtMs(start)
            .setPredictedEndAtMs(start + 7 * 86_400_000L)
            .setEndedAtMs(start + 4 * 86_400_000L)
            .build()
            .toByteArray()
    }

    private open class RecordingIssuer : InvoiceIssuer {
        val calls = AtomicInteger(0)

        override fun issue(
            messageId: String,
            rental: ClosedRental,
            settled: Settlement.Settled,
        ): Invoices.Issued {
            calls.incrementAndGet()
            return Invoices.Issued(Outcome.ISSUED, UUID.randomUUID())
        }
    }

    private class FailingIssuer(private val error: SQLException) : RecordingIssuer() {
        override fun issue(
            messageId: String,
            rental: ClosedRental,
            settled: Settlement.Settled,
        ): Invoices.Issued {
            calls.incrementAndGet()
            throw error
        }
    }
}
