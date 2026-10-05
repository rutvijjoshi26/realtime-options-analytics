-- SpiderRock-style options analytics: close snapshot schema.
--
-- Design notes:
--  * One row per contract per session. The natural key (session_date,
--    underlying, expiry, strike, right) is the primary key, so a re-run of the
--    close job upserts rather than duplicating.
--  * Analytics are stored as DOUBLE. DECIMAL would be wrong here: these are
--    computed model outputs, not currency amounts, and the extra precision
--    would imply an accuracy the model does not have. Strike stays DECIMAL
--    because it is an exact contract term.
--  * The covering index on (underlying, session_date) serves the dominant
--    historical query pattern, "give me the chain for this name on this day".

CREATE DATABASE IF NOT EXISTS spiderrock_analytics
    CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

USE spiderrock_analytics;

CREATE TABLE IF NOT EXISTS close_analytics (
    session_date      DATE          NOT NULL,
    underlying        VARCHAR(8)    NOT NULL,
    expiry            DATE          NOT NULL,
    strike            DECIMAL(12,4) NOT NULL,
    `right`           ENUM('C','P') NOT NULL,

    underlying_price  DOUBLE        NOT NULL,
    years_to_expiry   DOUBLE        NOT NULL,
    implied_vol       DOUBLE        NOT NULL,
    delta             DOUBLE        NOT NULL,
    gamma             DOUBLE        NOT NULL,
    vega              DOUBLE        NOT NULL,
    theta             DOUBLE        NOT NULL,
    rho               DOUBLE        NOT NULL,

    sequence_number   BIGINT        NOT NULL,
    captured_at       DATETIME(3)   NOT NULL,

    PRIMARY KEY (session_date, underlying, expiry, strike, `right`),
    KEY idx_underlying_session (underlying, session_date),
    KEY idx_expiry (expiry)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS close_surface (
    session_date     DATE          NOT NULL,
    underlying       VARCHAR(8)    NOT NULL,
    expiry           DATE          NOT NULL,

    years_to_expiry  DOUBLE        NOT NULL,
    forward          DOUBLE        NOT NULL,
    coef_a           DOUBLE        NOT NULL,
    coef_b           DOUBLE        NOT NULL,
    coef_c           DOUBLE        NOT NULL,
    point_count      INT           NOT NULL,
    rms_error        DOUBLE        NOT NULL,
    arbitrage_free   TINYINT(1)    NOT NULL,
    arbitrage_note   VARCHAR(255)  NULL,
    captured_at      DATETIME(3)   NOT NULL,

    PRIMARY KEY (session_date, underlying, expiry),
    KEY idx_surface_underlying (underlying, session_date)
) ENGINE=InnoDB;
