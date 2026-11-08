-- Intersect initial schema (PostgreSQL 16 + PostGIS 3).
-- Verified against PostgreSQL 16.15 / PostGIS 3.4.2.
-- Geographic data: geometry(Point, 4326) with a GiST index. Enums are stored as text (names match the C# enums).

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE users (
    id                        uuid PRIMARY KEY,
    display_name              varchar(40)  NOT NULL,
    email                     varchar(254) NULL,
    password_hash             text         NULL,
    primary_nostr_public_key  varchar(64)  NULL,
    created_at                timestamptz  NOT NULL,
    status                    text         NOT NULL CHECK (status IN ('Active', 'Suspended'))
);
CREATE UNIQUE INDEX ux_users_email ON users (email) WHERE email IS NOT NULL;

CREATE TABLE external_identities (
    id                   uuid PRIMARY KEY,
    user_id              uuid        NOT NULL REFERENCES users (id),
    provider             varchar(32) NOT NULL,
    external_identifier  varchar(128) NOT NULL,
    linked_at            timestamptz NOT NULL
);
CREATE UNIQUE INDEX ux_external_identities_provider_identifier ON external_identities (provider, external_identifier);
CREATE INDEX ix_external_identities_user ON external_identities (user_id);

CREATE TABLE communities (
    id                  uuid PRIMARY KEY,
    name                varchar(80)   NOT NULL,
    slug                varchar(80)   NOT NULL,
    description         varchar(2000) NOT NULL DEFAULT '',
    category            text          NOT NULL CHECK (category IN
        ('DigitalNomads','Alumni','Conference','Photography','Gaming','Technology','Arts','Music','Sports','Outdoors','Food','Travel','History','Social','Other')),
    topic               varchar(60)   NULL,
    cover_image_url     varchar(500)  NULL,

    location_name       varchar(120)  NULL,
    latitude            double precision NULL,
    longitude           double precision NULL,
    -- Generated from latitude/longitude so the application never writes geometry directly.
    location_point      geometry(Point, 4326) GENERATED ALWAYS AS (
        CASE WHEN latitude IS NULL OR longitude IS NULL THEN NULL
             ELSE ST_SetSRID(ST_MakePoint(longitude, latitude), 4326) END
    ) STORED,
    -- Future-ready: polygon areas (neighbourhoods, venues). Not written by the POC.
    location_area       geometry(Polygon, 4326) NULL,

    -- Context dates: the period the community is ABOUT. Independent of created_at / expires_at.
    context_start_date  date NULL,
    context_end_date    date NULL,

    created_at          timestamptz NOT NULL,
    expires_at          timestamptz NULL,

    visibility          text NOT NULL CHECK (visibility  IN ('Public','Private','Unlisted')),
    access_mode         text NOT NULL CHECK (access_mode IN ('Open','ApprovalRequired','InvitationOnly','Verified')),
    pricing_mode        text NOT NULL CHECK (pricing_mode IN ('Free','Paid')),
    price_amount        numeric(12,2) NULL,
    price_currency      varchar(3)    NULL,
    status              text NOT NULL CHECK (status IN ('Draft','Active','Archived','Expired')),
    created_by_user_id  uuid NOT NULL REFERENCES users (id),
    member_count        integer NOT NULL DEFAULT 0 CHECK (member_count >= 0),

    CONSTRAINT ck_communities_latitude  CHECK (latitude  IS NULL OR latitude  BETWEEN -90  AND 90),
    CONSTRAINT ck_communities_longitude CHECK (longitude IS NULL OR longitude BETWEEN -180 AND 180),
    CONSTRAINT ck_communities_location_pair CHECK ((latitude IS NULL) = (longitude IS NULL)),
    CONSTRAINT ck_communities_context_order CHECK (context_start_date IS NULL OR context_end_date IS NULL OR context_start_date <= context_end_date),
    CONSTRAINT ck_communities_paid_price CHECK (pricing_mode = 'Free' OR price_amount > 0)
);
CREATE UNIQUE INDEX ux_communities_slug ON communities (slug);
CREATE INDEX ix_communities_location_point ON communities USING gist (location_point);
CREATE INDEX ix_communities_status_visibility ON communities (status, visibility);
CREATE INDEX ix_communities_category ON communities (category);
CREATE INDEX ix_communities_context_dates ON communities (context_start_date, context_end_date);
CREATE INDEX ix_communities_expires_at ON communities (expires_at) WHERE status = 'Active' AND expires_at IS NOT NULL;
CREATE INDEX ix_communities_name_trgm ON communities USING gin (name gin_trgm_ops);
CREATE INDEX ix_communities_description_trgm ON communities USING gin (description gin_trgm_ops);

CREATE TABLE community_members (
    community_id        uuid NOT NULL REFERENCES communities (id),
    user_id             uuid NOT NULL REFERENCES users (id),
    role                text NOT NULL CHECK (role IN ('Member','Admin','Owner')),
    status              text NOT NULL CHECK (status IN ('Pending','Invited','AwaitingPayment','Active','Left','Removed','Banned')),
    invited_by_user_id  uuid NULL REFERENCES users (id),
    requested_at        timestamptz NULL,
    joined_at           timestamptz NULL,
    updated_at          timestamptz NOT NULL,
    paid_until          timestamptz NULL,
    PRIMARY KEY (community_id, user_id)
);
CREATE INDEX ix_community_members_user_status ON community_members (user_id, status);
CREATE INDEX ix_community_members_community_status ON community_members (community_id, status);
CREATE INDEX ix_community_members_paid_until ON community_members (paid_until) WHERE status = 'Active' AND paid_until IS NOT NULL;

CREATE TABLE conversations (
    id            uuid PRIMARY KEY,
    type          text NOT NULL CHECK (type IN ('Community','Direct')),
    community_id  uuid NULL REFERENCES communities (id),
    direct_key    varchar(80) NULL,
    created_at    timestamptz NOT NULL,
    status        text NOT NULL CHECK (status IN ('Active','Closed')),
    CONSTRAINT ck_conversations_shape CHECK (
        (type = 'Community' AND community_id IS NOT NULL AND direct_key IS NULL) OR
        (type = 'Direct'    AND community_id IS NULL     AND direct_key IS NOT NULL))
);
CREATE UNIQUE INDEX ux_conversations_community ON conversations (community_id) WHERE community_id IS NOT NULL;
CREATE UNIQUE INDEX ux_conversations_direct_key ON conversations (direct_key) WHERE direct_key IS NOT NULL;

CREATE TABLE conversation_members (
    conversation_id  uuid NOT NULL REFERENCES conversations (id),
    user_id          uuid NOT NULL REFERENCES users (id),
    joined_at        timestamptz NOT NULL,
    last_read_at     timestamptz NULL,
    status           text NOT NULL CHECK (status IN ('Active','Removed')),
    PRIMARY KEY (conversation_id, user_id)
);
CREATE INDEX ix_conversation_members_user_status ON conversation_members (user_id, status);

CREATE TABLE messages (
    id                  uuid PRIMARY KEY,
    conversation_id     uuid NOT NULL REFERENCES conversations (id),
    sender_user_id      uuid NOT NULL REFERENCES users (id),
    content             text NOT NULL,
    created_at          timestamptz NOT NULL,
    edited_at           timestamptz NULL,
    deleted_at          timestamptz NULL,
    deleted_by_user_id  uuid NULL REFERENCES users (id),
    status              text NOT NULL CHECK (status IN ('Active','Deleted'))
);
-- Keyset pagination: newest first within a conversation.
CREATE INDEX ix_messages_conversation_chronological ON messages (conversation_id, created_at DESC, id DESC);

-- No foreign keys: audit rows must outlive the rows they describe.
CREATE TABLE audit_log (
    id              uuid PRIMARY KEY,
    at              timestamptz NOT NULL,
    actor_user_id   uuid NULL,
    action          varchar(64) NOT NULL,
    community_id    uuid NULL,
    target_user_id  uuid NULL,
    details         text NULL
);
CREATE INDEX ix_audit_log_community_at ON audit_log (community_id, at DESC);
CREATE INDEX ix_audit_log_at ON audit_log (at DESC);

CREATE TABLE nostr_challenges (
    value             varchar(64) PRIMARY KEY,
    purpose           text NOT NULL CHECK (purpose IN ('Login','Link')),
    bound_public_key  varchar(64) NULL,
    user_id           uuid NULL,
    created_at        timestamptz NOT NULL,
    expires_at        timestamptz NOT NULL,
    consumed_at       timestamptz NULL
);
CREATE INDEX ix_nostr_challenges_expires_at ON nostr_challenges (expires_at);
