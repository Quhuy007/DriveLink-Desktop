# GitHub riêng tư và phát hành DriveLink

Repository: https://github.com/Quhuy007/DriveLink-Desktop

## Tải từ máy khác

Đăng nhập GitHub bằng tài khoản Quhuy007 hoặc tài khoản đã được cấp quyền cho repository. Mở repository → Releases → chọn bản → tải `DriveLink-vX.Y.Z-win-x64.zip`. Giải nén toàn bộ vào thư mục riêng rồi chạy DriveLink.exe. Bản ZIP này có sẵn .NET, khác các bản framework-dependent cũ trong versions/.

Repository Private giữ mã nguồn và Release riêng tư. Đường dẫn tải không tự cấp quyền: người chưa đăng nhập hoặc chưa được mời sẽ không tải được. Không nhúng token GitHub vào app để làm đường tải chung. Nếu cần cho người khác tải, cấp quyền repository cho đúng tài khoản GitHub. Bản ZIP chưa ký mã Windows; nếu Windows hỏi nhà phát hành, kiểm tra nguồn tải và SHA-256 đi kèm.

## Phát hành tự động

Workflow `.github/workflows/release.yml` chạy trên Windows khi đẩy tag `vX.Y.Z`; cũng có thể chạy thủ công từ Actions → Build and release DriveLink → Run workflow. Workflow kiểm tra app, lưu ZIP mã nguồn, đóng gói Windows x64 có .NET và tạo Release cùng SHA-256. Quyền workflow là contents: write trong repository này; dùng GITHUB_TOKEN tạm thời, không cần thêm token cá nhân hoặc mật khẩu Cloud.

Quy trình phiên bản mới:

1. Cập nhật mã nguồn, `<Version>` trong DriveLink.csproj và CHANGELOG.md.
2. Đưa commit mới lên main.
3. Tạo tag `vX.Y.Z` trùng Version hoặc chạy workflow thủ công ở commit đó.
4. Chờ workflow thành công và mở Releases để tải.

Workflow không ghi đè Release đã có. Muốn sửa bản đã phát hành, tăng số phiên bản. Chạy workflow/tag sẽ dùng tài nguyên GitHub Actions theo gói tài khoản của bạn. Chỉ giữ mã nguồn và tài nguyên cần thiết trong Git; các bản cũ trên máy vẫn nằm trong versions/, không bị xóa và không đưa cả thư mục này vào Git.

## Thông tin kết nối

CloudBuildSettings.cs chứa định danh kết nối công khai được đóng gói. Không đưa Google token, OAuth JSON thật, mật khẩu quản trị/reader/cấu hình, accounts.dat, preferences.dat, .env hoặc secret/service_role vào Git. Gói phát hành không chứa dữ liệu tài khoản đã lưu của máy xây dựng. Mật khẩu admin vẫn nhập khi sử dụng; mật khẩu OAuth vẫn cần để mở khóa JSON tải từ Cloud.

Tài liệu: https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
