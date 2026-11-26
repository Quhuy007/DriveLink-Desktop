# Giữ Supabase miễn phí không bị tạm dừng

Supabase gói Free tạm dừng (pause) một project nếu project có rất ít hoạt động cơ sở dữ liệu trong khoảng 7 ngày. Worker `drivelink-supabase-keepalive` trên Cloudflare chạy theo lịch, **mỗi 2 ngày lúc 03:17 UTC (10:17 giờ Việt Nam)**. Mỗi lần chạy, nó gọi hàm `public.drivelink_keepalive()` một lần.

- Hàm này chỉ ghi giờ của lần gọi gần nhất vào bảng một dòng `drivelink_heartbeat`. Nó không đọc và không sửa dữ liệu nào khác của DriveLink.
- Worker dùng khóa `sb_publishable_` công khai, cùng loại với khóa trong app, và khóa này được lưu dạng secret trên Cloudflare. Không bao giờ dùng secret key hay service_role.
- Worker không có địa chỉ web công khai (`workersDev: false`); nó chỉ chạy theo lịch.
- Nếu lỗi, Worker thử lại tối đa 3 lần. Sau 3 lần vẫn lỗi, lần chạy đó được đánh dấu thất bại trong Cloudflare.

## Cài một lần

1. Trong Supabase → **SQL Editor**, chạy [`supabase/keepalive.sql`](../../supabase/keepalive.sql). Chạy lại nhiều lần cũng an toàn.
2. Worker đã được triển khai lên tài khoản Cloudflare. Chỉ cần triển khai lại khi sửa mã (xem bên dưới).

## Kiểm tra

- **Cloudflare:** Workers & Pages → `drivelink-supabase-keepalive` → **Logs**. Có thể bấm vào lịch trong **Settings → Trigger Events** để xem các lần chạy.
- **Supabase:** chạy `select last_ping, pings from public.drivelink_heartbeat;`. `last_ping` không được cũ hơn 2–3 ngày.
- **Nếu project đã bị tạm dừng:** khôi phục bằng tay một lần trong trang quản lý Supabase. Worker không đánh thức được project đang bị dừng.

## Triển khai lại (sau khi sửa mã)

Dự án dùng `cf` CLI. Các phiên bản được ghim chính xác trong `package.json`, chọn các bản đã phát hành ít nhất 2 tuần.

```powershell
cd cloud/supabase-keepalive
npm ci
npx cf deploy --dry-run
```

Khóa `sb_publishable_` được tải lên kèm phiên bản qua `--secrets-file`. Tệp này dạng JSON `{"SUPABASE_PUBLISHABLE_KEY": "sb_publishable_…"}`. Tạo nó **ngoài** thư mục dự án và xóa sau khi triển khai:

```powershell
npx cf deploy --secrets-file $env:TEMP\keepalive.secrets.json
Remove-Item $env:TEMP\keepalive.secrets.json
```

Gỡ Worker: `npx cf workers delete drivelink-supabase-keepalive`. Không cần thì xóa luôn bảng và hàm bằng `drop function public.drivelink_keepalive(); drop table public.drivelink_heartbeat;`.

Nếu nâng lên gói Supabase Pro thì project không bị tạm dừng nữa, và có thể gỡ Worker này.
