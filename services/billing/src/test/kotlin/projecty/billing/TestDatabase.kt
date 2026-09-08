package projecty.billing

import com.zaxxer.hikari.HikariConfig
import com.zaxxer.hikari.HikariDataSource
import org.testcontainers.containers.GenericContainer
import org.testcontainers.utility.DockerImageName
import java.nio.file.Files
import java.nio.file.Path
import java.sql.DriverManager
import java.sql.SQLException

/**
 * Um CockroachDB por JVM, com o schema de `deploy/db/sql` aplicado.
 *
 * CockroachDB e não PostgreSQL porque as garantias que estes testes verificam
 * dependem do nível de isolamento: SERIALIZABLE é o padrão lá. Em READ
 * COMMITTED o teste de exatamente-uma-vez passaria por sorte.
 *
 * O container é compartilhado entre as classes de teste de propósito: subir um
 * banco por classe triplicaria o tempo do CI para provar a mesma coisa.
 */
object TestDatabase {
    private var container: GenericContainer<*>? = null

    val dataSource: HikariDataSource by lazy {
        // Um CockroachDB já de pé, quando houver, e um container quando não.
        //
        // O Testcontainers precisa falar com o daemon, e nem toda máquina de
        // desenvolvimento expõe o socket de dentro de um container -- o Docker
        // Desktop no Windows não expõe. O CI roda pelo caminho de baixo; a
        // variável existe para rodar os testes à mão sem ele.
        val endpoint = System.getenv("BILLING_TEST_COCKROACH") ?: startCockroach()
        val root = url(endpoint, "defaultdb")
        awaitReady(root)
        applyFile(root, "000_bootstrap.cockroach.sql")
        val app = url(endpoint, "projecty")
        for (file in SCHEMA) applyFile(app, file)

        HikariDataSource(
            HikariConfig().apply {
                jdbcUrl = app
                maximumPoolSize = 4
            },
        ).also {
            Runtime.getRuntime().addShutdownHook(
                Thread {
                    it.close()
                    container?.stop()
                },
            )
        }
    }

    private val SCHEMA =
        listOf(
            "001_schema.sql",
            "002_rental_core.sql",
            "003_billing.sql",
            "004_settlement_moves_to_billing.sql",
        )

    private fun startCockroach(): String {
        val started =
            GenericContainer(DockerImageName.parse("cockroachdb/cockroach:v26.3.1"))
                .withCommand("start-single-node", "--insecure")
                .withExposedPorts(26257)
        started.start()
        container = started
        return "${started.host}:${started.getMappedPort(26257)}"
    }

    private fun url(
        endpoint: String,
        database: String,
    ) = "jdbc:postgresql://$endpoint/$database?sslmode=disable&user=root"

    private fun awaitReady(url: String) {
        var last: Exception? = null
        repeat(60) {
            try {
                DriverManager.getConnection(url).use { connection ->
                    connection.createStatement().use { it.execute("SELECT 1") }
                }
                return
            } catch (error: SQLException) {
                last = error
                Thread.sleep(1_000)
            }
        }
        throw IllegalStateException("CockroachDB did not become ready", last)
    }

    private fun applyFile(
        url: String,
        file: String,
    ) {
        val sql = Files.readString(Path.of("../../deploy/db/sql", file))
        DriverManager.getConnection(url).use { connection ->
            connection.createStatement().use { it.execute(sql) }
        }
    }
}
