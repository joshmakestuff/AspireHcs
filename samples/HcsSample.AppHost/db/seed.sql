-- Seed data for the sample web page. The postgres image runs every script in
-- /docker-entrypoint-initdb.d once, on first start with an empty data directory —
-- before Aspire creates the appdb database the web app connects to, so the script
-- creates it and connects first.

create database appdb;

\connect appdb

create table notes (
    id         serial primary key,
    note       text not null,
    created_at timestamptz not null default now()
);

insert into notes (note) values
    ('Seeded from db/seed.sql on the container''s first start.'),
    ('Postgres runs as an ordinary Aspire Docker resource next to the HCS guests.'),
    ('The web app reads this table through the appdb connection string.');
