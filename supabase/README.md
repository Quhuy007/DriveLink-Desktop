# Supabase cho DriveLink — Quang Huy

**DriveLink 0.7.0:** nhận gói mã hóa trực tiếp không đăng nhập reader; Ctrl+Shift+A mở hộp mật khẩu admin. Dự án đã nâng cấp 0.6.0 cần chạy `upgrade-0.7.0.sql`. Hướng dẫn hiện hành nằm ở `CLOUD-OAUTH.md`; phần bên dưới ghi lại quy trình tạo tài khoản ban đầu của 0.5.0.

**DriveLink 0.6.0:** giao diện mới có tên Cloud lấy OAuth và kết nối đã được nhúng trong mã nguồn. Hướng dẫn bên dưới là quy trình 0.5.0 dùng để tạo/cấp quyền tài khoản lần đầu. Nếu đã có dự án, chạy `upgrade-0.6.0.sql` rồi làm theo `CLOUD-OAUTH.md` ở thư mục bản phát hành; không cần nhập URL/key trong giao diện mới. Setup mới đã bao gồm nâng cấp, không chạy lại setup cũ từ bản 0.5.0 sau khi nâng cấp.

Bản 0.5.0 có cửa sổ quản trị tích hợp: **Cài đặt → Tài khoản Google → Supabase: nhận cấu hình & quản trị**. Bạn không cần ứng dụng quản trị riêng hay mở website để cập nhật JSON hằng ngày. Dự án cloud cần được thiết lập một lần theo các bước dưới đây. Bộ SQL chưa được chạy trên dự án của bạn.

## 1. Tạo dự án

1. Mở https://supabase.com/dashboard, đăng nhập và tạo một project trong organization của bạn.
2. Đặt tên ví dụ `DriveLink-QuangHuy`, chọn khu vực phù hợp và đặt mật khẩu database riêng. Lưu mật khẩu đó ở nơi an toàn; không nhập nó vào DriveLink.
3. Chờ dự án sẵn sàng. Trong phần kết nối/API của dự án, lấy **Project URL** dạng `https://…supabase.co` và **publishable key** bắt đầu bằng `sb_publishable_`.

DriveLink hiện chỉ hỗ trợ URL hosted tiêu chuẩn này. Không dùng secret key, service_role, connection string database hoặc khóa anon kiểu cũ trong app. Publishable key được phép có mặt trong app; quyền dữ liệu do Supabase Auth và các kiểm tra phía máy chủ quyết định. Tài liệu chính thức: https://supabase.com/docs/guides/getting-started/api-keys.

## 2. Tạo hai tài khoản cloud

Trong **Authentication → Users**, tạo hai người dùng bằng email và mật khẩu:

- Một tài khoản quản trị cho bạn, được cấp vai trò `admin`.
- Một tài khoản nhận cấu hình cho máy khác, được cấp vai trò `reader`.

Hoàn tất xác nhận email; nếu dùng thao tác tạo người dùng của quản trị viên, có thể chọn xác nhận người dùng tại bước tạo. Dùng mật khẩu riêng cho các tài khoản này. Đây là tài khoản **Supabase Auth trong project**, khác tài khoản đăng nhập dashboard Supabase và khác tài khoản Google. Không gửi mật khẩu hoặc khóa quản trị vào chat. Nếu chỉ dùng cá nhân, bạn cũng có thể đăng nhập tài khoản admin trên các máy của mình; nên dùng reader cho máy chỉ cần tải cấu hình.

## 3. Thiết lập dữ liệu và quyền

1. Mở **SQL Editor** của đúng project mới, tạo một query và chạy toàn bộ nội dung `setup.sql` trong cùng thư mục hướng dẫn này.
2. Mở `bootstrap-members.sql`, thay `REPLACE_WITH_YOUR_ADMIN_EMAIL` và `REPLACE_WITH_YOUR_READER_EMAIL` bằng hai email đã tạo. Chạy nội dung đã sửa trong SQL Editor.
3. Nếu có lỗi, dừng tại lỗi đó; không tắt RLS hoặc cấp quyền rộng để bỏ qua. Hai tệp không chứa mật khẩu hoặc API key.

JSON nhỏ được lưu dưới dạng văn bản trong **Postgres**, cùng lịch sử phiên bản và bản đang dùng, không cần tạo bucket Storage. Việc thêm phiên bản và đổi bản đang dùng diễn ra trong cùng giao dịch. Reader chỉ tải bản đang dùng; admin có thể đăng bản mới, xem metadata lịch sử và chọn lại bản cũ. App không thể tự cấp admin. Thiết lập/thay đổi danh sách người dùng và vai trò vẫn do chủ project thực hiện; đây là trình quản trị cấu hình DriveLink, không thay thế toàn bộ dashboard Supabase.

## 4. Đăng JSON đầu tiên trong app

1. Mở DriveLink 0.5.0 → **Cài đặt → Tài khoản Google → Supabase: nhận cấu hình & quản trị**.
2. Tab **Kết nối**: điền Project URL và publishable key → **Lưu kết nối trên máy**.
3. Tab **Đăng nhập**: dùng email/mật khẩu tài khoản admin vừa tạo. Khi thành công sẽ hiện **Quản trị** và địa chỉ project.
4. Tab **Quản trị**: chọn JSON OAuth Desktop tải từ Google Cloud, đặt tên bản rồi bấm **Đăng phiên bản mới và đặt làm bản đang dùng**.
5. Xác nhận; kiểm tra tab **Nhận cấu hình** và danh sách lịch sử đã hiển thị bản vừa đăng.

Chỉ chọn JSON OAuth Desktop có phần `installed`; không chọn tài khoản dịch vụ, file token hoặc bản sao dữ liệu tài khoản. App kiểm tra định dạng và giới hạn 64 KB trước khi gửi. Mật khẩu và phiên đăng nhập Supabase không được lưu xuống đĩa; đóng cửa sổ cloud sẽ xóa phiên khỏi bộ nhớ của app. Khi phiên hết hạn cần đăng nhập lại. Việc này không làm Google đăng xuất.

## 5. Nhận cấu hình ở máy khác

1. Trên máy quản trị, tab **Kết nối → Xuất hồ sơ cho máy khác**. File này chỉ chứa URL và publishable key; không chứa JSON OAuth hoặc mật khẩu.
2. Máy nhận mở cùng cửa sổ, **Nhập hồ sơ kết nối → Lưu kết nối trên máy**, hoặc điền hai giá trị bằng tay.
3. Đăng nhập bằng tài khoản reader → **Nhận cấu hình → Tải & điền vào cấu hình máy**.
4. Kiểm tra tên bản/Client ID và xác nhận. Đóng cửa sổ cloud rồi bấm **Lưu cài đặt** ở cửa sổ cài đặt để áp dụng.
5. Sau đó bấm **+ Tài khoản** và đăng nhập Google của máy nhận. Hai máy dùng cùng OAuth Client nhưng có thể dùng hai tài khoản Google khác nhau. Quyền thư mục/file vẫn cần được chia sẻ cho đúng email Google.

App kiểm tra SHA-256 và Client ID trước khi điền cấu hình. SHA-256 kiểm tra tính nhất quán của nội dung tải về, không phải chữ ký xác thực độc lập. Cấu hình đã áp dụng được mã hóa bằng Windows DPAPI cho người dùng Windows hiện tại; máy vẫn sử dụng cấu hình cũ nếu cloud mất kết nối. Cloud chỉ lưu cấu hình ứng dụng, không lưu access token/refresh token hay file cá nhân Google Drive.

## 6. Cập nhật hoặc quay lại bản cũ

- Cập nhật: đăng nhập admin → chọn JSON mới → đặt tên → đăng bản mới. Bản cũ vẫn nằm trong lịch sử.
- Quay lại: chọn bản trong danh sách **100 bản gần nhất** → **Dùng lại phiên bản đã chọn**. Lịch sử không bị xóa.
- App kiểm tra bản đang dùng trước mỗi thay đổi. Nếu một phiên khác vừa cập nhật, app yêu cầu **Làm mới** thay vì ghi đè âm thầm.
- Nếu mất mạng/hủy chờ trong lúc đăng, máy chủ có thể đã hoàn tất. Làm mới để kiểm tra trước khi thử lại; app không tự gửi lại thao tác ghi.
- Các máy đã cấu hình **không tự đổi** theo cloud. Người dùng chủ động tải và lưu. Đổi Client ID yêu cầu xác nhận và kết nối lại Google trên máy áp dụng.
- Cập nhật OAuth trên Supabase không thay thế việc tạo/sửa OAuth Client tại Google Cloud khi bạn thực sự cần đổi Client. Với cùng OAuth Client, thêm tài khoản Google không cần JSON mới.

## 7. Kiểm tra trước khi dùng thật

Sau thiết lập, kiểm tra bằng hai tài khoản: reader tải được bản hiện tại nhưng không quản trị; admin đăng được hai bản và chọn lại bản đầu; email chưa được thêm vào `drivelink_members` không truy cập được. Đăng nhập từ hai cửa sổ/máy quản trị, cập nhật ở máy thứ nhất rồi thử cập nhật trạng thái cũ ở máy thứ hai: máy thứ hai phải yêu cầu làm mới. Không cấp thêm SELECT toàn bảng cấu hình hoặc quyền ghi cho anon/authenticated.

Bản phát hành đã kiểm tra phía app bằng phản hồi HTTP giả lập; quyền SQL/RLS và đăng nhập thực tế cần được kiểm tra trên project sau khi bạn tạo. Không có project hoặc tài khoản thật nào được tạo tự động.

## Lỗi thường gặp

- **Không đăng nhập được**: kiểm tra tài khoản thuộc Authentication của đúng project, mật khẩu và xác nhận email.
- **Chưa được cấp quyền**: đã chạy bootstrap với đúng email chưa? Tài khoản mới tạo không tự có vai trò.
- **404/chưa có cấu trúc cloud**: kiểm tra Project URL, chạy trọn `setup.sql`, đợi API nạp lại schema.
- **Phiên hết hạn**: đăng nhập cloud lại; cấu hình Google đã lưu không mất.
- **Bản đang dùng đã thay đổi**: làm mới rồi xem lại bản hiện tại trước khi quyết định đăng/chọn lại.
- **Lỗi máy chủ**: kiểm tra query setup có hoàn tất, trạng thái project và nhật ký Supabase; không đưa secret/service_role vào app để chữa lỗi quyền.

Tài liệu: [Supabase API keys](https://supabase.com/docs/guides/getting-started/api-keys), [Auth mật khẩu](https://supabase.com/docs/guides/auth/passwords), [RLS](https://supabase.com/docs/guides/database/postgres/row-level-security), [Database functions](https://supabase.com/docs/guides/database/functions).
