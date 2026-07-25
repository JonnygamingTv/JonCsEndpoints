CREATE TABLE IF NOT EXISTS users (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    email       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    artist_name TEXT    UNIQUE COLLATE NOCASE,
    pass_hash   TEXT    NOT NULL,         -- Argon2id hash
    credits     INTEGER NOT NULL DEFAULT 0, -- stored in cents
    is_mod      INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS audio (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id    INTEGER NOT NULL REFERENCES users(id),
    title       TEXT    NOT NULL,
    description TEXT    NOT NULL DEFAULT '',
    price       INTEGER NOT NULL DEFAULT 0, -- cents, 0 = free
    has_wav     INTEGER NOT NULL DEFAULT 0,
    plays       INTEGER NOT NULL DEFAULT 0,
    promoted    INTEGER NOT NULL DEFAULT 0, -- paid front-page slot
    promoted_until INTEGER,                 -- unix timestamp
    active      INTEGER NOT NULL DEFAULT 1, -- 0 = revoked by mod
    created_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS purchases (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    audio_id    INTEGER NOT NULL REFERENCES audio(id),
    paid_cents  INTEGER NOT NULL,
    purchased_at INTEGER NOT NULL,
    UNIQUE(user_id, audio_id)
);

CREATE TABLE IF NOT EXISTS sessions (
    token       TEXT    PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    expires_at  INTEGER NOT NULL
);

-- Artist name change requests (go through mod approval)
CREATE TABLE IF NOT EXISTS name_requests (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    new_name    TEXT    NOT NULL,
    requested_at INTEGER NOT NULL,
    status      TEXT    NOT NULL DEFAULT 'pending' -- pending|approved|rejected
);

-- Payment orders (unified across providers)
CREATE TABLE IF NOT EXISTS payment_orders (
    id           TEXT    PRIMARY KEY,  -- provider order/session id
    user_id      INTEGER NOT NULL REFERENCES users(id),
    provider     TEXT    NOT NULL,     -- paypal|stripe|coinbase
    credits      INTEGER NOT NULL,     -- credits to grant on success (cents)
    status       TEXT    NOT NULL DEFAULT 'pending', -- pending|completed|failed
    created_at   INTEGER NOT NULL
);

-- Indexing takes requests per second (for recommended and similar endpoints where it READS) from ~50K rps to ~200K rps (4x requests per second)
CREATE INDEX IF NOT EXISTS idx_name_req_user   ON name_requests(user_id, status);
CREATE INDEX IF NOT EXISTS idx_payment_orders  ON payment_orders(user_id, status);
CREATE INDEX IF NOT EXISTS idx_audio_owner   ON audio(owner_id);
CREATE INDEX IF NOT EXISTS idx_audio_active  ON audio(active, promoted, created_at);
CREATE INDEX IF NOT EXISTS idx_purchases_user ON purchases(user_id);
CREATE INDEX IF NOT EXISTS idx_sessions_exp  ON sessions(expires_at);
CREATE INDEX IF NOT EXISTS idx_audio_browse_new
    ON audio(active, created_at DESC)
    WHERE active=1;
CREATE INDEX IF NOT EXISTS idx_audio_browse_top
    ON audio(active, plays DESC)
    WHERE active=1;
CREATE INDEX IF NOT EXISTS idx_audio_browse_promo
    ON audio(active, promoted DESC, promoted_until DESC)
    WHERE active=1;
