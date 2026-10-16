-- Requires setup.sql + upgrade-0.6.0.sql. Does not change accounts/roles/history.
-- Deliberate public distribution of ONLY the active password-encrypted package.
begin;
create or replace function public.drivelink_public_config_status()
returns jsonb language sql stable security definer set search_path = '' as $$
  select jsonb_build_object('id', v.id, 'label', v.label, 'client_id', v.client_id,
    'sha256', v.sha256, 'created_at', v.created_at, 'payload_format', v.payload_format)
  from public.drivelink_config_current c join public.drivelink_config_versions v on v.id = c.version_id
  where c.singleton and v.payload_format = 'aes256gcm-pbkdf2-v1';
$$;
create or replace function public.drivelink_public_download_config()
returns jsonb language sql stable security definer set search_path = '' as $$
  select jsonb_build_object('id', v.id, 'label', v.label, 'client_id', v.client_id,
    'sha256', v.sha256, 'created_at', v.created_at, 'payload_format', v.payload_format, 'config_json', v.config_json)
  from public.drivelink_config_current c join public.drivelink_config_versions v on v.id = c.version_id
  where c.singleton and v.payload_format = 'aes256gcm-pbkdf2-v1';
$$;
revoke all on function public.drivelink_public_config_status() from public, anon, authenticated;
revoke all on function public.drivelink_public_download_config() from public, anon, authenticated;
grant execute on function public.drivelink_public_config_status() to anon, authenticated;
grant execute on function public.drivelink_public_download_config() to anon, authenticated;
-- No table grants, role grants, history access or write functions are added.
notify pgrst, 'reload schema';
commit;
