DriveLink 1.2.0 — Quang Huy

Bản bảo mật:

- Từ bản này, app chỉ cài bản cập nhật có chữ ký số của chủ ứng dụng. Kể cả khi tài khoản GitHub bị chiếm, bản cập nhật giả sẽ bị chặn và bản đang dùng được giữ nguyên. Có thể tự kiểm các tệp tải về bằng scripts/Verify-Release.ps1.
- Token Google được bảo vệ thêm bằng Windows Hello: mỗi lần mở app, Windows hỏi PIN, vân tay hoặc khuôn mặt một lần. Máy không có Windows Hello dùng một khóa riêng trên máy. Sao chép riêng tệp tài khoản sang máy khác không dùng được.
- Gửi chéo: nếu app bị tắt giữa chừng, quyền xem tạm còn sót sẽ được tự thu hồi ở lần mở sau.
- Mật khẩu quản trị Cloud: sai 3 lần phải chờ 15 phút, sai 9 lần thì máy bị khóa mở quản trị.
- Tệp đang chia sẻ công khai có biểu tượng quả cầu cam. Bật "Bất kỳ ai có liên kết" phải xác nhận rủi ro.
- App cảnh báo trước khi tải về hoặc mở thư mục của tệp chạy được (.exe, .bat, .ps1…) do người khác gửi vào không gian chung.
- Xóa tài khoản khỏi máy giờ thu hồi quyền tại Google trước. Cài đặt → Tài khoản Google có phần Bảo mật và nhật ký bảo mật (không ghi token).

Đang dùng bản đã cài bằng Setup: mở Kiểm tra cập nhật trong app để cài 1.2.0. Tài khoản và cài đặt được giữ lại. Sau khi nâng cấp, bản 1.1.x cũ không mở được tệp tài khoản mới nữa. Bản này cần Windows 10 1809 trở lên.

Quản trị viên Cloud: chạy supabase/upgrade-1.2.0.sql một lần trong Supabase để bật giới hạn số lần thử theo địa chỉ IP ở máy chủ.
