DriveLink 1.3.1 — Quang Huy

- Đăng nhập lại một chạm: khi Google báo phiên đăng nhập đã hết hạn hoặc bị thu hồi (ví dụ OAuth app còn ở chế độ Testing quá 7 ngày), tài khoản hiện "cần đăng nhập lại" và app hỏi một lần (hoặc gửi thông báo Windows khi đang ẩn). Trình duyệt mở sẵn đúng email; tài khoản, không gian và cài đặt giữ nguyên. Menu tài khoản luôn có mục Đăng nhập lại.
- Lỗi cấu hình OAuth Client được báo riêng, không nhầm với hết phiên đăng nhập.
- Truyền thẳng trong mạng LAN nhanh hơn: nhiều tệp đi qua một kết nối, đọc ổ/mã hóa/gửi mạng chạy song song, không đọc tệp hai lần, dùng ít bộ nhớ hơn nhiều. Đo trên một máy: tệp 1 GB nhanh hơn khoảng 3–4 lần, 300 tệp nhỏ nhanh hơn khoảng 3,5 lần. Qua mạng thật, tốc độ vẫn tùy Wi-Fi/cáp và ổ đĩa.
- Nhận cả lô qua LAN chỉ hiện một thông báo; cửa sổ LAN hiện thứ tự tệp đang gửi và tốc độ trung bình khi xong.

Cập nhật từ 1.3.0: mở Kiểm tra cập nhật trong app. Tài khoản, cài đặt và khóa được giữ nguyên. Máy 1.3.1 vẫn gửi/nhận LAN được với máy 1.3.0 (khi đó dùng cách cũ, không nhanh hơn). Không cần chạy thêm SQL Cloud cho bản này.
