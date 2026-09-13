-- Initial catalogue schema. Migrations are versioned and never edited once applied.
CREATE TABLE products (
    id             VARCHAR(36)    NOT NULL,
    sku            VARCHAR(64)    NOT NULL,
    name           VARCHAR(200)   NOT NULL,
    description    VARCHAR(2000),
    price          NUMERIC(12, 2) NOT NULL,
    currency       CHAR(3)        NOT NULL,
    stock_quantity INTEGER        NOT NULL,
    active         BOOLEAN        NOT NULL DEFAULT TRUE,
    created_at     TIMESTAMPTZ    NOT NULL,
    updated_at     TIMESTAMPTZ    NOT NULL,
    version        BIGINT         NOT NULL DEFAULT 0,
    CONSTRAINT pk_products PRIMARY KEY (id),
    CONSTRAINT uq_products_sku UNIQUE (sku),
    CONSTRAINT ck_products_price_non_negative CHECK (price >= 0),
    CONSTRAINT ck_products_stock_non_negative CHECK (stock_quantity >= 0)
);

-- The catalogue is read far more often than written; these cover the common queries.
CREATE INDEX idx_products_active ON products (active);
CREATE INDEX idx_products_name_lower ON products (LOWER(name));
