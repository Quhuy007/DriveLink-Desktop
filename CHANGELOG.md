# Phiên bản DriveLink — Quang Huy

## 0.7.2

- Bỏ ô nhập email quản trị, chỉ hỏi mật khẩu khi nhấn Ctrl+Shift+A.
- Đặt tài khoản admin mặc định thành email còn lại do chủ ứng dụng cung cấp; không dùng tài khoản cũ đã xóa. Mật khẩu vẫn chỉ nhập lúc xác thực, quyền admin do Cloud kiểm tra.
- Giữ sửa lỗi mã phản hồi API dạng số và cảnh báo mật khẩu; không cần nâng cấp SQL mới so với 0.7.1.

## 0.7.1

- Sửa lỗi đọc mã lỗi API kiểu Number thành String; xử lý error_code của Auth riêng, phân biệt thông tin đăng nhập sai và email chưa xác nhận.
- Hộp quản trị có email chỉnh được, hỗ trợ nhiều admin hoặc đổi sang tài khoản admin mới; không giới hạn tổng số tài khoản Cloud.
- Nâng cấp SQL cho phép xóa tài khoản Auth cũ mà giữ nguyên lịch sử OAuth: created_by có thể NULL và khóa ngoại ON DELETE SET NULL. Tệp nâng cấp không tự xóa tài khoản nào.

## 0.7.0

- Cloud lấy OAuth mở trực tiếp chế độ nhận, không có form đăng nhập reader. Theo lựa chọn người dùng, API chỉ phân phối công khai bản OAuth đang dùng đã mã hóa; không nhúng mật khẩu reader hoặc admin vào app, không tạo tài khoản mới.
- Ctrl+Shift+A mở hộp mật khẩu admin. Thiếu mật khẩu giữ hộp thoại mở và viền đỏ; sai mật khẩu đóng hộp thoại, hiển thị cảnh báo đỏ ở cửa sổ Cloud; đúng mật khẩu và vai trò admin mới mở tab quản trị. Khóa/đóng tab xóa phiên quản trị; lần mở tiếp theo xác thực lại.
- Trạng thái nổi bật phía trên; ô mật khẩu OAuth và mật khẩu đăng bản mới báo viền đỏ cùng lời nhắc khi thiếu/sai/không khớp, tự bỏ đánh dấu khi nhập lại.
- Nâng cấp SQL 0.7.0 chỉ cấp quyền gọi hai hàm nhận gói mã hóa hiện tại, không mở quyền bảng, lịch sử hay ghi dữ liệu cho người nhận tự động.

## 0.6.0

- Đổi giao diện thành Cloud lấy OAuth, bỏ trường URL/key và tên nhà cung cấp khỏi luồng người dùng. Kết nối mặc định đóng gói trong mã nguồn, dùng được trên máy mới; chỉ là che chuỗi công khai, không bảo đảm chống trích xuất file chạy.
- Mật khẩu riêng theo từng phiên bản OAuth, tách khỏi mật khẩu tài khoản Cloud. JSON mã hóa AES-256-GCM với PBKDF2-HMAC-SHA256 600.000 vòng và salt/nonce ngẫu nhiên trước khi gửi. Mật khẩu cấu hình không gửi lên Cloud hoặc lưu xuống máy.
- Ctrl+Shift+A mở/đóng quản trị ẩn sau khi đăng nhập admin. Reader không mở được. Lịch sử phân trang 100 bản/lần, tải thêm được toàn bộ; bản cũ vẫn có metadata để tra cứu.
- Bộ nâng cấp SQL giữ thành viên/lịch sử, chặn API đăng/tải bản JSON rõ và chỉ kích hoạt bản mã hóa. Cần chạy upgrade-0.6.0.sql trên dự án hiện có và đăng lại JSON với mật khẩu; chưa thực thi nâng cấp tự động trên Cloud thật.
- Khi áp dụng thành công, OAuth tiếp tục được Windows DPAPI mã hóa theo người dùng; không hỏi mật khẩu mỗi lần mở app. Bản cũ trên Cloud không được tự mã hóa lại và bản đã áp dụng trước đây không bị thu hồi tự động.

## 0.5.0

- Tích hợp Cloud & quản trị Supabase trong cài đặt Google: đăng nhập tài khoản Supabase Auth, tải cấu hình OAuth Desktop cho máy mới, đăng JSON mới và chọn lại phiên bản cũ.
- Vai trò reader/admin kiểm tra cả phía app và các hàm SQL; publishable key trong app, không dùng service_role/secret key. Mật khẩu/phiên cloud không lưu xuống đĩa.
- Kiểm tra SHA-256, Client ID, giới hạn JSON và chống ghi đè khi hai phiên quản trị cùng cập nhật. Chỉ áp dụng OAuth sau khi bấm Lưu cài đặt; đổi Client ID cần xác nhận kết nối lại Google.
- Xuất/nhập hồ sơ URL + publishable key cho các máy, không kèm mật khẩu/Google token. Bộ SQL và hướng dẫn thiết lập lần đầu trong supabase/; cần tạo project và kiểm tra kết nối thực tế sau thiết lập.

## 0.4.1

- Mở menu không gian ngay từ lần chuột phải đầu, không bị thao tác chọn mục/làm mới chặn menu.
- Cửa sổ khởi động vừa vùng làm việc của màn hình, tránh khuất thanh tiêu đề trên màn hình có tỷ lệ hiển thị lớn.

## 0.4.0

- Chuột phải không gian: màu, ghim đầu danh sách, ẩn trên máy, đưa thư mục vào Thùng rác Drive nếu có quyền. Không xóa vĩnh viễn.
- Cài đặt độ đậm lớp kéo thả, bật/tắt lớp thông báo.
- Gửi thư mục: nén ZIP mặc định; lựa chọn RAR, công cụ 7-Zip/WinRAR/tích hợp và mức nén mạnh. Giữ nguyên nguồn, dọn file nén tạm sau lượt gửi, không đi theo liên kết thư mục.
- Ghi nhớ thư mục nguồn để mở trên máy; chuyển kiểm tra đường dẫn và gọi Windows Shell sang luồng riêng, tránh gọi explorer.exe đồng bộ mỗi lần nhấn.
- Thu nhỏ/đóng vào khay hệ thống để chạy nền, khôi phục và thoát bằng menu icon; mặc định tắt.
- Khởi động khi đăng nhập Windows, tùy chọn trong cài đặt; mặc định tắt. Khi bật chạy nền, tác vụ khởi động mở vào khay hệ thống.
- Chống mở trùng bản 0.4.0 trở lên: lần mở tiếp theo đưa cửa sổ hiện có lên trước.
- Làm rõ việc thêm tài khoản Google không cần JSON mới khi dùng cùng OAuth Client; cài đặt và màu/ghim có tệp mã hóa riêng để tránh mất khi mở lại các bản cũ.

## 0.3.0

- Tên Quang Huy và số phiên bản trên thanh tiêu đề và giao diện.
- Icon từ drivelink-icon-concept.png cho tệp EXE, cửa sổ app và giao diện đăng nhập.
- Trang web đăng nhập thành công/thất bại có icon đầy đủ, tên tác giả và phiên bản; ảnh được nhúng, không phụ thuộc kết nối bên ngoài.
- Cửa sổ chờ đăng nhập nhỏ, hiện kết quả rồi tự đóng sau 3 giây; hỗ trợ hủy đăng nhập.
- Lưu các bản chạy, mã nguồn và thông tin kiểm tra SHA-256 riêng trong versions; không ghi đè bản đã lưu.

## 0.2.0

Bản trước lần đổi nhận diện này, được lưu từ bản chạy đã có: kéo thả toàn cửa sổ, menu sao chép link, ghi nhớ file cục bộ, gửi/nhận đồng thời và tốc độ truyền, trang callback đăng nhập.

Các bản trước 0.2.0 chưa được lưu thành bộ phiên bản riêng nên không thể phục hồi đầy đủ từ dự án hiện tại.
