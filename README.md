# DriveLink

Repository công khai: [Quhuy007/DriveLink-Desktop](https://github.com/Quhuy007/DriveLink-Desktop). Tải Setup Windows x64 từ Releases không cần đăng nhập GitHub. Cài một lần để bật cập nhật trong app; Setup tự cài .NET Desktop Runtime 10 nếu máy chưa có. Xem [GITHUB-RELEASES.md](GITHUB-RELEASES.md) về tải trên máy mới và phát hành bản đã ký, và [SECURITY.md](SECURITY.md) về các lớp bảo mật.

Ứng dụng của **Quang Huy**. Bản hiện tại: **1.4.0**. Các bản chạy được giữ riêng trong `versions`, dùng chung kết nối tài khoản đã lưu trên máy.

**Cài đặt → Tài khoản Google → Cloud lấy OAuth**: kết nối mặc định có sẵn. Nhập mật khẩu OAuth rồi tải/mở khóa và Lưu cài đặt, không đăng nhập reader. Ctrl+Shift+A mở hộp mật khẩu admin, xác thực đúng mới mở quản trị/lịch sử. Thiếu/sai mật khẩu có khung đỏ và thông báo nổi bật. Xem [CLOUD-OAUTH.md](CLOUD-OAUTH.md) để nâng cấp Cloud hiện có. Chưa chạy nâng cấp SQL hoặc kiểm tra đăng nhập trên project thật trong quá trình phát triển; kiểm tra app sử dụng phản hồi mô phỏng. Google token chỉ lưu trên từng máy.

- `versions/v0.2.0/DriveLink.exe`: bản trước khi đổi nhận diện.
- `versions/v0.3.0/DriveLink.exe`: icon riêng, tên Quang Huy và cửa sổ đăng nhập mới.
- `versions/v0.4.0/DriveLink.exe`: quản lý không gian, gửi thư mục nén, cài đặt giao diện, chạy nền và khởi động Windows.
- `versions/v0.4.1/DriveLink.exe`: bản hoàn thiện menu chuột phải và kích thước cửa sổ trên màn hình có tỷ lệ hiển thị lớn.
- `versions/v0.5.0/DriveLink.exe`: nhận cấu hình và quản trị phiên bản OAuth qua Supabase.
- `versions/v0.6.0/DriveLink.exe`: Cloud lấy OAuth, kết nối mặc định, mật khẩu mã hóa riêng và quản trị ẩn.
- `versions/v0.7.0/DriveLink.exe`: nhận trực tiếp không đăng nhập reader, hộp mật khẩu admin và cảnh báo mật khẩu rõ ràng.
- `versions/v0.7.1/DriveLink.exe`: sửa lỗi đọc phản hồi đăng nhập, chọn email admin và SQL giữ lịch sử khi xóa tài khoản cũ.
- `versions/v0.7.2/DriveLink.exe`: chỉ hỏi mật khẩu quản trị, dùng admin mặc định còn lại.
- `versions/v0.8.0/DriveLink.exe`: kiểm tra cập nhật, tải/cài có tiến độ, chờ gửi/nhận và bộ cài Velopack. Dùng Setup từ Release để bật cập nhật trong app.
- `versions/v1.0.0/DriveLink.exe`: giao diện gọn, thu phóng, menu thao tác file và chia sẻ mới. Bản đã cài 0.8.0 có thể cập nhật trong app.
- `versions/v1.1.0/DriveLink.exe`: giao diện kính nền tối bố cục Bento, gửi chéo tài khoản trên máy chủ Google, thẻ dung lượng và biểu đồ tốc độ. Bản đã cài 0.8.0/1.0.0 có thể cập nhật trong app.
- `versions/v1.1.1/DriveLink.exe`: mọi cửa sổ phụ, hộp thông báo, menu chuột phải và trang kết quả đăng nhập dùng cùng giao diện kính tối; thanh tiêu đề tối trên Windows 11. Bản đã cài 0.8.0/1.0.0 có thể cập nhật trong app.
- `versions/v1.1.2/DriveLink.exe`: báo rõ khi bấm Tải về/Mở trên Drive mà chưa chọn tệp; giữ tệp đang chọn sau khi đóng Cài đặt. Bản đã cài 0.8.0/1.0.0 có thể cập nhật trong app.
- `versions/v1.2.0/DriveLink.exe`: bản cập nhật có chữ ký số, token Google bảo vệ bằng Windows Hello, tự thu hồi quyền tạm còn sót, giới hạn số lần nhập mật khẩu quản trị, cảnh báo tệp công khai và tệp chạy được, nhật ký bảo mật. Cần Windows 10 1809 trở lên.
- `versions/v1.3.0/DriveLink.exe`: tự thử lại và gửi/nhận tiếp sau khi tắt app, truyền song song, Hàng đợi & Hoạt động, tệp mới tức thì + Hộp thư đến, gửi chéo thư mục, mã hóa đầu cuối, truyền thẳng LAN, giao diện sáng, chế độ nhẹ cho máy ít RAM. Cần .NET Desktop Runtime 10 (Setup tự cài).
- Mỗi bản có ZIP mã nguồn để phục hồi. Bản mới có `release.json` ghi phiên bản và SHA-256.
- `versions/v1.3.1/DriveLink.exe`: đăng nhập lại một chạm khi phiên Google hết hạn; truyền thẳng LAN nhanh hơn (một kết nối cho cả lô, dây chuyền, băm trong lúc gửi), vẫn tương thích máy 1.3.0.
- `versions/v1.4.0/DriveLink.exe`: tự sắp xếp bố cục bento của màn hình chính (kéo thả đổi chỗ, đổi cỡ, ẩn/hiện ô, danh sách tệp bên trái/phải).
- Tạo phiên bản tiếp theo: chạy `scripts/Build-Release.ps1 -Version 1.4.1`. Script từ chối ghi đè phiên bản đã tồn tại. Các bản framework-dependent cần .NET Desktop Runtime 10 và toàn bộ tệp trong thư mục bản đó.

## Tính năng 1.3.0

- **Truyền tải bền và nhanh hơn:** tự thử lại khi mạng chập chờn, gửi/nhận tiếp sau khi tắt app (`Services/DriveTransfers.cs`), 2–6 tệp song song tự điều chỉnh (`Services/TransferLane.cs`, `Services/TransferCenter.cs`). Nút danh sách ở thanh bên mở **Hàng đợi & Hoạt động**.
- **Tệp mới tức thì:** `Services/InboxWatcher.cs` đọc nguồn thay đổi của Drive vài giây một lần. App báo bằng thông báo Windows và tự tải vào `Downloads\DriveLink - Hộp thư đến` (Cài đặt → Giao diện & hệ thống → Tệp mới từ người khác). Danh sách tệp hiện tên máy gửi và số máy đã nhận.
- **Gửi chéo thư mục và nhiều tệp**, chọn nơi nhận (`Services/CrossBatch.cs`, `CrossDestinationWindow.cs`). **Gửi nguyên cây thư mục** chọn trong Cài đặt → Nén thư mục.
- **Mã hóa đầu cuối** theo không gian: chuột phải không gian → Mã hóa đầu cuối (`Services/E2eCrypto.cs`, `E2eWindow.cs`).
- **Sắp xếp bố cục bento:** nút lưới ở thanh bên (`MainWindow.Arrange.cs`, `Ui/BentoLayout.cs`): kéo thả đổi chỗ, đổi cỡ, ẩn/hiện ô, đổi bên danh sách tệp; lưu trong Cài đặt.
- **Truyền thẳng trong LAN:** nút mạng ở thanh bên (`Services/Lan`, `LanWindow.cs`). Đo tốc độ gửi/nhận trên máy này: `dotnet run --project tests/DriveLink.Checks.csproj -c Release -- --lan-bench 5` (qua loopback, kèm tốc độ từng khâu: đọc/ghi ổ, SHA-256, AES-GCM).
- **Giao diện sáng/tối** (`Ui/Palette.*.xaml`, `Ui/ThemeChoice.cs`) và **chế độ nhẹ** cho máy ít RAM (`Ui/PerformanceMode.cs`). Đo RAM bằng `DriveLink.exe --ui-preview-main --lite`; xem giao diện sáng bằng `--light`.

## Bảo mật 1.2.0

Chi tiết và các việc chủ ứng dụng phải tự làm nằm trong [SECURITY.md](SECURITY.md). Tóm tắt:

- Bản cập nhật phải có chữ ký số của chủ ứng dụng (`Services/UpdateTrust.cs`). Khóa ký chỉ nằm trên máy phát hành, nên chiếm được GitHub cũng không đẩy được bản giả. GitHub Actions chỉ còn chạy kiểm thử; Dependabot báo bản vá NuGet/Actions.
- Token Google được mã hóa thêm bằng khóa Windows Hello, hoặc bằng khóa riêng trong Registry nếu máy không có Hello. Khóa nằm tách khỏi `accounts.dat` (`Services/TokenVault.cs`).
- Quyền tạm của Gửi chéo được ghi lại trước khi cấp, và tự thu hồi khi mở app nếu lần trước bị gián đoạn (`Services/GrantJournal.cs`).
- Mật khẩu quản trị: sai 3 lần chờ 15 phút, sai 9 lần thì máy bị khóa hẳn (`Services/AdminLockout.cs`). Sau khi chạy `supabase/upgrade-1.2.0.sql`, Cloud chặn theo địa chỉ IP.
- Quả cầu cam đánh dấu tệp công khai; bật "Bất kỳ ai có liên kết" phải xác nhận rủi ro. App cảnh báo trước khi tải hoặc mở thư mục của tệp chạy được (.exe, .ps1…) do người khác gửi.
- Nhật ký bảo mật trên máy nằm ở `%LOCALAPPDATA%\DriveLink\security-log.jsonl` và không ghi token (`Services/SecurityLog.cs`).

## Giao diện 1.1.0 và Gửi chéo

Từ 1.1.1, các cửa sổ phụ (Cài đặt, Cập nhật, Chia sẻ, Thông tin, Thành viên, Cloud OAuth, Mở quản trị, Đăng nhập Google), hộp thông báo và menu chuột phải dùng chung kiểu kính tối định nghĩa trong `App.xaml`; màu dùng trong code nằm ở `Ui/Theme.cs`. Hộp thông báo của Windows được thay bằng hộp thoại kính (`GlassMessageBox.cs`); đóng bằng X chọn phương án an toàn. Trang kết quả đăng nhập trên trình duyệt cũng dùng nền tối và thẻ kính. Menu ở khay hệ thống vẫn là menu Windows mặc định.

Giao diện kính trên nền tối, bố cục Bento một màn: danh sách tệp là thẻ chính; cột phải có Gửi tệp, Truyền tải, Không gian, Gửi chéo và Dung lượng. Cửa sổ rộng hiện hai cột thẻ; cửa sổ vừa hiện một cột và ẩn Gửi chéo/Dung lượng; cửa sổ hẹp thu gọn thanh trên. Thanh trên là vùng kéo cửa sổ, có nút thu nhỏ/phóng to/đóng riêng. Chuyển động tắt theo cài đặt giảm hiệu ứng của Windows và nền chỉ chạy khi cửa sổ đang được dùng.

Gửi chéo sao chép một tệp sang Drive của tôi của tài khoản Google khác đã thêm vào DriveLink. Nếu tài khoản đích đã xem được tệp, Google sao chép thẳng. Nếu chưa, app cấp quyền xem tạm (không gửi thư) cho đúng email đích, để tài khoản đích sao chép trên máy chủ, rồi thu hồi quyền trong mọi trường hợp. Bản sao được đối chiếu MD5/kích thước. Khi Google từ chối chia sẻ (thường do chính sách tổ chức), app hỏi trước khi tải tệp về thư mục tạm `%LOCALAPPDATA%\DriveLink\Relay` rồi tải lên tài khoản đích; thư mục tạm được xóa sau đó. Chưa hỗ trợ thư mục; tài liệu Google chỉ sao chép được trên máy chủ. Nếu không thu hồi được quyền tạm, app cảnh báo để gỡ thủ công trong Chia sẻ.

Bản dựng thử chỉ dùng dữ liệu minh họa: `DriveLink.exe --ui-preview` (giao diện tối) và `DriveLink.exe --ui-preview-glass` (kính + Bento).

## Giao diện 1.0.0

Danh sách file được ưu tiên trên màn hình nhỏ. Thu phóng bằng nút thanh bên, Ctrl +/− hoặc Ctrl + cuộn; Ctrl + 0 tự điều chỉnh. Tỷ lệ, font và kích thước nút cùng thay đổi. Các cửa sổ được giới hạn theo vùng màn hình. Ở tỷ lệ lớn, thanh công cụ được rút gọn; dùng menu chuột phải để tải xuống hoặc mở trên Drive khi các nút dưới bị ẩn.

Email được che trong giao diện bằng tối đa 6 ký tự đầu + 6 dấu * + tên miền. Việc tìm kiếm và gửi lời mời vẫn dùng địa chỉ email thật. Che hiển thị không thay đổi danh tính hay quyền truy cập.

Chuột phải vào file: đổi tên, tải xuống, tạo bản sao vào không gian hiện tại, chia sẻ, thông tin, Thùng rác. Có thể dùng Shift+F10. Thông tin chỉ có chi tiết và khóa/mở khóa. Khóa là content restriction của Drive: ngăn sửa tên/nội dung/nhận xét, không chặn xem/tải hay chia sẻ; tài khoản có quyền phù hợp có thể mở khóa lại.

Cửa sổ chia sẻ dùng chung cho file và Thành viên không gian. Hỗ trợ người/nhóm, xem/nhận xét/chỉnh sửa, thu hồi quyền trực tiếp, liên kết và tên miền Workspace. Quyền kế thừa cần đổi ở nơi cấp quyền gốc. Chuyển sở hữu, thời hạn và thiết lập tổ chức dùng nút mở các tùy chọn khác trên Drive. Kiểm tra API dùng dữ liệu mô phỏng, không gửi lời mời hoặc thay đổi file thật trong quá trình kiểm thử.

Tài liệu: [Chia sẻ trên Drive](https://developers.google.com/workspace/drive/api/guides/manage-sharing), [Khóa chỉnh sửa](https://developers.google.com/workspace/drive/api/guides/content-restrictions).

## Không gian, thư mục và tùy chọn mới

- Chuột phải không gian để ghim/bỏ ghim, chọn màu, ẩn hoặc đưa thư mục vào Thùng rác Google Drive. Xóa yêu cầu quyền canTrash do Google trả về và xác nhận trong app; không gọi xóa vĩnh viễn. Khôi phục ở Thùng rác Drive. Ẩn chỉ thay đổi danh sách trên máy; Cài đặt có nút hiện lại.
- Màu và ghim lưu cục bộ, không tự đồng bộ sang máy khác. Mục ghim được xếp trên cùng rồi sắp theo tên.
- Cài đặt → Giao diện & hệ thống: độ đậm nền kéo thả 0–100%, bật/tắt lớp thông báo, chạy nền trong khay hệ thống, chạy khi đăng nhập Windows. Tắt lớp thông báo không tắt khả năng thả file.
- Chạy nền và khởi động Windows mặc định tắt. Bật chạy nền: nút X/thu nhỏ ẩn cửa sổ nhưng truyền file/cập nhật vẫn chạy. Nhấp đúp icon cạnh đồng hồ để mở lại, chuột phải → Thoát hoàn toàn để đóng. Nếu chỉ bật khởi động Windows, app mở bình thường; nếu bật cả hai thì mở trong khay. Windows có thể trì hoãn khởi động. Khi lưu cài đặt với khởi động bật, đường dẫn đăng ký được cập nhật sang phiên bản đang chạy; không di chuyển/xóa thư mục bản đó khi vẫn dùng đăng ký.
- Thả cả thư mục hoặc bấm Chọn thư mục: tạo một ZIP/RAR rồi gửi. ZIP mặc định, dùng 7-Zip nếu có, nếu không dùng WinRAR, cuối cùng ZIP tích hợp. RAR cần WinRAR, 7-Zip không tạo RAR. Có lựa chọn nén mạnh; hiệu quả thực tế tùy dữ liệu, file ảnh/video/đã nén có thể giảm ít hoặc không giảm.
- Công cụ được tìm ở thư mục Program Files/Program Files (x86) thông thường. Không tự tải/cài công cụ. Lỗi hoặc cảnh báo của công cụ nén sẽ ngăn gửi bản không hoàn chỉnh. Không hỗ trợ thư mục có symbolic link/junction/reparse point hoặc chọn cả ổ đĩa; không tự lấy dữ liệu bên ngoài nguồn.
- File nén được tạo tạm trong `%LOCALAPPDATA%\DriveLink\UploadArchives`, nguồn được giữ nguyên. Không chọn thư mục bao trùm chính vùng tạm này. Sau lượt gửi, file nén tạm được dọn; lịch sử cục bộ trỏ tới thư mục gốc. Nếu app bị tắt cưỡng bức, file tạm có thể còn lại. Máy nhận tải file nén rồi tự giải nén bằng công cụ phù hợp.
- Mở thư mục dùng Windows Shell trên luồng STA riêng, kiểm tra file/thư mục cũng ngoài luồng giao diện. Tránh gọi explorer.exe đồng bộ; không bảo đảm Explorer hiển thị tức thì nếu Windows, ổ đĩa hoặc mạng chậm.
- Thêm tài khoản Google khác trên cùng máy không cần JSON mới: dùng cấu hình đã nhập và bấm + Tài khoản. Chế độ Testing cần thêm email mới vào Test users. Máy mới chưa có cấu hình phải nhập cùng JSON một lần; đổi OAuth Client mới cần cấu hình lại.
- Cấu hình giao diện/màu/ghim lưu mã hóa trong `preferences.dat` bên cạnh `accounts.dat`, để phiên bản cũ ghi lại tài khoản không làm mất các tùy chọn mới.

Khi kết nối Google, cửa sổ nhỏ hiển thị trạng thái chờ. Sau khi có kết quả, cửa sổ hiện thành công hoặc thất bại rồi tự đóng sau 3 giây. Trang kết quả trên trình duyệt vẫn mở để xem lại, có icon đầy đủ, tên Quang Huy và số phiên bản. Thông tin tài khoản được giữ tại vị trí cũ; đổi bản chạy không yêu cầu đăng nhập lại khi kết nối còn hợp lệ.

Ứng dụng Windows C# / .NET 10 / WPF, chia sẻ file giữa các máy dùng tài khoản Google khác nhau. Không tạo ổ đĩa ảo, không cần máy chủ trung gian riêng.

## Bản đầu có gì?

- Giao diện tiếng Việt, danh sách file gần đây, lọc file mình sở hữu / người khác sở hữu và tìm theo tên hoặc email.
- Nhiều tài khoản Google trên một máy; chọn một tài khoản hoạt động tại một thời điểm.
- OAuth Desktop: trình duyệt hệ thống, PKCE S256, kiểm tra state, callback loopback và thời hạn chờ 3 phút.
- Lưu cấu hình và token bằng Windows DPAPI CurrentUser trong `%LOCALAPPDATA%\DriveLink\accounts.dat`. Không lưu mật khẩu Google. Dữ liệu được giữ qua cập nhật ứng dụng nếu không đổi OAuth Client.
- Tự gia hạn access token bằng refresh token khi cần.
- Tạo thư mục `[DriveLink] Tên không gian`; tìm các thư mục có nhãn này mà tài khoản được phép truy cập.
- Mời email với quyền reader/writer; xem và thu hồi quyền trực tiếp trên thư mục.
- Hai hàng đợi gửi/nhận độc lập: một file tải lên và một file tải xuống có thể truyền đồng thời; các file còn lại chờ theo thứ tự trong từng chiều. Hủy gửi không hủy nhận và ngược lại.
- Resumable upload theo khối 8 MiB, hiển thị tiến trình và tốc độ gửi/nhận riêng (B/s, KB/s, MB/s). Tốc độ đo từ dữ liệu file ứng dụng truyền, không phải tổng lưu lượng mạng toàn máy; về 0 khi không có dữ liệu mới.
- Kéo thả file/thư mục ở bất kỳ đâu trong phần nội dung cửa sổ chính, kể cả danh sách file, ô tìm kiếm hoặc thanh bên. File được gửi vào không gian đang chọn, thêm vào hàng đợi nếu đang gửi. Thư mục được nén theo cài đặt trước khi tải lên.
- Nhấn vào hàng file để mở thư mục trên máy nếu file đã được gửi hoặc tải về bằng phiên bản này. Đường dẫn được lưu mã hóa qua các lần khởi động. File chỉ có trên cloud hoặc đã di chuyển sẽ được báo cần tải về, không tự tải xuống.
- Chuột phải vào đúng hàng file → Sao chép link mở trên web / Mở thư mục trên máy. Sao chép link không thay đổi quyền truy cập.
- Trang callback 127.0.0.1 hiển thị kết quả đăng nhập tiếng Việt: chỉ báo thành công sau khi xác thực và lưu tài khoản hoàn tất; hiện thất bại nếu bị từ chối, state không hợp lệ hoặc xử lý kết nối lỗi. Trang không lưu cache hay tải tài nguyên từ website khác.
- Tải về theo luồng, ghi file tạm và chỉ thay file đích sau khi tải xong.
- Cập nhật file mỗi 15 giây, tìm không gian mới mỗi 60 giây khi ứng dụng đang mở và có mạng. Nút ↻ cập nhật ngay cả không gian. Khi có lỗi, tự cập nhật tạm dừng đến khi bấm làm mới thành công.
- Chế độ xem thử với dữ liệu minh họa, không gửi/tải file thật.

## Chạy và kiểm tra

Máy phát triển cần Windows và .NET SDK 10 có thành phần Windows Desktop.

```powershell
dotnet build DriveLink.csproj -c Release
dotnet run --project DriveLink.csproj
dotnet run --project tests/DriveLink.Checks.csproj -c Release
```

Ứng dụng sau khi build: `bin\Release\net10.0-windows10.0.19041.0\DriveLink.exe` (cần Windows 10 1809 trở lên). Cần .NET Desktop Runtime 10 trên máy chạy. Khi phân phối hãy giữ cả thư mục đầu ra, không chỉ sao chép EXE.

Đóng gói độc lập với runtime (cần tải runtime từ NuGet nếu chưa có):

```powershell
dotnet publish DriveLink.csproj -c Release -r win-x64 --self-contained true -o release
```

## Thiết lập Google một lần

1. Tạo/chọn dự án tại https://console.cloud.google.com/ và bật **Google Drive API**.
2. Vào **Google Auth Platform**, cấu hình tên ứng dụng, email liên hệ và Audience. Nếu dùng External / Testing, thêm cả hai tài khoản Google vào Test users.
3. Trong Data Access, cấu hình quyền `openid`, `email` và `https://www.googleapis.com/auth/drive`.
4. Tạo OAuth Client loại **Desktop app**, tải tệp JSON. Đây phải là cấu trúc `installed`, không phải `web`.
5. Trong DriveLink: **Kết nối và tài khoản → Nhập tệp OAuth Desktop → Lưu cấu hình → + Tài khoản**.
6. Dùng cùng OAuth Client/config trên cả hai máy. Mỗi máy đăng nhập tài khoản Google riêng.

Bản đầu dùng quyền Drive đầy đủ để tìm và truy cập các thư mục đã được chia sẻ giữa tài khoản. Đây là restricted scope: không phát hành rộng khi chưa xem xét yêu cầu xác minh Google. Hướng quyền hẹp `drive.file` cần tích hợp chọn/ủy quyền thư mục, không chỉ thay chuỗi scope. Cấu hình/token không được đưa vào mã nguồn hoặc log.

Testing với quyền Drive thường làm refresh token hết hạn sau 7 ngày. Chuyển sang Production để tránh giới hạn riêng của Testing; Production không bảo đảm token vĩnh viễn và không tự hoàn thành việc xác minh. Quyền có thể bị thu hồi và tài khoản tổ chức có thể chặn chia sẻ.

## Kiểm chứng trên hai máy

1. Máy A kết nối tài khoản A và tạo một không gian.
2. Bấm **Thành viên**, nhập email B, chọn **Gửi, chỉnh sửa và tải về**, bấm mời. Đây là lời mời thật, Google có thể gửi email thông báo.
3. Máy B kết nối tài khoản B bằng cùng OAuth Client, bấm ↻. Không gian `[DriveLink]` sẽ xuất hiện nếu Google đã cấp quyền.
4. Máy A gửi một file nhỏ. Đợi tải lên hoàn tất. Máy B sẽ thấy file ở lần cập nhật tiếp theo (thông thường trong khoảng 15 giây, có thể chậm hơn do mạng/API).
5. Máy B tải về, đối chiếu nội dung hoặc SHA-256 với file gốc. Thử gửi ngược B → A.
6. Đóng/mở lại cả hai ứng dụng để kiểm tra giữ phiên. Thử tài khoản chỉ có quyền reader: xem/tải được, tải lên bị Google từ chối.
7. Thử thu hồi quyền thư mục, tài khoản hết quyền, mất mạng và hủy tải.

## Những giới hạn cần biết

- File trên My Drive do người tải lên sở hữu và thường dùng quota của người đó. Một thư mục dùng chung không gộp dung lượng các tài khoản. Shared drives của tổ chức chưa nằm trong phạm vi bản đầu.
- File gần đây sắp theo **thời gian tạo trong không gian**, không phải nhật ký sự kiện chia sẻ. “Tôi đã gửi” dùng thuộc tính chủ sở hữu, chưa có lịch sử gửi hay tên máy gửi.
- Chỉ xem file con trực tiếp của thư mục không gian. Chưa duyệt thư mục lồng nhau, tải cả thư mục hoặc xuất Google Docs/Sheets/Slides. Dùng **Mở trên Drive** cho các mục này.
- Chỉ cấp reader/writer trên thư mục, chưa chỉnh quyền riêng từng file, quyền commenter, quyền hết hạn hoặc link công khai. Mời lại cùng email không phải luồng đổi quyền; quản lý thay đổi trên Google Drive trong bản này.
- Thu hồi quyền thư mục không tự thu hồi quyền riêng đã cấp trên từng file, không xóa file của thành viên và không xóa bản đã tải về.
- Gửi/nhận tự thử lại khoảng 2 phút khi mạng chập chờn và tiếp tục được sau khi tắt app (phiên tải lên của Google có hiệu lực khoảng 1 tuần). Thư mục gửi dạng nén hoặc tệp mã hóa đầu cuối thì được nén/mã hóa lại và gửi từ đầu. Hủy một chiều sẽ bỏ cả các file đang chờ của chiều đó; file đã gửi/tải xong được giữ nguyên.
- Tự cập nhật khi app đang chạy, gồm cả khi ẩn trong khay hệ thống. Tệp mới được phát hiện bằng cách hỏi Google vài giây một lần (không phải push); dấu "đã nhận" chỉ ghi được khi máy nhận có quyền sửa tệp. Máy tắt/ngủ hoặc mất mạng thì không truyền/nhận file.
- “Thu hồi quyền và xóa tài khoản khỏi máy này” gọi Google thu hồi token rồi mới xóa khỏi máy. Nếu không có mạng, thu hồi tại https://myaccount.google.com/connections . Không app nào phát hiện chắc chắn việc một phần mềm khác đọc tệp; SECURITY.md giải thích cách token được tách khóa và bảo vệ bằng Windows Hello.
- Bộ cài chưa được ký mã Windows (Authenticode), nên Windows có thể cảnh báo về nhà phát hành; riêng bản cập nhật được ký bằng khóa riêng của chủ ứng dụng. Chưa có đánh giá bảo mật độc lập. Chưa thể xác nhận tích hợp Google đầu cuối khi chưa có OAuth Client và hai tài khoản thực.

## Tài liệu chính thức

- OAuth Desktop: https://developers.google.com/identity/protocols/oauth2/native-app
- Phạm vi Drive: https://developers.google.com/workspace/drive/api/guides/api-specific-auth
- Upload: https://developers.google.com/workspace/drive/api/guides/manage-uploads
- Chia sẻ: https://developers.google.com/workspace/drive/api/guides/manage-sharing
- ZIP/RAR: https://www.win-rar.com/rar-vs-zip.html?L=1
- Các định dạng 7-Zip: https://www.7-zip.org/

## Cấu trúc

`MainWindow.xaml`: giao diện. `MainWindow.xaml.cs`: tương tác, bộ lọc và cập nhật định kỳ. `Dialogs.cs`: tài khoản, cấu hình và thành viên. `Services/GoogleAuth.cs`: OAuth. `Services/SecureStore.cs`: lưu mã hóa. `Services/DriveService.cs`: API, phân trang và truyền file. `tests`: kiểm tra bằng HTTP giả lập, không gọi Google thật.

`Services/OAuthCallbackServer.cs`: trang kết quả đăng nhập. `Services/TransferLane.cs`: hàng đợi từng chiều. `Services/TransferMeter.cs`: đo tốc độ truyền. `Services/LocalFileHistory.cs`: đường dẫn file trên máy. Khi truyền file, ứng dụng giữ tài khoản và không gian hiện tại để tránh gửi nhầm; vẫn dùng được tìm kiếm, chọn file và thêm lượt gửi/nhận.



