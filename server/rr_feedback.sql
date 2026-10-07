-- Raid Replay feedback reports.
--
-- The plugin POSTs JSON to https://supabase.gofigstudios.com/rr/feedback. nginx rate-limits that path, adds the anon
-- key and "Prefer: return=minimal", and forwards it to PostgREST as an insert into public.rr_feedback. The plugin
-- ships no key.
--
-- The anon role may insert the client columns and nothing else: it can't read, update or delete rows, or set the
-- server columns (id, received_at, client_ip, status). Logged-in users (authenticated) have no access at all.
-- Read reports in Studio, or as the table owner / service_role.
--
-- Each new report is emailed to SMTP_ADMIN_EMAIL by the rr-feedback-mail Edge Function (functions/rr-feedback-mail):
-- the insert trigger calls it through pg_net, and a pg_cron job retries reports that weren't sent (up to 5 tries).
--
-- Apply as the database owner:  sudo -u postgres psql -d supabase -f rr_feedback.sql

begin;

create table if not exists public.rr_feedback (
    id             bigint generated always as identity primary key,
    received_at    timestamptz not null default now(),
    client_ip      inet,
    status         text not null default 'new' check (status in ('new', 'triaged', 'fixed', 'wontfix')),
    emailed_at     timestamptz,
    email_claimed_at timestamptz,
    email_attempts int not null default 0,
    email_error    text,

    -- Sent by the plugin.
    install_id     uuid,
    plugin_version text not null check (length(plugin_version) <= 32),
    pack           text check (length(pack) <= 64),
    category       text not null check (category in ('wrong_culprit', 'wrong_root_cause', 'missed_mistake', 'wrong_spot', 'other')),
    note           text not null default '' check (length(note) <= 4000),
    pull           jsonb not null default '{}' check (pg_column_size(pull) <= 16384),
    incident       jsonb check (pg_column_size(incident) <= 65536),
    report         jsonb check (pg_column_size(report) <= 1048576),
    log_gz_base64  text check (length(log_gz_base64) <= 7340032),
    constraint rr_feedback_log_is_gzip check (log_gz_base64 is null or log_gz_base64 like 'H4sI%')
);

-- Tables created before these constraints existed get them here.
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'rr_feedback_log_is_gzip') then
        alter table public.rr_feedback
            add constraint rr_feedback_log_is_gzip check (log_gz_base64 is null or log_gz_base64 like 'H4sI%');
    end if;
end;
$$;

comment on table public.rr_feedback is 'Raid Replay feedback reports from the plugin (insert-only for anon via nginx /rr/feedback).';
comment on column public.rr_feedback.pull is 'Pull identity: zone, ordinal, start, duration, outcome, phase.';
comment on column public.rr_feedback.incident is 'The incident the report was opened from (kind, time, title, culprits by slot).';
comment on column public.rr_feedback.report is 'The whole wipe report as data, names replaced by party slots.';
comment on column public.rr_feedback.log_gz_base64 is 'Base64 of the gzipped, sanitized pull log (decode(log_gz_base64, ''base64'')).';

create index if not exists rr_feedback_new on public.rr_feedback (received_at) where status = 'new';
create index if not exists rr_feedback_by_ip on public.rr_feedback (client_ip, received_at);

-- Server-side stamps and limits. Runs as the table owner: the inserting role (anon) can't read the table.
--  * client_ip: the rightmost public address in X-Forwarded-For. Each proxy appends the address it received from, so
--    entries left of the one nginx added are whatever the client sent; private addresses are our own proxies (nginx,
--    Kong). If a CDN is ever put in front of nginx, nginx needs real_ip settings for this to stay the client.
--  * at most 20 reports per address per hour; more get HTTP 429 (PostgREST maps SQLSTATE PT429), which the plugin
--    keeps and retries later.
create or replace function public.rr_feedback_stamp() returns trigger
    language plpgsql
    security definer
    set search_path = ''
as $$
declare
    headers json := nullif(current_setting('request.headers', true), '')::json;
    hop text;
    ip inet;
begin
    new.received_at := now();
    new.status := 'new';
    new.client_ip := null;
    for hop in select trim(h) from unnest(string_to_array(coalesce(headers ->> 'x-forwarded-for', ''), ',')) with ordinality as x(h, n)
               order by n desc
    loop
        begin
            ip := hop::inet;
        exception when others then
            ip := null;
        end;
        if ip is not null and not (ip << any (array['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8',
                                                    '::1/128', 'fc00::/7', 'fe80::/10']::inet[])) then
            new.client_ip := ip;
            exit;
        end if;
    end loop;

    if new.client_ip is not null and (select count(*) from public.rr_feedback
                                      where client_ip = new.client_ip and received_at > now() - interval '1 hour') >= 20 then
        raise sqlstate 'PT429' using message = 'Too many reports from this address; it will be sent later.';
    end if;
    return new;
end;
$$;

revoke all on function public.rr_feedback_stamp() from public, anon, authenticated;

drop trigger if exists rr_feedback_stamp on public.rr_feedback;
create trigger rr_feedback_stamp before insert on public.rr_feedback
    for each row execute function public.rr_feedback_stamp();

-- One report per request: PostgREST also takes a JSON array, which would insert (and email) many rows at once and get
-- around nginx's per-request rate limit.
create or replace function public.rr_feedback_one_per_request() returns trigger
    language plpgsql
    set search_path = ''
as $$
begin
    if (select count(*) from new_rows) > 1 then
        raise exception 'Send one report per request.' using errcode = 'check_violation';
    end if;
    return null;
end;
$$;

revoke all on function public.rr_feedback_one_per_request() from public, anon, authenticated;

drop trigger if exists rr_feedback_one_per_request on public.rr_feedback;
create trigger rr_feedback_one_per_request after insert on public.rr_feedback
    referencing new table as new_rows
    for each statement execute function public.rr_feedback_one_per_request();

-- Permissions: Supabase's default privileges grant everything on new public tables to anon and authenticated; take
-- that back, then allow anon to insert the client columns only.
alter table public.rr_feedback enable row level security;
revoke all on public.rr_feedback from anon, authenticated;
grant insert (install_id, plugin_version, pack, category, note, pull, incident, report, log_gz_base64)
    on public.rr_feedback to anon;

drop policy if exists rr_feedback_insert on public.rr_feedback;
create policy rr_feedback_insert on public.rr_feedback for insert to anon with check (true);

-- ---- email ----------------------------------------------------------------------------------------------------

-- The Edge Function's address, through Kong on this host (not exposed by nginx).
create or replace function public.rr_feedback_mail_url() returns text
    language sql immutable
    set search_path = ''
as $$ select 'http://127.0.0.1:8001/functions/v1/rr-feedback-mail' $$;

-- Ask the Edge Function to email one report. Runs as its owner: the inserting role (anon) can't use pg_net.
create or replace function public.rr_feedback_request_mail(p_id bigint) returns void
    language sql
    security definer
    set search_path = ''
as $$
    select net.http_post(url := public.rr_feedback_mail_url(), body := jsonb_build_object('id', p_id),
                         headers := '{"Content-Type": "application/json"}'::jsonb, timeout_milliseconds := 30000);
$$;

create or replace function public.rr_feedback_after_insert() returns trigger
    language plpgsql
    security definer
    set search_path = ''
as $$
begin
    perform public.rr_feedback_request_mail(new.id);
    return null;
end;
$$;

drop trigger if exists rr_feedback_mail on public.rr_feedback;
create trigger rr_feedback_mail after insert on public.rr_feedback
    for each row execute function public.rr_feedback_after_insert();

-- Called by the Edge Function (service_role) to take a report for sending, so the trigger and the retry job never
-- send the same report twice. Returns nothing when it's already sent, being sent, or out of tries.
create or replace function public.rr_feedback_claim(p_id bigint) returns setof public.rr_feedback
    language sql
    security definer
    set search_path = ''
as $$
    update public.rr_feedback
       set email_claimed_at = now(), email_attempts = email_attempts + 1
     where id = p_id and emailed_at is null and email_attempts < 5
       and (email_claimed_at is null or email_claimed_at < now() - interval '10 minutes')
    returning *;
$$;

-- Called by the Edge Function after trying: marks the report sent, or records why it wasn't.
create or replace function public.rr_feedback_mailed(p_id bigint, p_error text default null) returns void
    language sql
    security definer
    set search_path = ''
as $$
    update public.rr_feedback
       set emailed_at = case when p_error is null then now() end,
           email_claimed_at = case when p_error is null then email_claimed_at end,
           email_error = p_error
     where id = p_id;
$$;

revoke all on function public.rr_feedback_mail_url(), public.rr_feedback_request_mail(bigint), public.rr_feedback_after_insert(),
    public.rr_feedback_claim(bigint), public.rr_feedback_mailed(bigint, text) from public, anon, authenticated;
grant execute on function public.rr_feedback_claim(bigint), public.rr_feedback_mailed(bigint, text) to service_role;

-- Retry reports the trigger's call didn't get out (SMTP or the function down): every 5 minutes, up to 5 tries each.
select cron.schedule('rr-feedback-mail-retry', '*/5 * * * *', $$
    select public.rr_feedback_request_mail(id)
      from public.rr_feedback
     where emailed_at is null and email_attempts < 5 and received_at < now() - interval '2 minutes'
       and (email_claimed_at is null or email_claimed_at < now() - interval '10 minutes')
     order by id
     limit 10
$$);

commit;

-- Let PostgREST see the new table.
notify pgrst, 'reload schema';
