-- =====================================================================================================
-- ATM Nexus  |  PostgreSQL 15+  |  V001 core schema
-- Conventions: snake_case, singular table names, uuid PKs for entities created by the platform,
-- timestamptz everywhere (UTC), no PAN / track / PIN data is ever stored.
-- Apply with: psql -v ON_ERROR_STOP=1 -f V001__core_schema.sql   (or Flyway / DbUp / EF migrations bundle)
-- =====================================================================================================
BEGIN;

CREATE EXTENSION IF NOT EXISTS pg_trgm;      -- fuzzy search on atm_code / branch name

-- -----------------------------------------------------------------------------------------------------
-- Reference data
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE vendor (
    vendor_id     smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code          varchar(32)  NOT NULL,
    name          varchar(100) NOT NULL,
    xfs_profile   varchar(64),                          -- vendor profile id (status layouts, event overrides)
    is_active     boolean      NOT NULL DEFAULT true,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_vendor_code UNIQUE (code),
    CONSTRAINT ck_vendor_code CHECK (code ~ '^[A-Z0-9_]+$')
);

CREATE TABLE location (
    location_id   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          varchar(150) NOT NULL,
    address_line1 varchar(200),
    address_line2 varchar(200),
    city          varchar(100),
    region        varchar(100),
    postal_code   varchar(20),
    country_code  char(2)      NOT NULL,
    latitude      numeric(9,6),
    longitude     numeric(9,6),
    site_type     varchar(24)  NOT NULL DEFAULT 'BRANCH',
    timezone      varchar(64)  NOT NULL DEFAULT 'UTC',
    created_at    timestamptz  NOT NULL DEFAULT now(),
    updated_at    timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_location_site_type CHECK (site_type IN ('BRANCH','OFFSITE','MALL','AIRPORT','DRIVE_THROUGH','OTHER')),
    CONSTRAINT ck_location_lat CHECK (latitude  IS NULL OR latitude  BETWEEN -90  AND 90),
    CONSTRAINT ck_location_lon CHECK (longitude IS NULL OR longitude BETWEEN -180 AND 180)
);
CREATE INDEX ix_location_city ON location (country_code, city);

CREATE TABLE branch (
    branch_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code             varchar(32)  NOT NULL,
    name             varchar(150) NOT NULL,
    region_code      varchar(32),
    location_id      uuid REFERENCES location(location_id),
    parent_branch_id uuid REFERENCES branch(branch_id),
    contact_phone    varchar(32),
    is_active        boolean      NOT NULL DEFAULT true,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_branch_code UNIQUE (code),
    CONSTRAINT ck_branch_not_self_parent CHECK (parent_branch_id IS NULL OR parent_branch_id <> branch_id)
);
CREATE INDEX ix_branch_region ON branch (region_code);
CREATE INDEX ix_branch_name_trgm ON branch USING gin (name gin_trgm_ops);

-- -----------------------------------------------------------------------------------------------------
-- ATM inventory + live status
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE atm (
    atm_id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    atm_code           varchar(32)  NOT NULL,              -- bank terminal id, also the agent client_id
    serial_number      varchar(64),
    vendor_id          smallint     NOT NULL REFERENCES vendor(vendor_id),
    model              varchar(64),
    branch_id          uuid REFERENCES branch(branch_id),
    location_id        uuid REFERENCES location(location_id),
    ip_address         inet,
    mac_address        macaddr,
    os_version         varchar(64),
    agent_version      varchar(32),
    xfs_version        varchar(16),
    application_name   varchar(64),
    application_version varchar(32),
    install_date       date,
    commission_status  varchar(16)  NOT NULL DEFAULT 'PLANNED',
    tags               jsonb        NOT NULL DEFAULT '{}'::jsonb,
    created_at         timestamptz  NOT NULL DEFAULT now(),
    updated_at         timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_atm_code UNIQUE (atm_code),
    CONSTRAINT uq_atm_serial UNIQUE (vendor_id, serial_number),
    CONSTRAINT ck_atm_commission CHECK (commission_status IN ('PLANNED','ACTIVE','MAINTENANCE','DECOMMISSIONED')),
    CONSTRAINT ck_atm_code_fmt CHECK (atm_code ~ '^[A-Za-z0-9_-]{3,32}$')
);
CREATE INDEX ix_atm_branch  ON atm (branch_id);
CREATE INDEX ix_atm_vendor_model ON atm (vendor_id, model);
CREATE INDEX ix_atm_commission ON atm (commission_status) WHERE commission_status <> 'DECOMMISSIONED';
CREATE INDEX ix_atm_code_trgm ON atm USING gin (atm_code gin_trgm_ops);
CREATE INDEX ix_atm_tags ON atm USING gin (tags jsonb_path_ops);

-- Current status: exactly one row per ATM, updated on every telemetry message (hot table: low fillfactor for HOT updates)
CREATE TABLE atm_status (
    atm_id            uuid PRIMARY KEY REFERENCES atm(atm_id) ON DELETE CASCADE,
    health_score      smallint     NOT NULL DEFAULT 100,
    health_level      varchar(10)  NOT NULL DEFAULT 'HEALTHY',
    connectivity      varchar(10)  NOT NULL DEFAULT 'UNKNOWN',
    in_service        boolean      NOT NULL DEFAULT true,
    last_seen_at      timestamptz,
    last_state_change_at timestamptz NOT NULL DEFAULT now(),
    components        jsonb        NOT NULL DEFAULT '{}'::jsonb,   -- {"devices":100,"cash":40,...}
    reasons           jsonb        NOT NULL DEFAULT '[]'::jsonb,
    updated_at        timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_status_score CHECK (health_score BETWEEN 0 AND 100),
    CONSTRAINT ck_status_level CHECK (health_level IN ('HEALTHY','DEGRADED','CRITICAL','DOWN')),
    CONSTRAINT ck_status_conn  CHECK (connectivity IN ('ONLINE','OFFLINE','UNKNOWN'))
) WITH (fillfactor = 70);
CREATE INDEX ix_status_level ON atm_status (health_level, connectivity);
CREATE INDEX ix_status_last_seen ON atm_status (last_seen_at) WHERE connectivity = 'ONLINE';   -- offline detector

-- Status history for trends, MTBF and AI features. Monthly partitions (create ahead with pg_partman or a cron job).
CREATE TABLE atm_status_history (
    atm_id        uuid        NOT NULL,
    recorded_at   timestamptz NOT NULL,
    health_score  smallint    NOT NULL,
    health_level  varchar(10) NOT NULL,
    connectivity  varchar(10) NOT NULL,
    PRIMARY KEY (atm_id, recorded_at)
) PARTITION BY RANGE (recorded_at);
CREATE TABLE atm_status_history_2025_01 PARTITION OF atm_status_history FOR VALUES FROM ('2025-01-01') TO ('2025-02-01');
CREATE TABLE atm_status_history_default PARTITION OF atm_status_history DEFAULT;

CREATE TABLE atm_component (
    component_id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    atm_id              uuid         NOT NULL REFERENCES atm(atm_id) ON DELETE CASCADE,
    logical_name        varchar(64)  NOT NULL,              -- XFS logical service name
    device_class        varchar(24)  NOT NULL,
    state               varchar(16)  NOT NULL DEFAULT 'UNKNOWN',
    vendor              varchar(32),
    model               varchar(64),
    serial_number       varchar(64),
    firmware            varchar(64),
    attributes          jsonb        NOT NULL DEFAULT '{}'::jsonb,
    last_state_change_at timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_component UNIQUE (atm_id, logical_name),
    CONSTRAINT ck_component_class CHECK (device_class IN ('CARD_READER','CASH_DISPENSER','CASH_RECYCLER','DEPOSITORY','PRINTER',
        'PIN_PAD','SENSORS','CAMERA','BARCODE_READER','PASSPORT_READER','NFC_READER','BIOMETRIC')),
    CONSTRAINT ck_component_state CHECK (state IN ('ONLINE','OFFLINE','POWER_OFF','NO_DEVICE','HARDWARE_ERROR','USER_ERROR','BUSY','FRAUD_ATTEMPT','UNKNOWN'))
) WITH (fillfactor = 80);
CREATE INDEX ix_component_state ON atm_component (state) WHERE state NOT IN ('ONLINE','BUSY');

CREATE TABLE atm_cash_unit (
    atm_id         uuid         NOT NULL REFERENCES atm(atm_id) ON DELETE CASCADE,
    unit_number    smallint     NOT NULL,
    unit_id        varchar(16)  NOT NULL,
    unit_type      varchar(16)  NOT NULL,
    currency       char(3),
    denomination   integer      NOT NULL DEFAULT 0,
    note_count     integer      NOT NULL DEFAULT 0,
    initial_count  integer      NOT NULL DEFAULT 0,
    reject_count   integer      NOT NULL DEFAULT 0,
    minimum        integer      NOT NULL DEFAULT 0,
    maximum        integer      NOT NULL DEFAULT 0,
    status         varchar(16)  NOT NULL,
    updated_at     timestamptz  NOT NULL DEFAULT now(),
    PRIMARY KEY (atm_id, unit_number),
    CONSTRAINT ck_cash_counts CHECK (note_count >= 0 AND initial_count >= 0 AND reject_count >= 0 AND denomination >= 0)
) WITH (fillfactor = 80);
CREATE INDEX ix_cash_status ON atm_cash_unit (status) WHERE status IN ('Low','Empty','Inoperative','Missing');

-- -----------------------------------------------------------------------------------------------------
-- Alerts & incidents
-- -----------------------------------------------------------------------------------------------------
CREATE SEQUENCE incident_number_seq START 1;

CREATE TABLE atm_incident (
    incident_id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    incident_number  varchar(20)  NOT NULL DEFAULT ('INC-' || to_char(now(), 'YYYY') || '-' || lpad(nextval('incident_number_seq')::text, 6, '0')),
    atm_id           uuid         NOT NULL REFERENCES atm(atm_id),
    title            varchar(200) NOT NULL,
    description      text,
    priority         smallint     NOT NULL DEFAULT 3,         -- 1 = P1 (critical) .. 4 = P4
    category         varchar(32)  NOT NULL DEFAULT 'HARDWARE',
    state            varchar(16)  NOT NULL DEFAULT 'NEW',
    assigned_group   varchar(64),
    assigned_to      uuid,
    created_by       varchar(128) NOT NULL,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    acknowledged_at  timestamptz,
    resolved_at      timestamptz,
    closed_at        timestamptz,
    sla_due_at       timestamptz,
    resolution_code  varchar(32),
    resolution_notes text,
    source_alert_id  uuid,
    CONSTRAINT uq_incident_number UNIQUE (incident_number),
    CONSTRAINT ck_incident_priority CHECK (priority BETWEEN 1 AND 4),
    CONSTRAINT ck_incident_state CHECK (state IN ('NEW','ASSIGNED','IN_PROGRESS','PENDING','RESOLVED','CLOSED')),
    CONSTRAINT ck_incident_category CHECK (category IN ('HARDWARE','CASH','SOFTWARE','NETWORK','SECURITY','OTHER')),
    CONSTRAINT ck_incident_times CHECK (resolved_at IS NULL OR resolved_at >= created_at)
);
CREATE INDEX ix_incident_open ON atm_incident (priority, created_at) WHERE state NOT IN ('RESOLVED','CLOSED');
CREATE INDEX ix_incident_atm  ON atm_incident (atm_id, created_at DESC);
CREATE INDEX ix_incident_sla  ON atm_incident (sla_due_at) WHERE state NOT IN ('RESOLVED','CLOSED');
-- at most one open incident per (ATM, category): prevents alert storms creating duplicate tickets
CREATE UNIQUE INDEX uq_incident_open_per_atm_category ON atm_incident (atm_id, category) WHERE state NOT IN ('RESOLVED','CLOSED');

CREATE TABLE atm_alert (
    alert_id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    atm_id           uuid         NOT NULL REFERENCES atm(atm_id) ON DELETE CASCADE,
    code             varchar(64)  NOT NULL,
    severity         smallint     NOT NULL,                  -- 0 info, 1 warning, 2 major, 3 critical
    source           varchar(64)  NOT NULL,
    message          varchar(1000) NOT NULL,
    data             jsonb        NOT NULL DEFAULT '{}'::jsonb,
    fingerprint      varchar(160) NOT NULL,                  -- code|source|unit : de-duplication key
    state            varchar(14)  NOT NULL DEFAULT 'OPEN',
    first_raised_at  timestamptz  NOT NULL DEFAULT now(),
    last_raised_at   timestamptz  NOT NULL DEFAULT now(),
    occurrences      integer      NOT NULL DEFAULT 1,
    acknowledged_by  varchar(128),
    acknowledged_at  timestamptz,
    cleared_at       timestamptz,
    incident_id      uuid REFERENCES atm_incident(incident_id),
    CONSTRAINT ck_alert_severity CHECK (severity BETWEEN 0 AND 3),
    CONSTRAINT ck_alert_state CHECK (state IN ('OPEN','ACKNOWLEDGED','CLEARED','SUPPRESSED'))
);
CREATE UNIQUE INDEX uq_alert_active_fingerprint ON atm_alert (atm_id, fingerprint) WHERE state IN ('OPEN','ACKNOWLEDGED');
CREATE INDEX ix_alert_open ON atm_alert (severity DESC, last_raised_at DESC) WHERE state IN ('OPEN','ACKNOWLEDGED');
CREATE INDEX ix_alert_atm ON atm_alert (atm_id, first_raised_at DESC);
CREATE INDEX ix_alert_incident ON atm_alert (incident_id) WHERE incident_id IS NOT NULL;
ALTER TABLE atm_incident ADD CONSTRAINT fk_incident_source_alert FOREIGN KEY (source_alert_id) REFERENCES atm_alert(alert_id);

-- -----------------------------------------------------------------------------------------------------
-- Software deployment
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE deployment_package (
    package_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name          varchar(100) NOT NULL,
    version       varchar(32)  NOT NULL,
    package_type  varchar(16)  NOT NULL,
    vendor_id     smallint REFERENCES vendor(vendor_id),     -- NULL = vendor neutral
    file_uri      varchar(500) NOT NULL,                     -- blob/OCI location (never a local path)
    sha256        char(64)     NOT NULL,
    size_bytes    bigint       NOT NULL,
    signature     text         NOT NULL,                     -- detached signature verified by the agent before install
    release_notes text,
    is_active     boolean      NOT NULL DEFAULT true,
    created_by    varchar(128) NOT NULL,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_package UNIQUE (name, version),
    CONSTRAINT ck_package_type CHECK (package_type IN ('SOFTWARE','CONFIG','OS_PATCH','AGENT')),
    CONSTRAINT ck_package_sha CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_package_size CHECK (size_bytes > 0)
);

CREATE TABLE deployment_history (
    deployment_id  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    package_id     uuid         NOT NULL REFERENCES deployment_package(package_id),
    atm_id         uuid         NOT NULL REFERENCES atm(atm_id),
    strategy       varchar(20)  NOT NULL DEFAULT 'MAINTENANCE_WINDOW',
    state          varchar(14)  NOT NULL DEFAULT 'SCHEDULED',
    attempt        smallint     NOT NULL DEFAULT 1,
    scheduled_at   timestamptz  NOT NULL,
    started_at     timestamptz,
    completed_at   timestamptz,
    requested_by   varchar(128) NOT NULL,
    approved_by    varchar(128),
    failure_reason varchar(1000),
    log_uri        varchar(500),
    CONSTRAINT ck_deploy_strategy CHECK (strategy IN ('IMMEDIATE','MAINTENANCE_WINDOW','CANARY')),
    CONSTRAINT ck_deploy_state CHECK (state IN ('SCHEDULED','DOWNLOADING','INSTALLING','SUCCEEDED','FAILED','ROLLED_BACK','CANCELLED')),
    CONSTRAINT ck_deploy_four_eyes CHECK (approved_by IS NULL OR approved_by <> requested_by)
);
CREATE INDEX ix_deploy_atm ON deployment_history (atm_id, scheduled_at DESC);
CREATE INDEX ix_deploy_active ON deployment_history (state, scheduled_at) WHERE state IN ('SCHEDULED','DOWNLOADING','INSTALLING');
CREATE INDEX ix_deploy_package ON deployment_history (package_id, state);

-- -----------------------------------------------------------------------------------------------------
-- Identity & RBAC (users are provisioned just-in-time from the IdP; passwords are never stored here)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE app_user (
    user_id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    external_id   varchar(128) NOT NULL,                     -- OIDC "sub"
    username      varchar(64)  NOT NULL,
    display_name  varchar(150) NOT NULL,
    email         varchar(254),
    is_active     boolean      NOT NULL DEFAULT true,
    last_login_at timestamptz,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT uq_user_external UNIQUE (external_id),
    CONSTRAINT uq_user_username UNIQUE (username)
);

CREATE TABLE role (
    role_id     smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code        varchar(32)  NOT NULL,
    name        varchar(100) NOT NULL,
    description varchar(300),
    CONSTRAINT uq_role_code UNIQUE (code)
);

CREATE TABLE permission (
    permission_id smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code          varchar(64)  NOT NULL,
    description   varchar(300),
    CONSTRAINT uq_permission_code UNIQUE (code),
    CONSTRAINT ck_permission_code CHECK (code ~ '^[a-z]+(\.[a-z_]+)+$')
);

CREATE TABLE role_permission (
    role_id       smallint NOT NULL REFERENCES role(role_id) ON DELETE CASCADE,
    permission_id smallint NOT NULL REFERENCES permission(permission_id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

CREATE TABLE user_role (
    user_id      uuid     NOT NULL REFERENCES app_user(user_id) ON DELETE CASCADE,
    role_id      smallint NOT NULL REFERENCES role(role_id) ON DELETE CASCADE,
    scope_region varchar(32),                                -- NULL = all regions (row-level scoping by branch.region_code)
    granted_by   varchar(128),
    granted_at   timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX uq_user_role_scope ON user_role (user_id, role_id, COALESCE(scope_region, '*'));

-- -----------------------------------------------------------------------------------------------------
-- Audit log: append-only, hash-chained (PCI DSS 10.3 / 10.5), partitioned by month
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE audit_log (
    audit_id       bigint GENERATED ALWAYS AS IDENTITY,
    occurred_at    timestamptz  NOT NULL DEFAULT now(),
    actor_id       varchar(128) NOT NULL,                    -- user sub, "agent:<atm_code>" or "system"
    actor_name     varchar(150),
    actor_ip       inet,
    action         varchar(64)  NOT NULL,                    -- e.g. atm.command.restart
    entity_type    varchar(32)  NOT NULL,
    entity_id      varchar(64),
    atm_id         uuid,
    outcome        varchar(8)   NOT NULL,
    details        jsonb        NOT NULL DEFAULT '{}'::jsonb,
    correlation_id varchar(64),
    prev_hash      bytea        NOT NULL,
    row_hash       bytea        NOT NULL,
    PRIMARY KEY (audit_id, occurred_at),
    CONSTRAINT ck_audit_outcome CHECK (outcome IN ('SUCCESS','DENIED','FAILURE'))
) PARTITION BY RANGE (occurred_at);
CREATE TABLE audit_log_2025_01 PARTITION OF audit_log FOR VALUES FROM ('2025-01-01') TO ('2025-02-01');
CREATE TABLE audit_log_default PARTITION OF audit_log DEFAULT;
CREATE INDEX ix_audit_time   ON audit_log (occurred_at DESC);
CREATE INDEX ix_audit_actor  ON audit_log (actor_id, occurred_at DESC);
CREATE INDEX ix_audit_entity ON audit_log (entity_type, entity_id, occurred_at DESC);
CREATE INDEX ix_audit_atm    ON audit_log (atm_id, occurred_at DESC) WHERE atm_id IS NOT NULL;

CREATE FUNCTION audit_append(p_actor_id text, p_actor_name text, p_actor_ip inet, p_action text, p_entity_type text,
                             p_entity_id text, p_atm_id uuid, p_outcome text, p_details jsonb, p_correlation text)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE
    v_prev bytea; v_now timestamptz := clock_timestamp(); v_hash bytea; v_id bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(727001);                   -- serialise the chain; shard per partition if write rate demands
    SELECT row_hash INTO v_prev FROM audit_log ORDER BY occurred_at DESC, audit_id DESC LIMIT 1;
    v_prev := COALESCE(v_prev, '\x00'::bytea);
    v_hash := sha256(v_prev || convert_to(concat_ws('|', v_now::text, p_actor_id, p_action, p_entity_type, p_entity_id,
                                                    p_atm_id::text, p_outcome, p_details::text), 'UTF8'));
    INSERT INTO audit_log (occurred_at, actor_id, actor_name, actor_ip, action, entity_type, entity_id, atm_id, outcome, details,
                           correlation_id, prev_hash, row_hash)
    VALUES (v_now, p_actor_id, p_actor_name, p_actor_ip, p_action, p_entity_type, p_entity_id, p_atm_id, p_outcome,
            COALESCE(p_details, '{}'::jsonb), p_correlation, v_prev, v_hash)
    RETURNING audit_id INTO v_id;
    RETURN v_id;
END $$;

CREATE FUNCTION audit_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'audit_log is append-only'; END $$;
CREATE TRIGGER trg_audit_no_update BEFORE UPDATE OR DELETE ON audit_log FOR EACH ROW EXECUTE FUNCTION audit_immutable();

-- -----------------------------------------------------------------------------------------------------
-- Transactional outbox (events are written in the same transaction as the state change, relayed to Kafka)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE outbox_event (
    outbox_id    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    topic        varchar(100) NOT NULL,
    event_key    varchar(100) NOT NULL,
    payload      jsonb        NOT NULL,
    created_at   timestamptz  NOT NULL DEFAULT now(),
    published_at timestamptz,
    attempts     integer      NOT NULL DEFAULT 0
);
CREATE INDEX ix_outbox_pending ON outbox_event (outbox_id) WHERE published_at IS NULL;

-- -----------------------------------------------------------------------------------------------------
-- Remote commands (signed by the platform, pulled by the agent)
-- -----------------------------------------------------------------------------------------------------
CREATE TABLE remote_command (
    command_id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    atm_id        uuid         NOT NULL REFERENCES atm(atm_id),
    command_type  varchar(32)  NOT NULL,
    parameters    jsonb        NOT NULL DEFAULT '{}'::jsonb,
    issued_by     varchar(128) NOT NULL,
    approved_by   varchar(128),
    issued_at     timestamptz  NOT NULL DEFAULT now(),
    expires_at    timestamptz  NOT NULL,
    nonce         varchar(64)  NOT NULL,
    key_id        varchar(32),
    signature     text,                                       -- NULL while PENDING_APPROVAL
    state         varchar(16)  NOT NULL DEFAULT 'PENDING',
    delivered_at  timestamptz,
    completed_at  timestamptz,
    output        text,
    error         text,
    CONSTRAINT ck_cmd_type CHECK (command_type IN ('RestartApplication','RestartXfs','RestartDevice','RestartWindowsService',
        'CollectLogs','CollectEj','RunDiagnostics','RebootAtm','ShutdownAtm')),
    CONSTRAINT ck_cmd_state CHECK (state IN ('PENDING_APPROVAL','PENDING','DELIVERED','RUNNING','SUCCEEDED','FAILED','REJECTED','EXPIRED')),
    CONSTRAINT ck_cmd_four_eyes CHECK (approved_by IS NULL OR approved_by <> issued_by),
    CONSTRAINT ck_cmd_signed CHECK (state = 'PENDING_APPROVAL' OR signature IS NOT NULL),
    CONSTRAINT uq_cmd_nonce UNIQUE (nonce)
);
CREATE INDEX ix_cmd_pending ON remote_command (atm_id, issued_at) WHERE state = 'PENDING';
CREATE INDEX ix_cmd_atm ON remote_command (atm_id, issued_at DESC);

-- -----------------------------------------------------------------------------------------------------
-- Seed / sample data
-- -----------------------------------------------------------------------------------------------------
INSERT INTO vendor (code, name, xfs_profile) VALUES
 ('NCR','NCR Atleos','ncr-aptra-xfs3'), ('DN','Diebold Nixdorf','dn-wincor-xfs3'), ('HYOSUNG','Hyosung TNS','hyosung-xfs3'),
 ('GRG','GRG Banking','grg-xfs3'), ('HITACHI','Hitachi-Omron','hitachi-xfs3'), ('WINCOR','Wincor Nixdorf','dn-wincor-xfs3'),
 ('KAL','KAL Kalignite','kal-xfs3'), ('CUSTOM','Custom XFS device','generic-xfs3');

INSERT INTO permission (code, description) VALUES
 ('atm.read','View ATMs, status and dashboards'), ('atm.manage','Create/update ATM master data'),
 ('atm.command.execute','Issue non-disruptive remote commands'), ('atm.command.approve','Approve disruptive remote commands (second person)'),
 ('alert.read','View alerts'), ('alert.ack','Acknowledge and clear alerts'),
 ('incident.read','View incidents'), ('incident.manage','Create and update incidents'),
 ('deployment.read','View deployments'), ('deployment.manage','Create deployments'), ('deployment.approve','Approve deployments'),
 ('user.read','View users and roles'), ('user.manage','Manage users and role assignments'),
 ('audit.read','Read audit log'), ('report.read','Run reports');

INSERT INTO role (code, name, description) VALUES
 ('NOC_VIEWER','NOC Viewer','Read-only monitoring'),
 ('NOC_OPERATOR','NOC Operator','Triage alerts/incidents, run safe commands'),
 ('FIELD_ENGINEER','Field Engineer','Update incidents assigned to them'),
 ('SUPERVISOR','NOC Supervisor','Approve disruptive commands and deployments'),
 ('ADMIN','Administrator','User and master-data management'),
 ('AUDITOR','Auditor','Read-only incl. audit log');

INSERT INTO role_permission (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM role r JOIN permission p ON
  (r.code='NOC_VIEWER'     AND p.code IN ('atm.read','alert.read','incident.read','deployment.read','report.read')) OR
  (r.code='NOC_OPERATOR'   AND p.code IN ('atm.read','alert.read','alert.ack','incident.read','incident.manage','atm.command.execute','deployment.read','report.read')) OR
  (r.code='FIELD_ENGINEER' AND p.code IN ('atm.read','alert.read','incident.read','incident.manage')) OR
  (r.code='SUPERVISOR'     AND p.code IN ('atm.read','alert.read','alert.ack','incident.read','incident.manage','atm.command.execute','atm.command.approve','deployment.read','deployment.manage','deployment.approve','report.read')) OR
  (r.code='ADMIN'          AND p.code IN ('atm.read','atm.manage','user.read','user.manage','deployment.read','report.read')) OR
  (r.code='AUDITOR'        AND p.code IN ('atm.read','alert.read','incident.read','deployment.read','audit.read','user.read','report.read'));

INSERT INTO location (location_id, name, address_line1, city, region, postal_code, country_code, latitude, longitude, site_type, timezone)
VALUES ('11111111-1111-1111-1111-111111111111','Main Street Branch','1 Main Street','Chennai','Tamil Nadu','600001','IN',13.082700,80.270700,'BRANCH','Asia/Kolkata'),
       ('22222222-2222-2222-2222-222222222222','Airport T1 Lobby','Terminal 1','Chennai','Tamil Nadu','600027','IN',12.990000,80.169300,'AIRPORT','Asia/Kolkata');

INSERT INTO branch (branch_id, code, name, region_code, location_id)
VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','BR-0001','Main Street','SOUTH','11111111-1111-1111-1111-111111111111'),
       ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','BR-0002','Airport T1','SOUTH','22222222-2222-2222-2222-222222222222');

INSERT INTO atm (atm_id, atm_code, serial_number, vendor_id, model, branch_id, location_id, ip_address, os_version, agent_version,
                 xfs_version, application_name, application_version, install_date, commission_status, tags)
VALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc','ATM-000123','NCR-SN-77881',(SELECT vendor_id FROM vendor WHERE code='NCR'),'SelfServ 84',
        'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','11111111-1111-1111-1111-111111111111','10.20.30.41','Windows 10 IoT LTSC 2021','0.1.0','3.30',
        'APTRA Advance NDC','5.4.2','2023-03-14','ACTIVE','{"cit":"SecureCash","tier":"A"}'),
       ('dddddddd-dddd-dddd-dddd-dddddddddddd','ATM-000124','DN-SN-55120',(SELECT vendor_id FROM vendor WHERE code='DN'),'DN Series 200',
        'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','22222222-2222-2222-2222-222222222222','10.20.30.42','Windows 10 IoT LTSC 2021','0.1.0','3.20',
        'ProBase/C','2.9.1','2022-11-02','ACTIVE','{"cit":"SecureCash","tier":"A"}');

INSERT INTO atm_status (atm_id, health_score, health_level, connectivity, in_service, last_seen_at, components, reasons)
VALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc',92,'HEALTHY','ONLINE',true,now(),'{"devices":100,"cash":90,"application":100,"network":100,"security":100,"history":96}','[]'),
       ('dddddddd-dddd-dddd-dddd-dddddddddddd',38,'CRITICAL','ONLINE',true,now(),'{"devices":60,"cash":40,"application":100,"network":100,"security":100,"history":70}','["Cash unit 2 (USD 50) low: 90"]');

INSERT INTO atm_component (atm_id, logical_name, device_class, state, vendor, model, firmware, attributes) VALUES
 ('cccccccc-cccc-cccc-cccc-cccccccccccc','CardReader','CARD_READER','ONLINE','NCR','Smart Card Reader','2.1.4','{"media":"NotPresent","retainBin":"Ok"}'),
 ('cccccccc-cccc-cccc-cccc-cccccccccccc','CashDispenser','CASH_DISPENSER','ONLINE','NCR','S2 Dispenser','5.0.1','{"safeDoor":"closed","dispenser":"ok"}'),
 ('dddddddd-dddd-dddd-dddd-dddddddddddd','IDC30','CARD_READER','HARDWARE_ERROR','DN','CHD V4','1.8.0','{"media":"Jammed"}');

INSERT INTO atm_cash_unit (atm_id, unit_number, unit_id, unit_type, currency, denomination, note_count, initial_count, minimum, maximum, status) VALUES
 ('cccccccc-cccc-cccc-cccc-cccccccccccc',1,'CASS1','Dispense','INR',500,1450,2000,200,2500,'Ok'),
 ('cccccccc-cccc-cccc-cccc-cccccccccccc',2,'CASS2','Dispense','INR',2000,380,1000,150,1500,'Ok'),
 ('dddddddd-dddd-dddd-dddd-dddddddddddd',2,'CASS2','Dispense','INR',2000,90,1000,150,1500,'Low');

INSERT INTO atm_alert (atm_id, code, severity, source, message, data, fingerprint) VALUES
 ('dddddddd-dddd-dddd-dddd-dddddddddddd','CARD_JAM',3,'IDC30','Card jammed in reader','{"device":"IDC30"}','CARD_JAM|IDC30|'),
 ('dddddddd-dddd-dddd-dddd-dddddddddddd','CASH_LOW',2,'CASS2','Cash unit 2 (INR 2000) low: 90','{"device":"CashDispenser","unit":"2"}','CASH_LOW|CashDispenser|2');

INSERT INTO deployment_package (name, version, package_type, vendor_id, file_uri, sha256, size_bytes, signature, created_by)
VALUES ('ATMNexus.Agent','0.1.0','AGENT',NULL,'https://artifacts.example.bank/agent/0.1.0/agent.zip',
        repeat('a',64),48234567,'MEUCIQ...sample...','release-pipeline');

INSERT INTO app_user (external_id, username, display_name, email) VALUES ('00000000-0000-0000-0000-0000000000a1','noc.operator1','NOC Operator One','noc1@example.bank');
INSERT INTO user_role (user_id, role_id, granted_by) SELECT u.user_id, r.role_id, 'seed' FROM app_user u, role r WHERE u.username='noc.operator1' AND r.code='NOC_OPERATOR';

SELECT audit_append('system','Seed',NULL,'db.seed','schema','V001',NULL,'SUCCESS','{"version":"V001"}'::jsonb,NULL);

COMMIT;
