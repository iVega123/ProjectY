# Documentação do Desafio Backend - Sistema de Gestão de Motos e Entregadores

## Introdução

Este documento detalha a implementação do sistema de gestão de aluguel de motos e entregadores, desenvolvido como parte de um desafio de backend. O sistema é dividido em quatro microserviços: Auth Gate, MotoHub, Rider Manager e Rental Operations, cada um rodando em sua própria instância Docker e comunicando-se através de uma rede definida.

## Arquitetura de Microserviços

### Auth Gate
- **Função**: Responsável pela autenticação dos usuários e geração de tokens de acesso. Gerencia dois tipos de usuários: Admin e Rider.
- **Tecnologia**: ASP.NET Core.
- **Portas**:
  - `8080`: HTTP API para autenticação.
  - `8181`: HTTPS API para autenticação segura.
- **Banco de Dados**:
  - Utiliza Postgres para armazenar dados de usuários.
- **Dependências**:
  - Comunica com todos os outros microserviços para validar tokens de acesso.
  - **Swagger URL**: [Auth Gate Swagger](http://localhost:8080/swagger)

### MotoHub
- **Função**: Administração das motos na plataforma, incluindo cadastro, consulta, modificação e remoção de motos.
- **Tecnologia**: ASP.NET Core.
- **Portas**:
  - `8100`: HTTP API para gestão de motos.
  - `8101`: HTTPS API para gestão de motos segura.
- **Banco de Dados**:
  - Utiliza Postgres para persistência de dados.
- **Dependências**:
  - RabbitMQ para comunicação de eventos relacionados a motos.
  - **Swagger URL**: [MotoHub Swagger](http://localhost:8100/swagger)

### Rider Manager
- **Função**: Focado no cadastro e gerenciamento dos entregadores que irão alugar motos.
- **Tecnologia**: ASP.NET Core.
- **Portas**:
  - `8000`: HTTP API para gestão de entregadores.
  - `8001`: HTTPS API para gestão de entregadores segura.
- **Banco de Dados**:
  - Utiliza Postgres para armazenar dados dos entregadores.
- **Dependências**:
  - RabbitMQ para comunicação de eventos relacionados aos entregadores.
  - MinIO para armazenamento das fotos de CNH.
  - **Swagger URL**: [Rider Manager Swagger](http://localhost:8000/swagger)

### Rental Operations
- **Função**: Gerenciamento dos aluguéis de motos, incluindo início, término e cálculo de custos associados.
- **Tecnologia**: ASP.NET Core.
- **Portas**:
  - `8200`: HTTP API para operações de aluguel.
  - `8201`: HTTPS API para operações de aluguel segura.
- **Banco de Dados**:
  - Utiliza MongoDB para armazenamento dos registros de aluguéis.
- **Dependências**:
  - RabbitMQ para comunicação de eventos de aluguéis.
  - **Swagger URL**: [Rental Operations Swagger](http://localhost:8200/swagger)

### Elastic Stack (Elasticsearch, Logstash, Kibana)
- **Função**: Utilizado para monitoramento, análise de logs e visualizações em tempo real dos dados gerados pelos microserviços.
- **Componentes**:
  - **Elasticsearch**: Armazenamento e indexação de logs.
  - **Logstash**: Agregação e processamento de logs antes de enviar para o Elasticsearch.
  - **Kibana**: Interface gráfica para visualizar dados do Elasticsearch.

## Docker Compose

Todos os microserviços são configurados e gerenciados através de Docker Compose, assegurando isolamento, facilidade de configuração e deploy. O arquivo `docker-compose.yml` inclui a definição de redes, volumes e dependências necessárias para cada serviço.

## Uso

Para executar o sistema:
1. Clone o repositório contendo o código fonte e o Docker Compose.
2. Navegue até a pasta raiz do projeto.
3. Execute `docker-compose up` para iniciar todos os serviços.
4. Acesse as APIs expostas pelos microserviços nas portas designadas.

## Testes e Qualidade

- **Testes Unitários e de Integração**: Cada microserviço possui sua suite de testes para garantir a integridade das operações.
- **Design Patterns**: Utilizados para garantir um código limpo e organizado.
- **Logs**: Implementados em todos os microserviços para facilitar o diagnóstico e monitoramento.

## Conclusão

Este sistema foi desenvolvido seguindo as melhores práticas de desenvolvimento e arquitetura de microserviços, oferecendo uma solução robusta e escalável para o gerenciamento de aluguéis de motos e entregadores.
