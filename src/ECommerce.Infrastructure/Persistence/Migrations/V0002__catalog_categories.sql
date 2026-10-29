CREATE TABLE catalog.categories (
    id               uuid PRIMARY KEY,
    name             text NOT NULL UNIQUE,
    slug             text NOT NULL UNIQUE,
    description      text NOT NULL DEFAULT '',     -- texto indexável (SEO-10)
    seo_title        text NULL,
    seo_description  text NULL,
    is_active        boolean NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);
