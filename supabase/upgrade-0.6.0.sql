-- Run AFTER setup.sql on the existing project. Keeps members/history intact.
-- Disables plaintext publishing/downloading through the client API.
begin;
alter table public.drivelink_config_versions add column if not exists payload_format text not null default 'legacy';
alter table public.drivelink_config_versions drop constraint if exists drivelink_config_versions_config_json_check;
alter table public.drivelink_config_versions add constraint drivelink_config_versions_config_json_check check (octet_length(config_json) <= 100000);
grant select (payload_format) on public.drivelink_config_versions to authenticated;

create or replace function public.drivelink_config_status()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare result jsonb;
begin
  if not exists (select 1 from public.drivelink_members where user_id = auth.uid() and role in ('admin','reader')) then
    raise exception 'Membership required' using errcode = '42501';
  end if;
  select jsonb_build_object('id', v.id, 'label', v.label, 'client_id', v.client_id, 'sha256', v.sha256, 'created_at', v.created_at, 'payload_format', v.payload_format)
    into result from public.drivelink_config_current c join public.drivelink_config_versions v on v.id = c.version_id where c.singleton;
  return result;
end $$;

create or replace function public.drivelink_download_config()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare result jsonb; chosen public.drivelink_config_versions;
begin
  if not exists (select 1 from public.drivelink_members where user_id = auth.uid() and role in ('admin','reader')) then
    raise exception 'Membership required' using errcode = '42501';
  end if;
  select v.* into chosen from public.drivelink_config_current c join public.drivelink_config_versions v on v.id = c.version_id where c.singleton;
  if not found then return null; end if;
  if chosen.payload_format <> 'aes256gcm-pbkdf2-v1' then
    raise exception 'Admin must republish a password-encrypted configuration' using errcode = '22023';
  end if;
  return jsonb_build_object('id', chosen.id, 'label', chosen.label, 'client_id', chosen.client_id, 'sha256', chosen.sha256, 'created_at', chosen.created_at, 'payload_format', chosen.payload_format, 'config_json', chosen.config_json);
end $$;

create or replace function public.drivelink_publish_locked_config(p_label text, p_client_id text, p_envelope text, p_expected_current_id uuid)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare parsed jsonb; active_id uuid; created public.drivelink_config_versions;
begin
  if not exists (select 1 from public.drivelink_members where user_id = auth.uid() and role = 'admin') then
    raise exception 'Admin required' using errcode = '42501';
  end if;
  if p_label is null or length(btrim(p_label)) not between 1 and 80 or p_label ~ '[[:cntrl:]]'
    or p_client_id is null or length(p_client_id) > 512 or p_client_id !~ '^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$'
    or p_envelope is null or octet_length(p_envelope) > 100000 then
    raise exception 'Invalid configuration metadata' using errcode = '22023';
  end if;
  begin
    parsed := p_envelope::jsonb;
    if jsonb_typeof(parsed) is distinct from 'object'
       or parsed ->> 'Version' is distinct from '1' or parsed ->> 'Iterations' is distinct from '600000'
       or jsonb_typeof(parsed -> 'Salt') is distinct from 'string' or jsonb_typeof(parsed -> 'Nonce') is distinct from 'string'
       or jsonb_typeof(parsed -> 'Tag') is distinct from 'string' or jsonb_typeof(parsed -> 'Ciphertext') is distinct from 'string'
       or octet_length(decode(parsed ->> 'Salt', 'base64')) is distinct from 16
       or octet_length(decode(parsed ->> 'Nonce', 'base64')) is distinct from 12
       or octet_length(decode(parsed ->> 'Tag', 'base64')) is distinct from 16
       or octet_length(decode(parsed ->> 'Ciphertext', 'base64')) not between 1 and 65536 then
      raise exception 'Invalid envelope';
    end if;
    if (parsed - array['Version','Iterations','Salt','Nonce','Tag','Ciphertext']) <> '{}'::jsonb then raise exception 'Unexpected fields'; end if;
  exception when others then raise exception 'Invalid encrypted envelope' using errcode = '22023'; end;
  perform pg_advisory_xact_lock(68204105);
  select version_id into active_id from public.drivelink_config_current where singleton;
  if active_id is distinct from p_expected_current_id then raise exception 'Refresh first' using errcode = '40001'; end if;
  insert into public.drivelink_config_versions(label, client_id, config_json, sha256, created_by, payload_format)
    values (btrim(p_label), p_client_id, p_envelope, encode(sha256(convert_to(p_envelope, 'UTF8')), 'hex'), auth.uid(), 'aes256gcm-pbkdf2-v1') returning * into created;
  insert into public.drivelink_config_current(singleton, version_id) values (true, created.id)
    on conflict (singleton) do update set version_id = excluded.version_id, updated_at = now();
  return jsonb_build_object('id', created.id, 'label', created.label, 'client_id', created.client_id, 'sha256', created.sha256, 'created_at', created.created_at, 'payload_format', created.payload_format);
end $$;

create or replace function public.drivelink_activate_config(p_version_id uuid, p_expected_current_id uuid)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare active_id uuid; chosen public.drivelink_config_versions;
begin
  if not exists (select 1 from public.drivelink_members where user_id = auth.uid() and role = 'admin') then
    raise exception 'Admin required' using errcode = '42501';
  end if;
  perform pg_advisory_xact_lock(68204105);
  select version_id into active_id from public.drivelink_config_current where singleton;
  if active_id is distinct from p_expected_current_id then raise exception 'Refresh first' using errcode = '40001'; end if;
  select * into chosen from public.drivelink_config_versions where id = p_version_id;
  if not found or chosen.payload_format <> 'aes256gcm-pbkdf2-v1' then
    raise exception 'Choose an encrypted version or republish legacy JSON' using errcode = '22023';
  end if;
  insert into public.drivelink_config_current(singleton, version_id) values (true, chosen.id)
    on conflict (singleton) do update set version_id = excluded.version_id, updated_at = now();
  return jsonb_build_object('id', chosen.id, 'label', chosen.label, 'client_id', chosen.client_id, 'sha256', chosen.sha256, 'created_at', chosen.created_at, 'payload_format', chosen.payload_format);
end $$;

revoke all on function public.drivelink_publish_config(text,text,uuid) from public, anon, authenticated;
revoke all on function public.drivelink_publish_locked_config(text,text,text,uuid) from public, anon, authenticated;
revoke all on function public.drivelink_config_status() from public, anon;
revoke all on function public.drivelink_download_config() from public, anon;
revoke all on function public.drivelink_activate_config(uuid,uuid) from public, anon;
grant execute on function public.drivelink_publish_locked_config(text,text,text,uuid) to authenticated;
grant execute on function public.drivelink_config_status() to authenticated;
grant execute on function public.drivelink_download_config() to authenticated;
grant execute on function public.drivelink_activate_config(uuid,uuid) to authenticated;
notify pgrst, 'reload schema';
commit;
