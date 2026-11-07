CREATE SCHEMA IF NOT EXISTS fsgg_orchestration;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.observer_store_metadata (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    schema_version integer NOT NULL,
    migration_state text NOT NULL CHECK (migration_state IN ('applying','ready')),
    backup_identity uuid NOT NULL,
    updated_at timestamptz NOT NULL
);

DO $guard$
DECLARE root_version integer; root_state text; root_identity uuid;
BEGIN
    SELECT schema_version,migration_state,backup_identity
      INTO root_version,root_state,root_identity
      FROM fsgg_orchestration.store_metadata WHERE singleton;
    -- HostInitialization.initialize applies the execution migration before this one, and it moves a ready root schema from v1 to v2.
    -- The observer tables depend only on the ready root, so either root version is accepted (as the execution migration accepts 1 and 2).
    IF NOT FOUND OR root_version NOT IN (1, 2) OR root_state <> 'ready' THEN
        RAISE EXCEPTION 'observer migration requires ready root schema v1 or v2';
    END IF;
    IF EXISTS (
        SELECT 1 FROM fsgg_orchestration.observer_store_metadata
        WHERE singleton AND (schema_version <> 1 OR migration_state <> 'ready' OR backup_identity <> root_identity)
    ) THEN
        RAISE EXCEPTION 'refusing incompatible observer schema metadata';
    END IF;
END
$guard$;

INSERT INTO fsgg_orchestration.observer_store_metadata(singleton,schema_version,migration_state,backup_identity,updated_at)
SELECT true,1,'ready',backup_identity,statement_timestamp()
FROM fsgg_orchestration.store_metadata WHERE singleton
ON CONFLICT(singleton) DO NOTHING;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.observer_stream (
    observer_id text PRIMARY KEY,
    last_sequence bigint NOT NULL CHECK(last_sequence >= 0)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.observer_event (
    observer_id text NOT NULL REFERENCES fsgg_orchestration.observer_stream(observer_id),
    sequence_number bigint NOT NULL CHECK(sequence_number > 0),
    event_id uuid NOT NULL,
    schema_version integer NOT NULL CHECK(schema_version > 0),
    serializer_version text NOT NULL,
    payload bytea NOT NULL,
    payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9a-f]{64}$'),
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY(observer_id,sequence_number),
    UNIQUE(observer_id,event_id)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.observer_inbox (
    observer_id text NOT NULL,
    command_id uuid NOT NULL,
    body_sha256 text NOT NULL CHECK(body_sha256 ~ '^[0-9a-f]{64}$'),
    terminal_sequence bigint NOT NULL CHECK(terminal_sequence > 0),
    received_at timestamptz NOT NULL,
    PRIMARY KEY(observer_id,command_id),
    FOREIGN KEY(observer_id,terminal_sequence)
      REFERENCES fsgg_orchestration.observer_event(observer_id,sequence_number)
);
