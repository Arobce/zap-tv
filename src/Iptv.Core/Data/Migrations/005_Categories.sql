-- Migration 005 - provider category names.
--
-- streams.category_id has been stored since the first schema, but only the id. A provider
-- lists 118,763 streams across hundreds of categories, and a list you can only search is
-- not navigable: you have to already know the name of the thing you are looking for.
--
-- Kept per provider and per kind, because both are part of the identity. Two providers
-- reuse the same numeric ids for different things, and one provider's live category "1"
-- has nothing to do with its VOD category "1".

CREATE TABLE categories (
  provider_id INTEGER NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  kind        TEXT    NOT NULL CHECK (kind IN ('live', 'vod', 'series')),
  category_id TEXT    NOT NULL,
  name        TEXT    NOT NULL,
  -- Xtream nests categories one level. Kept so a later UI can group, and ignored for now.
  parent_id   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (provider_id, kind, category_id)
) STRICT;

-- The browse query joins streams to this by (provider, kind, category) and orders by name.
CREATE INDEX ix_categories_name ON categories(kind, name);
