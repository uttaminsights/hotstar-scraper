-- Hotstar scraper database (MySQL 8 / MariaDB 10.6+ / TiDB).
-- The scraper runs this file at startup, so every statement must be safe to repeat.
-- All times are UTC. List columns (genres, cast_members, ...) are JSON arrays of strings.
--
--   tv_shows 1 ─── * seasons 1 ─── * episodes
--   movies / tv_shows 1 ─── * promotions   (one row per title, per section, per run)

CREATE TABLE IF NOT EXISTS movies (
  content_id       VARCHAR(20)   NOT NULL,
  title            VARCHAR(300)  NOT NULL,
  description      TEXT          NULL,
  release_year     SMALLINT      NULL,
  age_rating       VARCHAR(20)   NULL,
  runtime_minutes  SMALLINT      NULL,
  imdb_rating      DECIMAL(3,1)  NULL,
  genres           JSON          NULL,
  languages        JSON          NULL,
  directors        JSON          NULL,
  producers        JSON          NULL,
  cast_members     JSON          NULL,
  callouts         JSON          NULL,
  url              VARCHAR(500)  NOT NULL,
  image_url        VARCHAR(500)  NULL,
  title_image_url  VARCHAR(500)  NULL,
  first_seen_at    DATETIME      NOT NULL,
  last_scraped_at  DATETIME      NOT NULL,
  PRIMARY KEY (content_id),
  KEY ix_movies_title (title(100)),
  KEY ix_movies_year (release_year)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS tv_shows (
  content_id       VARCHAR(20)   NOT NULL,
  title            VARCHAR(300)  NOT NULL,
  description      TEXT          NULL,
  release_year     SMALLINT      NULL,
  age_rating       VARCHAR(20)   NULL,
  imdb_rating      DECIMAL(3,1)  NULL,
  season_count     SMALLINT      NOT NULL DEFAULT 0,
  episode_count    INT           NULL,
  genres           JSON          NULL,
  languages        JSON          NULL,
  creators         JSON          NULL,
  directors        JSON          NULL,
  producers        JSON          NULL,
  cast_members     JSON          NULL,
  callouts         JSON          NULL,
  url              VARCHAR(500)  NOT NULL,
  image_url        VARCHAR(500)  NULL,
  title_image_url  VARCHAR(500)  NULL,
  first_seen_at    DATETIME      NOT NULL,
  last_scraped_at  DATETIME      NOT NULL,
  PRIMARY KEY (content_id),
  KEY ix_tv_shows_title (title(100))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS seasons (
  season_id        VARCHAR(30)   NOT NULL,
  show_id          VARCHAR(20)   NOT NULL,
  position         SMALLINT      NOT NULL,
  season_number    SMALLINT      NULL,
  title            VARCHAR(100)  NOT NULL,
  episode_count    INT           NULL COMMENT 'count shown by the site on the season tab',
  first_seen_at    DATETIME      NOT NULL,
  last_scraped_at  DATETIME      NOT NULL,
  PRIMARY KEY (season_id),
  KEY ix_seasons_show (show_id, position),
  CONSTRAINT fk_seasons_show FOREIGN KEY (show_id) REFERENCES tv_shows (content_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS episodes (
  episode_id       VARCHAR(20)   NOT NULL,
  show_id          VARCHAR(20)   NOT NULL,
  season_id        VARCHAR(30)   NOT NULL,
  season_number    SMALLINT      NULL,
  episode_number   INT           NULL,
  title            VARCHAR(500)  NOT NULL,
  description      TEXT          NULL,
  air_date         DATE          NULL,
  runtime_minutes  SMALLINT      NULL,
  url              VARCHAR(500)  NULL,
  image_url        VARCHAR(500)  NULL,
  first_seen_at    DATETIME      NOT NULL,
  last_scraped_at  DATETIME      NOT NULL,
  PRIMARY KEY (episode_id),
  KEY ix_episodes_season (season_id, episode_number),
  KEY ix_episodes_show (show_id),
  KEY ix_episodes_air_date (air_date),
  CONSTRAINT fk_episodes_show FOREIGN KEY (show_id) REFERENCES tv_shows (content_id) ON DELETE CASCADE,
  CONSTRAINT fk_episodes_season FOREIGN KEY (season_id) REFERENCES seasons (season_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS promotions (
  id               BIGINT        NOT NULL AUTO_INCREMENT,
  run_id           VARCHAR(20)   NOT NULL COMMENT 'yyyyMMdd-HHmmss (UTC) of the scrape run',
  scraped_at       DATETIME      NOT NULL,
  section_type     ENUM('hero','row') NOT NULL,
  section_title    VARCHAR(200)  NOT NULL COMMENT '"Hero Banner" or the row title',
  section_position SMALLINT      NOT NULL COMMENT '0 = hero banners, then rows top to bottom',
  position         SMALLINT      NOT NULL COMMENT '1-based position inside the section',
  on_home          TINYINT(1)    NOT NULL COMMENT '0 = only reachable through View All',
  content_type     ENUM('movie','show','episode','other') NOT NULL,
  content_id       VARCHAR(20)   NULL,
  movie_id         VARCHAR(20)   NULL,
  show_id          VARCHAR(20)   NULL COMMENT 'the show, also for episode cards',
  title            VARCHAR(500)  NOT NULL,
  description      TEXT          NULL,
  tags             JSON          NULL,
  callouts         JSON          NULL,
  url              VARCHAR(500)  NULL,
  image_url        VARCHAR(500)  NULL,
  PRIMARY KEY (id),
  UNIQUE KEY ux_promotions_slot (run_id, section_position, position),
  KEY ix_promotions_scraped (scraped_at),
  KEY ix_promotions_content (content_id),
  CONSTRAINT fk_promotions_movie FOREIGN KEY (movie_id) REFERENCES movies (content_id) ON DELETE SET NULL,
  CONSTRAINT fk_promotions_show FOREIGN KEY (show_id) REFERENCES tv_shows (content_id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
