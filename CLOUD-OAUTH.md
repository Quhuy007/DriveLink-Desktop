# Cloud lấy OAuth — DriveLink 0.7.2

Bản 0.7.2 chỉ hỏi mật khẩu quản trị, bỏ ô email. Tài khoản mặc định là `admin_account_verify@drivelink.private.com`, được chủ ứng dụng cung cấp sau khi xóa admin cũ. Mật khẩu không được nhúng trong ứng dụng; vai trò admin vẫn do máy chủ kiểm tra. Giữ nguyên sửa lỗi API trả code dạng số của 0.7.1, không cần chạy thêm SQL cho bản 0.7.2.

Để xóa tài khoản cũ đã đăng OAuth: chạy `supabase/upgrade-0.7.1.sql` trong SQL Editor, rồi vào Authentication → Users xóa đúng tài khoản cũ. SQL chỉ sửa liên kết created_by để khi xóa tài khoản thì giữ nguyên phiên bản OAuth, đặt trường người tạo thành NULL. Không tự xóa người dùng, không xóa lịch sử. Nếu vẫn lỗi 500, xem Auth/Postgres logs để tìm ràng buộc hoặc Storage object khác; không xóa dữ liệu hàng loạt. Bản đang chạy cần thoát hoàn toàn trước khi mở EXE 0.7.2.

## Nâng cấp một lần

1. Nếu đã nâng cấp Cloud 0.6.0, mở SQL Editor của đúng project và chạy toàn bộ `supabase/upgrade-0.7.0.sql`. Nếu chưa nâng cấp 0.6.0, chạy `upgrade-0.6.0.sql` trước rồi chạy 0.7.0. Không cần tạo lại hai tài khoản admin/reader hoặc đổi các vai trò đã có.
2. Thoát hoàn toàn DriveLink cũ (khay hệ thống → Thoát hoàn toàn) rồi mở `versions/v0.7.2/DriveLink.exe`. App chỉ chạy một phiên; EXE mới khi bản cũ còn chạy sẽ đưa bản cũ lên trước.
3. Mở Cài đặt → Tài khoản Google → Cloud lấy OAuth. App kiểm tra Cloud tự động. Nhập mật khẩu OAuth → Tải, mở khóa & điền cấu hình → đóng Cloud → Lưu cài đặt.

Nếu tạo project mới, `supabase/setup.sql` đã bao gồm các nâng cấp. Chỉ cấp quyền admin cho tài khoản quản trị theo hướng dẫn thiết lập ban đầu. Hai tệp nâng cấp không được chạy tự động trên project thật trong quá trình xây dựng.

## Nhận OAuth không đăng nhập reader

Theo lựa chọn của chủ ứng dụng, app phân phối công khai **chỉ gói OAuth hiện tại đã mã hóa bằng mật khẩu**. Bất kỳ ai có kết nối app đều có thể tải gói mã hóa và metadata bản hiện tại; họ vẫn cần mật khẩu OAuth để đọc JSON. App không đăng nhập tài khoản reader ngầm, không nhúng mật khẩu reader/admin trong file chạy, không tạo thêm tài khoản. Tài khoản reader đã tạo vẫn giữ nguyên để tương thích với luồng cũ.

API nhận tự động không cấp quyền xem bảng/lịch sử hoặc ghi dữ liệu. Hai hàm mới chỉ trả bản có định dạng mã hóa; JSON rõ từ bản cũ không được phân phối. Mật khẩu mở khóa chỉ xử lý trên máy, không gửi lên Cloud. Dùng mật khẩu dài, khó đoán vì người đã tải ciphertext có thể thử đoán ngoại tuyến.

Thiếu hoặc sai mật khẩu OAuth: ô nhập có viền đỏ, lời nhắc ngay bên dưới và thông báo đỏ nổi bật phía trên. Khi sửa mật khẩu, viền cảnh báo được xóa để thử lại. Lỗi mạng không được báo thành sai mật khẩu. Sau khi đã lưu cấu hình, app dùng Windows DPAPI cho người dùng Windows hiện tại, không hỏi mật khẩu mỗi lần mở.

## Mở và khóa quản trị

- Trong cửa sổ Cloud, nhấn **Ctrl+Shift+A** để mở hộp nhập mật khẩu quản trị.
- Email quản trị được đặt trong `Services/CloudDefaults.cs` và không có ô nhập trên giao diện. Mật khẩu là mật khẩu Supabase Auth của email mặc định, khác mật khẩu OAuth. Vai trò admin phải có trong bảng drivelink_members, không chỉ dựa vào việc tài khoản tồn tại trong Authentication.
- Bỏ trống: giữ hộp thoại mở và đánh dấu đỏ ô nhập.
- Sai mật khẩu: hộp thoại đóng, cửa sổ Cloud báo đỏ “Nhập sai mật khẩu quản trị”, tab Quản trị vẫn ẩn.
- Đúng mật khẩu và máy chủ xác nhận vai trò admin: hộp thoại đóng và mở tab Quản trị. Tài khoản có mật khẩu đúng nhưng chỉ mang vai trò reader vẫn bị từ chối.
- Bấm Khóa và đóng quản trị hoặc nhấn lại Ctrl+Shift+A: xóa phiên quản trị trong bộ nhớ. Lần mở tiếp theo cần nhập lại mật khẩu.
- Lỗi mạng: hộp thoại giữ mở để thử lại. Đóng khi đang xác thực sẽ hủy yêu cầu.

Quản trị viên chọn JSON OAuth Desktop gốc, đặt tên và mật khẩu riêng từ 12–256 ký tự, nhập lại mật khẩu rồi đăng. Mật khẩu thiếu/quá ngắn hoặc xác nhận không khớp được đánh dấu tại đúng ô. Lịch sử phân trang 100 bản/lần; bấm Tải thêm lịch sử cho đến khi Đã tải hết. Chọn lại bản mã hóa phải dùng đúng mật khẩu của bản đó trên máy nhận. Mất mạng/hủy trong lúc đăng: Làm mới trước khi thử lại vì máy chủ có thể đã hoàn tất.

## Bảo vệ dữ liệu và tương thích

JSON được mã hóa AES-256-GCM trước khi gửi; PBKDF2-HMAC-SHA256 600.000 vòng và salt/nonce ngẫu nhiên mỗi lần tạo khóa. Mật khẩu cấu hình không gửi lên Cloud hoặc lưu trong app. Cloud không khôi phục được mật khẩu cũ: nếu quên, đăng lại JSON gốc với mật khẩu mới.

URL/publishable key được đóng gói trong `Services/CloudBuildSettings.cs`; khóa mở gói đi cùng EXE nên có thể trích xuất. Đây là che thông tin công khai, không phải kho bí mật. Không nhúng secret/service_role hoặc mật khẩu tài khoản. Đổi kết nối mặc định bằng `scripts/Set-CloudDefaults.ps1 -ProfilePath <hồ sơ công khai>` rồi tạo phiên bản mới. Đổi admin bằng cách sửa `CloudDefaults.AdminEmail` và bảo đảm email đó được cấp admin trên máy chủ.

Bản cũ đã đăng chưa có mật khẩu không tự được mã hóa lại. Metadata lịch sử vẫn hiện cho admin; nội dung rõ cũ còn trong database cho chủ project. Đăng lại JSON gốc với mật khẩu bằng bản mới. Máy đã áp dụng cấu hình trước đây vẫn hoạt động; đổi mật khẩu Cloud không thu hồi bản họ đã nhận hoặc thay đổi quyền Google Drive.

## Kiểm tra trên project sau nâng cấp

Kiểm tra nhận gói mã hóa không đăng nhập, mật khẩu OAuth sai bị báo đỏ và không áp dụng, mật khẩu đúng mở khóa được. Ctrl+Shift+A: thiếu/sai/đúng mật khẩu admin phải cho kết quả tương ứng; tài khoản không có vai trò admin không được mở quản trị. Lịch sử/đăng/kích hoạt vẫn cần phiên admin. Kiểm tra đồng thời hai phiên cập nhật: trạng thái cũ yêu cầu làm mới. Kiểm thử phía app dùng phản hồi HTTP giả lập; SQL/RLS và đăng nhập thật cần kiểm tra trên project của bạn.
