CREATE SCHEMA IF NOT EXISTS fsgg_orchestration;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.execution_stream (
    assignment_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    generation bigint,
    last_revision bigint NOT NULL DEFAULT 0 CHECK (last_revision >= 0),
    PRIMARY KEY (assignment_id, attempt_id)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.execution_event (
    assignment_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    event_identity text NOT NULL CHECK (event_identity ~ '^[0-9a-f]{64}$'),
    schema text NOT NULL,
    payload bytea NOT NULL CHECK (octet_length(payload) <= 262144),
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (assignment_id, attempt_id, revision),
    UNIQUE (assignment_id, attempt_id, event_identity),
    FOREIGN KEY (assignment_id, attempt_id) REFERENCES fsgg_orchestration.execution_stream(assignment_id, attempt_id)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.executor_command (
    command_id uuid PRIMARY KEY,
    body_sha256 text NOT NULL CHECK (body_sha256 ~ '^[0-9a-f]{64}$'),
    assignment_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    generation bigint NOT NULL CHECK (generation >= 0),
    expected_revision bigint NOT NULL CHECK (expected_revision >= 0),
    deadline timestamptz NOT NULL,
    payload bytea NOT NULL CHECK (octet_length(payload) <= 32768),
    durable_revision bigint NOT NULL,
    visible boolean NOT NULL DEFAULT false,
    settled boolean NOT NULL DEFAULT false,
    receipt bytea,
    created_at timestamptz NOT NULL,
    UNIQUE (command_id, body_sha256)
);
CREATE INDEX IF NOT EXISTS ix_executor_pending ON fsgg_orchestration.executor_command(created_at,command_id) WHERE visible AND NOT settled;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.execution_input_object (
    input_sha256 text PRIMARY KEY CHECK (input_sha256 ~ '^[0-9a-f]{64}$'),
    bytes bytea NOT NULL CHECK (octet_length(bytes) <= 16777216),
    size_bytes bigint NOT NULL CHECK (size_bytes = octet_length(bytes)),
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.subscription_reservation (
    reservation_id uuid PRIMARY KEY,
    assignment_id uuid NOT NULL UNIQUE,
    attempt_id uuid NOT NULL UNIQUE,
    generation bigint NOT NULL CHECK (generation >= 0),
    expected_revision bigint NOT NULL CHECK (expected_revision > 0),
    reservation_payload bytea NOT NULL CHECK (octet_length(reservation_payload) <= 8192),
    settlement_payload bytea CHECK (settlement_payload IS NULL OR octet_length(settlement_payload) <= 8192),
    active boolean NOT NULL DEFAULT true,
    reserved_at timestamptz NOT NULL,
    deadline timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_subscription_active ON fsgg_orchestration.subscription_reservation(active,deadline);
