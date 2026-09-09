#!/usr/bin/env bash
#
# Gera os tipos Go a partir de contracts/events.
#
# O código gerado é versionado, ao contrário do que fazem o billing e o
# rental-core, que geram no build. O motivo é o dev loop: gerar no build
# obrigaria quem roda `go test ./...` a ter protoc instalado, e a linguagem
# inteira parte do princípio de que `go test` funciona sozinho.
#
# O que impede a cópia de envelhecer é o CI, que roda este script e recusa o PR
# se o resultado diferir do que está versionado -- a mesma troca de "binário
# versionado por verificação em texto" que o repositório vem fazendo em todo
# lugar.
#
# A geração roda DENTRO do container para o cabeçalho do arquivo gerado, que
# carrega a versão do protoc, ser o mesmo na máquina de quem escreve e no CI.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# No Git Bash o daemon do Docker só entende caminho do Windows, e o próprio
# shell reescreve os caminhos que passam pela linha de comando. As duas linhas
# abaixo não fazem nada em Linux, que é onde o CI roda.
if WINDOWS_ROOT="$(cd "$ROOT" && pwd -W 2>/dev/null)"; then ROOT="$WINDOWS_ROOT"; fi
export MSYS_NO_PATHCONV=1

IMAGE="golang:1.27-alpine"
PROTOC_GEN_GO="v1.36.12"
PACKAGE="github.com/iVega123/ProjectY/services/identity/internal/events"

docker run --rm \
    --volume "$ROOT:/src" \
    --volume projecty-go-mod:/go/pkg/mod \
    --workdir /src \
    "$IMAGE" sh -eu -c "
        apk add --no-cache protobuf >/dev/null
        go install google.golang.org/protobuf/cmd/protoc-gen-go@$PROTOC_GEN_GO
        PATH=\$PATH:/root/go/bin protoc -I /src/contracts/events \
            --go_out=/src/services/identity/internal/events \
            --go_opt=paths=source_relative \
            --go_opt=Mrider.proto=$PACKAGE \
            --go_opt=Mrider_v2.proto=$PACKAGE \
            rider.proto rider_v2.proto
    "
