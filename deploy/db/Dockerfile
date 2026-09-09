FROM cockroachdb/cockroach:v26.3.1
COPY sql /sql
COPY run-schema.sh /run-schema.sh
USER 65532:65532
ENTRYPOINT ["/bin/bash", "/run-schema.sh"]
