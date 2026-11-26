DriveLink 1.1.1 — Quang Huy

- Giao diện kính trên nền tối, bố cục Bento một màn. Danh sách tệp là thẻ chính; bên phải có các thẻ Gửi tệp, Truyền tải, Không gian, Gửi chéo và Dung lượng. Bố cục tự thu gọn trên cửa sổ nhỏ hoặc khi phóng to chữ.
- Mọi cửa sổ phụ (Cài đặt, Cập nhật, Chia sẻ, Thông tin, Thành viên, Cloud OAuth, Đăng nhập Google), hộp thông báo và menu chuột phải dùng cùng giao diện kính tối; thanh tiêu đề tối trên Windows 11. Hộp thông báo có nút "Có/Không" tiếng Việt, đóng bằng X chọn phương án an toàn.
- Gửi chéo tài khoản: chọn một tệp, chọn tài khoản Google khác đã thêm vào DriveLink rồi bấm Gửi (hoặc chuột phải → Chia sẻ → Gửi sang tài khoản khác…). Google sao chép trên máy chủ vào Drive của tôi của tài khoản đích, không tốn băng thông máy. Quyền xem tạm chỉ cấp cho đúng email đích, không gửi thư thông báo, và luôn được thu hồi sau khi sao chép. Bản sao được đối chiếu MD5 và kích thước với tệp gốc. Nếu Google chặn chia sẻ, app hỏi trước khi tải qua máy. Chưa hỗ trợ gửi cả thư mục.
- Thẻ Dung lượng hiển thị dung lượng đã dùng/còn trống của tài khoản đang chọn. Thẻ Truyền tải hiển thị tốc độ tải lên/tải về và biểu đồ theo thời gian thực. Danh sách tệp có biểu tượng màu theo loại tệp.
- Thông báo dạng "đảo" ở giữa phía trên khi gửi, nhận hoặc gửi chéo xong. Trang kết quả đăng nhập trên trình duyệt cũng dùng nền tối. Hiệu ứng chuyển động tự tắt khi Windows bật giảm hiệu ứng.

Đang dùng bản 0.8.0 hoặc 1.0.0 đã cài bằng Setup: mở Kiểm tra cập nhật trong app để tải và cài 1.1.1. Nếu dùng EXE/ZIP cũ, thoát app và cài **QuangHuy.DriveLink-win-x64-Setup.exe** một lần. Tài khoản, OAuth và cài đặt đã lưu được giữ lại. Không cần nâng cấp SQL Cloud cho bản này.

Gửi chéo cần ít nhất hai tài khoản Google trong DriveLink. Tài khoản tổ chức có thể chặn chia sẻ ra ngoài; khi đó app đề nghị tải qua máy. Menu ở khay hệ thống vẫn là menu Windows mặc định. Bộ cài Windows x64 đã có .NET; chưa ký mã Windows.
