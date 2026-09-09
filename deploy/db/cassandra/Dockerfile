FROM cassandra:5.0
COPY 001_init.cql /schema/001_init.cql
USER 999:999
ENTRYPOINT ["cqlsh", "cassandra", "-f", "/schema/001_init.cql"]
