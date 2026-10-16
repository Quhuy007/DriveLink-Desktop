-- One-time setup AFTER creating these users in Supabase Authentication > Users.
-- Replace both emails. This is performed by the project owner in SQL Editor.
-- Desktop apps cannot grant themselves admin rights or add members.
begin;
do $$
declare admin_email text := 'REPLACE_WITH_YOUR_ADMIN_EMAIL';
        reader_email text := 'REPLACE_WITH_YOUR_READER_EMAIL';
        admin_id uuid; reader_id uuid;
begin
  select id into admin_id from auth.users where lower(email) = lower(admin_email);
  select id into reader_id from auth.users where lower(email) = lower(reader_email);
  if admin_id is null or reader_id is null or admin_id = reader_id then
    raise exception 'Create two distinct Supabase Auth users, then replace both email placeholders.';
  end if;
  insert into public.drivelink_members(user_id, role) values (admin_id, 'admin'), (reader_id, 'reader')
    on conflict (user_id) do update set role = excluded.role;
end $$;
commit;
