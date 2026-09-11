CREATE SCHEMA IF NOT EXISTS fsgg_orchestration;

DO $guard$
DECLARE current_version integer; current_state text;
BEGIN
  SELECT schema_version,migration_state INTO current_version,current_state
  FROM fsgg_orchestration.store_metadata WHERE singleton FOR UPDATE;
  IF NOT FOUND OR current_version NOT IN (1,2) OR current_state <> 'ready' THEN
    RAISE EXCEPTION 'refusing execution schema migration version=% state=%',current_version,current_state;
  END IF;
END
$guard$;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.execution_stream (
    assignment_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    generation bigint,
    executor_binding text,
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
    manifest_payload bytea NOT NULL CHECK (octet_length(manifest_payload) <= 32768),
    bytes bytea NOT NULL CHECK (octet_length(bytes) <= 16777216),
    size_bytes bigint NOT NULL CHECK (size_bytes = octet_length(bytes)),
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.execution_workspace_manifest (
    manifest_sha256 text PRIMARY KEY CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    payload bytea NOT NULL CHECK (octet_length(payload) <= 32768),
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.execution_route_binding (
    assignment_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    generation bigint NOT NULL CHECK (generation >= 0),
    binding_sha256 text NOT NULL CHECK (binding_sha256 ~ '^[0-9a-f]{64}$'),
    payload bytea NOT NULL CHECK (octet_length(payload) <= 32768),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (assignment_id,attempt_id),
    UNIQUE (binding_sha256)
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

UPDATE fsgg_orchestration.store_metadata
SET schema_version=2,updated_at=statement_timestamp()
WHERE singleton AND schema_version=1 AND migration_state='ready';
