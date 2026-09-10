-- Schema version 1. Akka.Persistence.Sql DDL is adapted from upstream commit
-- 0c111df3d1685457e285cebe404ca3ab2b3b65c3 with an isolated schema.
CREATE SCHEMA IF NOT EXISTS fsgg_orchestration;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.store_metadata (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    schema_version integer NOT NULL,
    migration_state text NOT NULL CHECK (migration_state IN ('applying', 'ready')),
    backup_identity uuid NOT NULL,
    generation_fence bigint NOT NULL DEFAULT 0 CHECK (generation_fence >= 0),
    updated_at timestamptz NOT NULL
);

DO $guard$
DECLARE
    current_version integer;
    current_state text;
BEGIN
    SELECT schema_version, migration_state INTO current_version, current_state
    FROM fsgg_orchestration.store_metadata WHERE singleton;
    IF FOUND AND (current_version <> 1 OR current_state <> 'ready') THEN
        RAISE EXCEPTION 'refusing schema state version=% state=%', current_version, current_state;
    END IF;
END
$guard$;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.journal_metadata (
    persistence_id text NOT NULL,
    sequence_number bigint NOT NULL,
    CONSTRAINT pk_akka_journal_metadata PRIMARY KEY (persistence_id, sequence_number)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.journal (
    ordering bigserial PRIMARY KEY,
    deleted boolean NOT NULL DEFAULT false,
    persistence_id text NOT NULL,
    sequence_number bigint NOT NULL,
    created bigint NOT NULL,
    tags text,
    message bytea NOT NULL,
    identifier integer,
    manifest text,
    writer_uuid text,
    CONSTRAINT uq_akka_journal_stream UNIQUE (persistence_id, sequence_number)
);
CREATE INDEX IF NOT EXISTS ix_akka_journal_created ON fsgg_orchestration.journal (created);
CREATE INDEX IF NOT EXISTS ix_akka_journal_persistence ON fsgg_orchestration.journal (persistence_id);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.tags (
    ordering_id bigint NOT NULL,
    tag text NOT NULL,
    sequence_nr bigint NOT NULL,
    persistence_id text NOT NULL,
    CONSTRAINT pk_akka_tags PRIMARY KEY (ordering_id, tag)
);
CREATE INDEX IF NOT EXISTS ix_akka_tags_persistence_sequence ON fsgg_orchestration.tags (persistence_id, sequence_nr);
CREATE INDEX IF NOT EXISTS ix_akka_tags_tag ON fsgg_orchestration.tags (tag);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.snapshot (
    persistence_id text NOT NULL,
    sequence_number bigint NOT NULL,
    created bigint NOT NULL,
    snapshot bytea,
    manifest text,
    serializer_id integer,
    CONSTRAINT pk_akka_snapshot PRIMARY KEY (persistence_id, sequence_number)
);
CREATE INDEX IF NOT EXISTS ix_akka_snapshot_created ON fsgg_orchestration.snapshot (created);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.stream (
    persistence_id text PRIMARY KEY,
    last_sequence bigint NOT NULL CHECK (last_sequence >= 0)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.event (
    persistence_id text NOT NULL REFERENCES fsgg_orchestration.stream (persistence_id),
    sequence_number bigint NOT NULL CHECK (sequence_number > 0),
    event_id uuid NOT NULL,
    schema_version integer NOT NULL CHECK (schema_version > 0),
    serializer_version text NOT NULL,
    payload bytea NOT NULL,
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    effect_change smallint NOT NULL CHECK (effect_change BETWEEN 0 AND 2),
    effect_operation_id uuid,
    effect_kind smallint,
    effect_generation bigint,
    effect_workflow_revision bigint,
    effect_resource_id text,
    effect_payload_sha256 text,
    recorded_at timestamptz NOT NULL,
    CONSTRAINT pk_event PRIMARY KEY (persistence_id, sequence_number),
    CONSTRAINT uq_event_id UNIQUE (persistence_id, event_id),
    CONSTRAINT ck_effect_shape CHECK (
        (effect_change = 0 AND effect_operation_id IS NULL AND effect_kind IS NULL AND effect_generation IS NULL AND effect_workflow_revision IS NULL AND effect_resource_id IS NULL AND effect_payload_sha256 IS NULL)
        OR (effect_change = 1 AND effect_operation_id IS NOT NULL AND effect_kind IS NOT NULL AND effect_kind BETWEEN 0 AND 9 AND effect_generation IS NOT NULL AND effect_generation >= 0 AND effect_workflow_revision IS NOT NULL AND effect_workflow_revision >= 0 AND effect_resource_id IS NOT NULL AND length(effect_resource_id) BETWEEN 1 AND 256 AND effect_payload_sha256 IS NOT NULL AND effect_payload_sha256 ~ '^[0-9a-f]{64}$')
        OR (effect_change = 2 AND effect_operation_id IS NOT NULL AND effect_kind IS NULL AND effect_generation IS NULL AND effect_workflow_revision IS NULL AND effect_resource_id IS NULL AND effect_payload_sha256 IS NULL)
    )
);
-- The hosted-writer amendment appends effect kinds 5 through 9 without changing the
-- event row shape. Replace the version-1 constraint when upgrading an existing store.
ALTER TABLE fsgg_orchestration.event DROP CONSTRAINT IF EXISTS ck_effect_shape;
ALTER TABLE fsgg_orchestration.event ADD CONSTRAINT ck_effect_shape CHECK (
    (effect_change = 0 AND effect_operation_id IS NULL AND effect_kind IS NULL AND effect_generation IS NULL AND effect_workflow_revision IS NULL AND effect_resource_id IS NULL AND effect_payload_sha256 IS NULL)
    OR (effect_change = 1 AND effect_operation_id IS NOT NULL AND effect_kind IS NOT NULL AND effect_kind BETWEEN 0 AND 9 AND effect_generation IS NOT NULL AND effect_generation >= 0 AND effect_workflow_revision IS NOT NULL AND effect_workflow_revision >= 0 AND effect_resource_id IS NOT NULL AND length(effect_resource_id) BETWEEN 1 AND 256 AND effect_payload_sha256 IS NOT NULL AND effect_payload_sha256 ~ '^[0-9a-f]{64}$')
    OR (effect_change = 2 AND effect_operation_id IS NOT NULL AND effect_kind IS NULL AND effect_generation IS NULL AND effect_workflow_revision IS NULL AND effect_resource_id IS NULL AND effect_payload_sha256 IS NULL)
);
CREATE INDEX IF NOT EXISTS ix_event_effect ON fsgg_orchestration.event (persistence_id, effect_operation_id, sequence_number)
    WHERE effect_operation_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS fsgg_orchestration.inbox (
    persistence_id text NOT NULL,
    command_id uuid NOT NULL,
    body_sha256 text NOT NULL CHECK (body_sha256 ~ '^[0-9a-f]{64}$'),
    terminal_sequence bigint NOT NULL CHECK (terminal_sequence > 0),
    received_at timestamptz NOT NULL,
    CONSTRAINT pk_inbox PRIMARY KEY (persistence_id, command_id),
    CONSTRAINT fk_inbox_terminal_event FOREIGN KEY (persistence_id, terminal_sequence)
        REFERENCES fsgg_orchestration.event (persistence_id, sequence_number)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.domain_snapshot (
    persistence_id text NOT NULL,
    sequence_number bigint NOT NULL CHECK (sequence_number > 0),
    schema_version integer NOT NULL CHECK (schema_version > 0),
    payload bytea NOT NULL,
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT pk_domain_snapshot PRIMARY KEY (persistence_id, sequence_number),
    CONSTRAINT fk_snapshot_event FOREIGN KEY (persistence_id, sequence_number)
        REFERENCES fsgg_orchestration.event (persistence_id, sequence_number)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.projection_checkpoint (
    projection_id text NOT NULL,
    persistence_id text NOT NULL,
    sequence_number bigint NOT NULL CHECK (sequence_number >= 0),
    projection_version integer NOT NULL CHECK (projection_version > 0),
    CONSTRAINT pk_projection_checkpoint PRIMARY KEY (projection_id, persistence_id)
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.candidate_object (
    content_sha256 text PRIMARY KEY CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    bytes bytea NOT NULL,
    size_bytes bigint NOT NULL CHECK (size_bytes >= 0 AND size_bytes = octet_length(bytes)),
    created_at timestamptz NOT NULL,
    retain_until timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.candidate (
    candidate_id uuid PRIMARY KEY,
    content_sha256 text NOT NULL REFERENCES fsgg_orchestration.candidate_object (content_sha256),
    manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    baseline_sha text NOT NULL,
    head_sha text NOT NULL,
    tree_sha text NOT NULL,
    media_type text NOT NULL,
    size_bytes bigint NOT NULL CHECK (size_bytes >= 0),
    object_key text NOT NULL,
    retain_until timestamptz NOT NULL,
    receipt_sha256 text NOT NULL CHECK (receipt_sha256 ~ '^[0-9a-f]{64}$'),
    verified_at timestamptz NOT NULL,
    quarantined_reason text
);

CREATE TABLE IF NOT EXISTS fsgg_orchestration.candidate_upload_staging (
    upload_id uuid PRIMARY KEY,
    candidate_id uuid NOT NULL,
    content_sha256 text NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    bytes bytea NOT NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_candidate_staging_expiry ON fsgg_orchestration.candidate_upload_staging (expires_at, upload_id);

INSERT INTO fsgg_orchestration.store_metadata
    (singleton, schema_version, migration_state, backup_identity, updated_at)
VALUES (true, 1, 'ready', gen_random_uuid(), statement_timestamp())
ON CONFLICT (singleton) DO NOTHING;
