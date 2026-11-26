# GitHub và cập nhật DriveLink

Repository công khai: https://github.com/Quhuy007/DriveLink-Desktop
Trang tải: https://github.com/Quhuy007/DriveLink-Desktop/releases/latest

## Cài trên máy mới hoặc nâng từ bản cũ

Tải `QuangHuy.DriveLink-win-x64-Setup.exe`, thoát DriveLink đang chạy rồi cài. Windows x64. Từ 1.3.0 app không kèm .NET: Setup (và trình cập nhật) tự cài .NET Desktop Runtime 10 nếu máy chưa có, sau khi hỏi người dùng. Bản 0.7.2 và các EXE/ZIP framework cũ cần cài Setup một lần để bật cơ chế cập nhật. Có thể dùng Portable do Velopack đóng gói nếu không muốn cài; cần giữ toàn bộ cấu trúc thư mục.

Tài khoản, OAuth và cài đặt nằm tại `%LOCALAPPDATA%\DriveLink`, tách khỏi thư mục cài đặt. Không xóa thư mục dữ liệu này khi nâng cấp. Chưa ký mã Windows; bộ cài có thể hiển thị cảnh báo nhà phát hành.

## Cập nhật từ app

App kiểm tra khi mở và mỗi 6 giờ, báo bản mới ở đầu cửa sổ. Mở “Kiểm tra cập nhật” ở thanh bên hoặc Cài đặt → Cập nhật để kiểm tra thủ công, đọc thay đổi, tải/cài hoặc để sau. Có thể tắt kiểm tra tự động. App không tự khởi động lại nếu chưa yêu cầu cập nhật; cài đặt chờ các lượt gửi/nhận hoàn tất. Tải hỏng hoặc hủy tải không thay thế phiên bản đang dùng.

Nguồn cập nhật là GitHub Releases công khai, không cần tài khoản hoặc token GitHub. Từ 1.2.0, app chỉ nhận bản có danh sách tệp được ký bằng khóa riêng của chủ ứng dụng (xem [SECURITY.md](SECURITY.md)). Gói được kiểm SHA-256 theo danh sách đó trước khi cài. Cập nhật giao diện, chức năng nhỏ hoặc lớn đều dùng cùng quy trình; thư viện và runtime được đóng gói cùng app. Thay đổi dữ liệu cần viết chuyển đổi tương thích và kiểm tra trước khi phát hành. SQL Cloud không tự chạy từ trình cập nhật.

## Phát hành phiên bản mới

Bản phát hành được build và **ký trên máy phát hành**. GitHub Actions (`verify.yml`) chỉ chạy kiểm thử và không tạo Release.

1. Sửa mã nguồn; tăng `<Version>` trong DriveLink.csproj; cập nhật CHANGELOG.md và RELEASE-NOTES.md; chạy `dotnet run --project tests/DriveLink.Checks.csproj -c Release`.
2. `pwsh scripts/Build-Release.ps1`: lưu bản vào `versions\vX.Y.Z` kèm ZIP mã nguồn.
3. `pwsh scripts/Build-UpdateRelease.ps1`: tải gói của bản trước từ GitHub để tạo gói delta, rồi tạo Setup, Portable, gói `.nupkg` đầy đủ + delta và feed `releases.win-x64.json` trong `artifacts\updates-vX.Y.Z`. Gói của bản trước bị loại khỏi thư mục trước khi ký.
4. `pwsh scripts/Sign-Release.ps1`: ký danh sách tệp bằng khóa ở `%LOCALAPPDATA%\DriveLink-Signing`. Khóa được tạo một lần bằng `scripts/New-UpdateSigningKey.ps1`; nhớ sao lưu.
5. `pwsh scripts/Publish-Release.ps1`: kiểm chữ ký, đẩy mã nguồn lên main và gắn tag, tạo Release nháp, tải lên các tệp đã ký, rồi mới công bố. Script dùng đăng nhập GitHub trong Git Credential Manager.
6. Kiểm tra Release và chạy `pwsh scripts/Verify-Release.ps1 -Folder <thư mục tải về>`. Máy đã cài sẽ thấy bản mới trong lần kiểm tra tiếp theo.

Giữ nguyên packId `QuangHuy.DriveLink` và channel `win-x64` giữa các phiên bản. Luôn tải lên feed và `.nupkg` cùng Setup/Portable; chỉ tải Setup không đủ cho máy cũ cập nhật. Không ghi đè bản phát hành cũ: sửa lỗi bằng số phiên bản mới. Không bao giờ đưa khóa ký lên GitHub hay vào thư mục dự án; workflow chỉ có quyền contents:read. Các bản lưu trên máy ở versions/ vẫn được giữ lại.

## Dữ liệu được công khai

Mã nguồn, tài nguyên giao diện, định danh kết nối Cloud công khai và bộ cài được phát hành. Không đưa Google token, OAuth JSON thật, mật khẩu, accounts.dat, preferences.dat, .env, secret/service_role hoặc khóa ký vào Git. Mật khẩu OAuth vẫn cần để giải mã cấu hình Cloud; mật khẩu quản trị vẫn cần để mở chức năng quản trị và quyền được kiểm tra tại máy chủ.

Tài liệu: https://docs.velopack.io/getting-started/csharp
