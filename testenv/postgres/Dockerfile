# The plain `postgres` profile: PostgreSQL 17 with the pgvector extension, for the knowledge index's vector search (plan 27 section 2.4).
#
# A build step on the stock Debian image for the same reason as postgres-logical: the official image already has the PGDG apt repository
# configured and PGDG publishes pgvector as a package, so this is one `apt-get install` and the server stays the Debian build that Build's
# conformance suite pins differences to. The extension is installed, not created: a suite (or the knowledge module's migration) runs
# `CREATE EXTENSION vector` in the database it uses.
FROM postgres:17

RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-17-pgvector \
    && rm -rf /var/lib/apt/lists/*
