-- Keep OAuth versions when an old Auth account is deleted via Dashboard.
-- This changes the relationship only; it does NOT delete any account or version.
begin;
alter table public.drivelink_config_versions alter column created_by drop not null;
alter table public.drivelink_config_versions drop constraint if exists drivelink_config_versions_created_by_fkey;
alter table public.drivelink_config_versions add constraint drivelink_config_versions_created_by_fkey
  foreign key (created_by) references auth.users(id) on delete set null;
commit;
