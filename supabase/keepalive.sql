-- DriveLink: giữ project Supabase miễn phí không bị tạm dừng (pause) vì ít hoạt động.
-- Chạy một lần trong Supabase → SQL Editor. Chạy lại nhiều lần cũng an toàn.
-- Cloudflare Worker "drivelink-supabase-keepalive" gọi hàm public.drivelink_keepalive() mỗi 2 ngày.
-- Hàm chỉ ghi giờ của lần gọi gần nhất vào một bảng một dòng; không đọc hay sửa dữ liệu nào khác.

create table if not exists public.drivelink_heartbeat (
  id smallint primary key default 1 check (id = 1),
  last_ping timestamptz not null default now(),
  pings bigint not null default 0
);
alter table public.drivelink_heartbeat enable row level security;
-- Không có policy nào: chỉ hàm bên dưới (security definer) ghi được, không ai đọc/sửa trực tiếp qua API.
revoke all on public.drivelink_heartbeat from anon, authenticated;

create or replace function public.drivelink_keepalive()
returns timestamptz
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
  stamp timestamptz;
begin
  insert into public.drivelink_heartbeat as h (id, last_ping, pings)
  values (1, now(), 1)
  on conflict (id) do update set last_ping = now(), pings = h.pings + 1
  returning h.last_ping into stamp;
  return stamp;
end;
$$;

revoke all on function public.drivelink_keepalive() from public;
grant execute on function public.drivelink_keepalive() to anon, authenticated;

-- Xem lần gọi gần nhất:
-- select last_ping, pings from public.drivelink_heartbeat;
