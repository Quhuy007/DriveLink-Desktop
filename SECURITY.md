# Bảo mật DriveLink

DriveLink là công cụ dùng nội bộ của **Quang Huy**. Tài liệu này ghi các lớp bảo vệ có trong app (từ bản 1.2.0), những việc chủ ứng dụng phải tự làm, và các giới hạn còn lại.

## 1. Khi tài khoản GitHub bị chiếm

Repository vẫn công khai để máy khách tải cập nhật mà không cần token. Từ 1.2.0, việc chiếm được GitHub **không còn đủ** để đẩy bản cài độc hại lên các máy đã cài:

- **Bản phát hành được ký trên máy phát hành.** `scripts/Sign-Release.ps1` tạo `DriveLink-release-manifest.json` (tên và SHA-256 của mọi tệp tải lên Release) cùng chữ ký `DriveLink-release-manifest.json.sig` (ECDSA P-256). Khóa riêng nằm ở `%LOCALAPPDATA%\DriveLink-Signing\update-signing-key.dat`, được mã hóa DPAPI theo người dùng Windows của máy phát hành, và không bao giờ lên GitHub.
- **App kiểm chữ ký trước khi báo có bản mới.** Khóa công khai được ghim trong `Services/UpdateTrust.cs`. Gói Velopack sắp tải phải có đúng tên, SHA-256 và số phiên bản như trong danh sách đã ký; Velopack lại kiểm gói tải về theo đúng mã đó. Thiếu chữ ký, sai khóa, hay danh sách bị sửa thì app chặn, giữ bản đang dùng, hiện cảnh báo và ghi `update-blocked` vào nhật ký bảo mật. Không hạ phiên bản (`AllowVersionDowngrade = false`), nên kẻ tấn công cũng không đẩy lại được một bản cũ đã ký.
- **GitHub Actions chỉ chạy kiểm thử** (`.github/workflows/verify.yml`). Workflow chỉ có quyền `contents: read`, các action được ghim theo mã commit, và workflow không còn tạo Release. Một action bị nhiễm độc hay một token workflow bị lộ không tạo được bản phát hành mà app chấp nhận.
- **Dependabot** (`.github/dependabot.yml`) mở pull request hằng tuần khi Velopack, các gói NuGet hoặc GitHub Actions có bản vá. Đọc thay đổi rồi mới gộp.
- **Người tải có thể tự kiểm:** tải các tệp của Release vào một thư mục rồi chạy

  ```powershell
  pwsh scripts/Verify-Release.ps1 -Folder <thư mục>
  ```

  Kết quả "HỢP LỆ" nghĩa là các tệp do chủ ứng dụng ký và không bị sửa sau khi ký. Mã nguồn `source-vX.Y.Z.zip` cũng nằm trong danh sách đã ký.

Lưu ý chuyển tiếp: máy đang chạy 1.1.x chưa có bước kiểm chữ ký, nên lần nâng lên 1.2.0 vẫn tin GitHub như trước. Bảo vệ có hiệu lực từ khi máy đã chạy 1.2.0. Hãy phát hành 1.2.0 khi chắc chắn tài khoản GitHub còn an toàn.

### Việc chủ ứng dụng phải tự làm

1. **Sao lưu khóa ký** ra USB hoặc trình quản lý mật khẩu, không để trong thư mục dự án hay trên cloud. Lệnh sau hỏi mật khẩu và xuất tệp PEM được mã hóa:

   ```powershell
   pwsh scripts/New-UpdateSigningKey.ps1 -BackupPath E:\drivelink-signing-backup.pem
   ```

   Mất khóa thì các máy đã cài không nhận được bản mới nữa; khi đó phải phát hành một bản có khóa công khai mới bằng Setup và cài lại trên từng máy. Muốn đổi khóa mà không mất cập nhật thì thêm khóa mới vào `UpdateTrust.TrustedPublicKeys`, phát hành một bản ký bằng khóa cũ, rồi mới chuyển sang ký bằng khóa mới.
2. **Bật 2FA** cho tài khoản GitHub, ưu tiên passkey hoặc khóa bảo mật thay cho SMS. Lưu mã khôi phục ở nơi riêng.
3. **Bảo vệ nhánh và tag** (Settings → Rules → Rulesets): chặn force-push và xóa nhánh `main`; chặn xóa và cập nhật tag `v*`.
4. Định kỳ xem Settings → Security log của GitHub và danh sách token/SSH key/app đã cấp quyền.

## 2. Token Google trên máy

- `accounts.dat` vẫn được mã hóa DPAPI, giờ kèm khóa phụ riêng của DriveLink. Bên trong, refresh token và access token được mã hóa thêm một lớp **AES-256-GCM**, với khóa nằm ở nơi khác:
  - **Windows Hello** (khi máy có): khóa được sinh từ chữ ký của một khóa nằm trong TPM/Windows Hello, và chỉ dùng được sau khi người dùng xác nhận PIN, vân tay hoặc khuôn mặt. Mỗi lần mở app, Windows Hello hỏi một lần; nếu bấm hủy, các tài khoản Google bị khóa cho tới khi bấm ↻.
  - **Khóa riêng trên máy** (máy không có Hello): 32 byte ngẫu nhiên, mã hóa DPAPI, lưu ở `HKCU\Software\QuangHuy\DriveLink`, tách khỏi `accounts.dat`.
- Kết quả là sao chép riêng `accounts.dat` sang máy khác thì không dùng được. Một phần mềm độc hại chạy dưới tài khoản của bạn cũng không đọc thẳng được token: với Hello, nó cần bạn xác nhận; với khóa riêng, nó phải biết DriveLink giấu khóa ở đâu.
- Cài đặt → Tài khoản Google → **Bảo mật** cho biết phương thức đang dùng. Nút **Thu hồi quyền và xóa tài khoản khỏi máy này** gọi Google thu hồi token rồi mới xóa khỏi máy.
- Token cũ (bản 1.1.x) vẫn đọc được và được mã hóa lại ngay khi mở app. Sau khi nâng cấp, bản 1.1.x cũ **không đọc được** `accounts.dat` mới. Nếu cần quay về bản cũ, thêm lại tài khoản Google.

### Vì sao không có "ai đụng vào tệp thì tự thu hồi token"

Windows không cho một app thường biết chắc ai đã **đọc** một tệp. Việc đọc không để lại dấu vết mà app thấy được, và ghi nhật ký truy cập của hệ thống cần quyền quản trị. Kẻ đọc được tệp cũng thường đọc được luôn "bẫy". Vì vậy app không tự thu hồi khi có người đụng vào tệp. Thay vào đó, app làm cho tệp bị lấy cắp trở nên vô dụng: khóa nằm tách khỏi tệp, trong phần cứng hoặc Registry, và cần Hello để mở. Chia nhỏ khóa ra nhiều nơi không thêm an toàn thật trước phần mềm chạy cùng người dùng, vì app tự ghép lại được thì phần mềm đó cũng ghép được.

Nếu nghi máy bị nhiễm: thu hồi DriveLink tại https://myaccount.google.com/connections cho từng tài khoản và đổi mật khẩu Google. Việc này có hiệu lực ngay, kể cả khi token đã bị sao chép.

## 3. Quyền Google Drive

App vẫn xin phạm vi `drive`. Phạm vi hẹp `drive.file` chỉ thấy tệp do chính app tạo, nên không đọc được không gian chung do tài khoản khác tạo hay tệp được gửi tới, tức là làm hỏng tính năng chính. Phạm vi rộng được bù lại bằng các lớp bảo vệ token ở mục 2 và khả năng thu hồi nhanh.

## 4. Quyền tạm khi gửi chéo

- Mỗi quyền xem tạm được ghi vào `%LOCALAPPDATA%\DriveLink\pending-grants.dat` (mã hóa DPAPI) **trước** khi cấp, và chỉ xóa khỏi tệp sau khi đã thu hồi thành công.
- Nếu app bị tắt ngang hoặc thu hồi lỗi, lần mở sau sẽ tự tìm và thu hồi các quyền còn sót, theo mã quyền hoặc theo email đích. Tệp đã bị xóa thì bỏ qua.
- Thư mục tạm của lần tải qua máy bị gián đoạn được dọn khi mở app.

## 5. Chia sẻ công khai

- Tệp đang có quyền "Bất kỳ ai có liên kết" hiện biểu tượng quả cầu màu cam trong danh sách tệp.
- Bật "Bất kỳ ai có liên kết" phải qua một hộp xác nhận nêu rõ rủi ro (người có quyền sửa còn có thể xóa hoặc thay nội dung). Cửa sổ Chia sẻ cảnh báo khi mở một tệp đang công khai.
- Bật hoặc tắt liên kết công khai được ghi vào nhật ký bảo mật.

## 6. Mật khẩu quản trị Cloud

- **Trên máy:** sai 3 lần phải chờ 15 phút; tổng cộng 9 lần sai thì máy bị khóa mở quản trị hẳn. Đăng nhập đúng khi chưa bị khóa hẳn sẽ đặt lại bộ đếm. Trạng thái được lưu ở hai nơi, tệp mã hóa DPAPI và Registry, và app lấy giá trị nghiêm ngặt hơn. Vì vậy xóa một nơi không mở khóa được, còn sửa dữ liệu thì máy bị khóa luôn.
- **Trên Cloud:** chạy `supabase/upgrade-1.2.0.sql` một lần trong Supabase → SQL Editor. Máy chủ đếm số lần sai theo địa chỉ IP (sai 3 lần chờ 15 phút; 9 lần thì chặn hẳn). Địa chỉ bị chặn không mở được quản trị, không đọc được danh sách thành viên và không phát hành được cấu hình, kể cả khi dùng công cụ khác ngoài app. Khi Cloud báo bị chặn, app khóa luôn trên máy.
- **Mở lại** cho một địa chỉ hoặc một máy: chạy lệnh sau trong SQL Editor của Supabase

  ```sql
  delete from public.drivelink_admin_guard where client_ip = '<địa chỉ>';
  ```

  Trên máy bị khóa, xóa `%LOCALAPPDATA%\DriveLink\admin-guard.dat` và giá trị `AdminGuard` trong `HKCU\Software\QuangHuy\DriveLink`.

## 7. Tệp chạy được từ người khác

Trước khi tải về, hoặc trước khi mở thư mục chứa một tệp có thể chạy mã (.exe, .msi, .bat, .cmd, .ps1, .vbs, .js, .lnk, .reg, .jar, .iso, .docm…) do **người khác** gửi vào không gian chung, app hỏi xác nhận và nhắc kiểm tra người gửi. Mỗi tệp chỉ hỏi một lần trong mỗi phiên.

## 8. Nhật ký bảo mật

`%LOCALAPPDATA%\DriveLink\security-log.jsonl`, mở bằng Cài đặt → Tài khoản Google → **Mở nhật ký bảo mật**. Mỗi dòng một sự kiện: cấp hoặc thu hồi quyền tạm, đổi cấu hình OAuth, cài cập nhật, bản cập nhật bị chặn, bảo vệ và thu hồi token, xóa tài khoản, sai mật khẩu và đăng nhập quản trị, khóa máy, bật hoặc tắt liên kết công khai, cảnh báo tệp chạy được. Nhật ký **không bao giờ** ghi token, mật khẩu hay email đầy đủ (email được che). Khi vượt 1 MB, tệp cũ được đổi tên thành `.1`.

## 9. Mã hóa đầu cuối (tùy chọn, từ 1.3.0)

- Bật cho từng không gian: chuột phải không gian → **Mã hóa đầu cuối**. Mọi thành viên nhập **cùng một mật khẩu** trên máy của họ và so **mã kiểm tra** (ví dụ `1A2B-3C4D-5E6F`) để chắc đã nhập giống nhau.
- Khóa không gian = PBKDF2-SHA256 (600.000 vòng, muối từ mã thư mục không gian). Khóa này lưu mã hóa DPAPI trong `e2e-keys.dat`; mật khẩu không được lưu.
- Mỗi tệp có khóa riêng (HKDF) và được mã hóa AES-256-GCM theo khối 1 MB. Số thứ tự khối và cờ "khối cuối" được xác thực, nên sửa, cắt hay đảo dữ liệu đều bị phát hiện. Google chỉ nhận tệp `.dlenc`.
- Giới hạn: tên tệp, kích thước gần đúng, thời gian và người gửi vẫn thấy được trên Drive. Mất mật khẩu thì không mở được tệp đã mã hóa; không có cách khôi phục.

## 10. Truyền thẳng trong LAN (tùy chọn, mặc định tắt)

- Khi bật, app gửi gói UDP quảng bá trên cổng 47210 (chỉ mã thiết bị, tên máy, cổng) và nhận kết nối TCP trên cổng 47211. Windows có thể hỏi về tường lửa: chỉ cho phép **Mạng riêng**.
- Mỗi máy có khóa ECDSA P-256 riêng (DPAPI). **Ghép nối** hiện cùng một mã 6 số trên hai máy, tính từ phần bắt tay đã ký. Chỉ bấm Có khi hai mã trùng nhau và chính bạn vừa bấm Ghép nối trên máy kia; nhờ vậy kẻ chen giữa không ghép nối được.
- Chỉ máy đã ghép nối mới gửi/nhận được. Kênh truyền dùng ECDH tạm thời + AES-256-GCM, mỗi tệp được kiểm SHA-256 trước khi lưu vào `Hộp thư đến\LAN`. Bỏ ghép nối trong cửa sổ Thiết bị LAN.
- Giao thức 2 (từ 1.3.1): cả lô tệp đi qua một kết nối; mã SHA-256 của mỗi tệp được gửi sau dữ liệu, trong cùng kênh mã hóa. Tệp chỉ được đổi từ `.lan-partial` sang tên thật khi mã khớp. Phiên bản giao thức nằm trong lời chào đã ký, nên kẻ chen giữa không ép hai máy xuống bản cũ mà không bị phát hiện. Nhật ký bảo mật ghi một dòng mỗi lô (số tệp, đuôi tệp), không ghi tên tệp.

## 11. Tự tải tệp mới

- Tệp chạy được (.exe, .ps1, .lnk…) và tệp vượt giới hạn dung lượng **không** tự tải, chỉ báo. Tệp mã hóa đầu cuối chỉ tự tải khi máy đã có khóa của không gian.
- Tệp tự tải nằm riêng trong `Downloads\DriveLink - Hộp thư đến\<không gian>`, không bao giờ ghi đè tệp có sẵn. Tắt tự tải trong Cài đặt nếu không cần.

## Quy trình phát hành (thay cho phát hành tự động)

```powershell
pwsh scripts/Build-Release.ps1           # versions\vX.Y.Z + mã nguồn
pwsh scripts/Build-UpdateRelease.ps1     # artifacts\updates-vX.Y.Z (Setup, Portable, gói cập nhật, feed)
pwsh scripts/Sign-Release.ps1            # ký danh sách tệp
pwsh scripts/Publish-Release.ps1         # kiểm chữ ký, đẩy mã nguồn + tag, tạo Release và tải lên tệp đã ký
```

`Publish-Release.ps1` dùng đăng nhập GitHub có sẵn trong Git Credential Manager. Token chỉ nằm trong bộ nhớ, không in ra và không ghi xuống đĩa. Script từ chối ghi đè một tag đã có.

## Báo lỗi bảo mật

Đây là app nội bộ: báo trực tiếp cho chủ ứng dụng, không mở issue công khai.
