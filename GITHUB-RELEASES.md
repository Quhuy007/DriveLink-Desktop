# GitHub và cập nhật DriveLink

Repository công khai: https://github.com/Quhuy007/DriveLink-Desktop
Trang tải: https://github.com/Quhuy007/DriveLink-Desktop/releases/latest

## Cài trên máy mới hoặc nâng từ bản cũ

Tải `QuangHuy.DriveLink-win-x64-Setup.exe`, thoát DriveLink đang chạy rồi cài. Windows x64, đã có sẵn .NET. Bản 0.7.2 và các EXE/ZIP framework cũ cần cài Setup một lần để bật cơ chế cập nhật. Có thể dùng Portable do Velopack đóng gói nếu không muốn cài; cần giữ toàn bộ cấu trúc thư mục.

Tài khoản, OAuth và cài đặt nằm tại `%LOCALAPPDATA%\DriveLink`, tách khỏi thư mục cài đặt. Không xóa thư mục dữ liệu này khi nâng cấp. Chưa ký mã Windows; bộ cài có thể hiển thị cảnh báo nhà phát hành.

## Cập nhật từ app

App kiểm tra khi mở và mỗi 6 giờ, báo bản mới ở đầu cửa sổ. Mở “Kiểm tra cập nhật” ở thanh bên hoặc Cài đặt → Cập nhật để kiểm tra thủ công, đọc thay đổi, tải/cài hoặc để sau. Có thể tắt kiểm tra tự động. App không tự khởi động lại nếu chưa yêu cầu cập nhật; cài đặt chờ các lượt gửi/nhận hoàn tất. Tải hỏng hoặc hủy tải không thay thế phiên bản đang dùng.

Nguồn cập nhật là GitHub Releases công khai, không cần tài khoản hoặc token GitHub. Gói được kiểm tra toàn vẹn trước khi cài. Cập nhật giao diện, chức năng nhỏ hoặc lớn đều dùng cùng quy trình; thư viện và runtime được đóng gói cùng app. Thay đổi dữ liệu cần viết chuyển đổi tương thích và kiểm tra trước khi phát hành. SQL Cloud không tự chạy từ trình cập nhật.

## Phát hành phiên bản mới

1. Sửa mã nguồn; tăng `<Version>` trong DriveLink.csproj; cập nhật CHANGELOG.md và RELEASE-NOTES.md.
2. Đưa commit mới lên main.
3. Tạo tag `vX.Y.Z` trùng Version hoặc Actions → Build and release DriveLink → Run workflow.
4. Workflow kiểm tra app, đóng gói Setup/Portable, gói cập nhật đầy đủ và feed `releases.win-x64.json`, rồi tạo Release.
5. Kiểm tra Release hoàn tất. Máy cũ sẽ thấy bản mới trong lần kiểm tra tiếp theo.

Giữ nguyên packId `QuangHuy.DriveLink` và channel `win-x64` giữa các phiên bản. Luôn tải lên feed và `.nupkg` cùng Setup/Portable; chỉ tải Setup không đủ cho máy cũ cập nhật. Không ghi đè bản phát hành cũ: sửa lỗi bằng số phiên bản mới. Workflow dùng GITHUB_TOKEN tạm thời với quyền contents:write, không cần mật khẩu Cloud. Các bản lưu trên máy ở versions/ vẫn được giữ lại.

## Dữ liệu được công khai

Mã nguồn, tài nguyên giao diện, định danh kết nối Cloud công khai và bộ cài được phát hành. Không đưa Google token, OAuth JSON thật, mật khẩu, accounts.dat, preferences.dat, .env, secret/service_role hoặc khóa ký vào Git. Mật khẩu OAuth vẫn cần để giải mã cấu hình Cloud; mật khẩu quản trị vẫn cần để mở chức năng quản trị và quyền được kiểm tra tại máy chủ.

Tài liệu: https://docs.velopack.io/getting-started/csharp
