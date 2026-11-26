-- DriveLink 1.2.0: khóa đăng nhập quản trị theo địa chỉ IP sau nhiều lần nhập sai mật khẩu.
-- Chạy một lần trong SQL Editor của project Supabase hiện có, bằng tài khoản chủ project.
-- Quy tắc (khớp với app): mỗi 3 lần sai phải chờ 15 phút; tổng cộng 9 lần sai thì địa chỉ IP bị chặn hẳn.
-- Địa chỉ bị chặn không đăng/chọn được cấu hình OAuth kể cả khi có đúng mật khẩu, và không đọc được vai trò quản trị.
-- Mở chặn thủ công: delete from public.drivelink_admin_guard where client_ip = '<địa chỉ>';
-- Không chứa token Google, mật khẩu hay khóa bí mật.
begin;

create table if not exists public.drivelink_admin_guard (
  client_ip text primary key,
  failures integer not null default 0,
  cooldown_until timestamptz,
  blocked boolean not null default false,
  updated_at timestamptz not null default now()
);
alter table public.drivelink_admin_guard enable row level security;
revoke all on public.drivelink_admin_guard from public, anon, authenticated;

-- Địa chỉ của người gọi do Supabase ghi trong header (địa chỉ đầu tiên của x-forwarded-for).
create or replace function public.drivelink_request_ip()
returns text language sql stable set search_path = '' as $$
  select coalesce(nullif(btrim(split_part(coalesce(current_setting('request.headers', true)::json ->> 'x-forwarded-for', ''), ',', 1)), ''), 'unknown');
$$;

create or replace function public.drivelink_ip_blocked()
returns boolean language sql stable security definer set search_path = '' as $$
  select coalesce((select g.blocked from public.drivelink_admin_guard g where g.client_ip = public.drivelink_request_ip()), false);
$$;

create or replace function public.drivelink_admin_gate()
returns jsonb language sql stable security definer set search_path = '' as $$
  select coalesce(
    (select jsonb_build_object('blocked', g.blocked, 'cooldown_until', g.cooldown_until, 'failures', g.failures)
       from public.drivelink_admin_guard g where g.client_ip = public.drivelink_request_ip()),
    jsonb_build_object('blocked', false, 'cooldown_until', null, 'failures', 0));
$$;

create or replace function public.drivelink_admin_failed()
returns jsonb language plpgsql security definer set search_path = '' as $$
declare guard public.drivelink_admin_guard;
begin
  insert into public.drivelink_admin_guard(client_ip, failures) values (public.drivelink_request_ip(), 1)
    on conflict (client_ip) do update set failures = public.drivelink_admin_guard.failures + 1, updated_at = now()
    returning * into guard;
  if guard.failures >= 9 then
    update public.drivelink_admin_guard set blocked = true, cooldown_until = null where client_ip = guard.client_ip returning * into guard;
  elsif guard.failures % 3 = 0 then
    update public.drivelink_admin_guard set cooldown_until = now() + interval '15 minutes' where client_ip = guard.client_ip returning * into guard;
  end if;
  return jsonb_build_object('blocked', guard.blocked, 'cooldown_until', guard.cooldown_until, 'failures', guard.failures);
end $$;

-- Chỉ quản trị viên đã đăng nhập mới đặt lại được bộ đếm của địa chỉ mình; địa chỉ đã bị chặn hẳn thì không.
create or replace function public.drivelink_admin_succeeded()
returns jsonb language plpgsql security definer set search_path = '' as $$
begin
  if not exists (select 1 from public.drivelink_members where user_id = auth.uid() and role = 'admin') then
    raise exception 'Admin required' using errcode = '42501';
  end if;
  delete from public.drivelink_admin_guard where client_ip = public.drivelink_request_ip() and not blocked;
  return public.drivelink_admin_gate();
end $$;

-- Địa chỉ bị chặn không còn đọc được vai trò của mình nên app không mở giao diện quản trị.
drop policy if exists drivelink_members_read_self on public.drivelink_members;
create policy drivelink_members_read_self on public.drivelink_members
  for select to authenticated using (user_id = (select auth.uid()) and not public.drivelink_ip_blocked());

create or replace function public.drivelink_publish_locked_config(p_label text, p_client_id text, p_envelope text, p_expected_current_id uuid)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare parsed jsonb; active_id uuid; created public.drivelink_config_versions;
begin
  if public.drivelink_ip_blocked() then raise exception 'Address blocked' using errcode = '42501'; end if;
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
  if public.drivelink_ip_blocked() then raise exception 'Address blocked' using errcode = '42501'; end if;
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

revoke all on function public.drivelink_request_ip() from public, anon, authenticated;
revoke all on function public.drivelink_ip_blocked() from public, anon, authenticated;
revoke all on function public.drivelink_admin_gate() from public, anon, authenticated;
revoke all on function public.drivelink_admin_failed() from public, anon, authenticated;
revoke all on function public.drivelink_admin_succeeded() from public, anon, authenticated;
grant execute on function public.drivelink_ip_blocked() to authenticated;
grant execute on function public.drivelink_admin_gate() to anon, authenticated;
grant execute on function public.drivelink_admin_failed() to anon, authenticated;
grant execute on function public.drivelink_admin_succeeded() to authenticated;
revoke all on function public.drivelink_publish_locked_config(text,text,text,uuid) from public, anon;
revoke all on function public.drivelink_activate_config(uuid,uuid) from public, anon;
grant execute on function public.drivelink_publish_locked_config(text,text,text,uuid) to authenticated;
grant execute on function public.drivelink_activate_config(uuid,uuid) to authenticated;
commit;
notify pgrst, 'reload schema';
