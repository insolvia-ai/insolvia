"""PostgreSQL adapters (ADR 0030): psycopg 3, plain SQL, no ORM.

`connection` opens a connection, `migrations` applies the numbered `.sql`
files under `migrations/`, `queries` holds the two helpers every store needs
(a refused conditional write, a keyset cursor), and `testing` is the pytest
plugin that gives store tests a real, clean database.
"""
