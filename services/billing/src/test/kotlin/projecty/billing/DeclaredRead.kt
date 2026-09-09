package projecty.billing

import com.fasterxml.jackson.databind.ObjectMapper
import java.io.File

/**
 * A declaração do consumidor, lida do arquivo em vez de repetida aqui.
 *
 * O #138 pede que remover um endpoint de lote deixe um teste vermelho antes de
 * deixar uma tela em branco. Isso só vale se o teste souber o que a tela pede,
 * e a única forma de saber é ler a mesma declaração que o console lê --
 * `contracts/reads.json`. Repetir os nomes de campo aqui provaria apenas que
 * este arquivo concorda consigo mesmo.
 */
data class DeclaredRead(
    val method: String,
    val path: String,
    val idsParameter: String?,
    val maxBatch: Int?,
    val fields: List<String>,
) {
    companion object {
        fun of(
            provider: String,
            path: String,
        ): DeclaredRead {
            val document = ObjectMapper().readTree(locate())
            for (read in document["reads"]) {
                if (read["provider"].asText() == provider && read["path"].asText() == path) {
                    return DeclaredRead(
                        method = read["method"].asText(),
                        path = read["path"].asText(),
                        idsParameter = read["idsParameter"]?.asText(),
                        maxBatch = read["maxBatch"]?.asInt(),
                        fields = read["fields"].map { it.asText() },
                    )
                }
            }
            error(
                "contracts/reads.json declares no $provider read at $path. " +
                    "If the console stopped asking for it, delete the endpoint; " +
                    "if it still asks, restore the declaration.",
            )
        }

        /**
         * Sobe até achar o arquivo. O diretório de trabalho de um teste Gradle é
         * o do módulo, e um caminho relativo fixo quebra ao mover o módulo.
         */
        private fun locate(): File {
            var directory: File? = File(".").absoluteFile
            while (directory != null) {
                val candidate = File(directory, "contracts/reads.json")
                if (candidate.isFile) return candidate
                directory = directory.parentFile
            }
            error("contracts/reads.json was not found above ${File(".").absolutePath}")
        }
    }
}
