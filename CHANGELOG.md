# v1.3.1

DriveLink 1.3.1 — Quang Huy · Đăng nhập lại một chạm và truyền LAN nhanh hơn

- Đăng nhập lại một chạm khi Google báo phiên đăng nhập hết hạn hoặc bị thu hồi (invalid_grant, ví dụ OAuth app còn ở chế độ Testing quá 7 ngày): tài khoản hiện "cần đăng nhập lại", app hỏi một lần (hoặc gửi thông báo Windows khi đang ẩn), trình duyệt mở sẵn đúng email; tài khoản, không gian và cài đặt giữ nguyên. Chọn nhầm tài khoản khác thì không lưu gì. Menu tài khoản luôn có mục Đăng nhập lại.
- Lỗi cấu hình OAuth Client (invalid_client) được báo riêng, không nhầm với hết phiên.
- Truyền thẳng LAN nhanh hơn:
  - Không đọc tệp hai lần: mã SHA-256 được tính trong lúc gửi và gửi ở cuối mỗi tệp.
  - Đọc ổ, mã hóa và gửi mạng chạy song song theo dây chuyền (máy nhận cũng vậy với nhận, giải mã và ghi ổ).
  - Dùng lại vài bộ đệm 1 MB thay vì cấp phát mới mỗi khối.
  - Nhiều tệp đi qua một kết nối, chỉ bắt tay một lần; báo nhận và ghi nhật ký một lần cho cả lô.
  - Gửi ngay các gói nhỏ (tắt Nagle), bộ đệm socket 1 MB, đặt trước kích thước tệp nhận.
  - Đo trên cùng một máy qua loopback (`--lan-bench`, trung vị 5 lần, hai vòng xen kẽ bản cũ/mới): 1 tệp 1 GB 11,6 s → 2,7–4,4 s; 300 tệp 100 KB 4,1–7,9 s → 1,2–2,1 s; bộ nhớ cấp phát khi gửi 1 GB 3.078 MB → 15 MB. Qua mạng thật, tốc độ vẫn bị giới hạn bởi Wi-Fi/cáp và ổ đĩa.
  - Vẫn gửi/nhận được với máy chạy 1.3.0 (hai máy báo phiên bản giao thức khi bắt tay; với máy cũ thì dùng cách cũ).
- Thêm 9 kiểm thử (tổng 361).

# v1.3.0

DriveLink 1.3.0 — Quang Huy · Truyền tải, thông báo, mã hóa đầu cuối, LAN, giao diện sáng và chế độ nhẹ

- Truyền tải bền hơn: mạng chập chờn thì tự thử lại khoảng 2 phút (chỉ với lỗi mạng, hết thời gian chờ, 429, 5xx). Gửi tiếp đúng từ chỗ Google đã nhận, nhận tiếp bằng Range, đối chiếu MD5 trước khi thay tệp đích. Tắt app giữa chừng thì lần mở sau hỏi để tiếp tục.
- Gửi/nhận 2–6 tệp song song, tự điều chỉnh theo tốc độ mạng. Chọn nhiều tệp để tải về một lần.
- Màn Hàng đợi & Hoạt động: từng lượt đang chạy/đang chờ (hủy riêng từng lượt) và nhật ký 300 lượt gần nhất (mã hóa trên máy), có nút mở thư mục.
- Tệp mới hiện gần như ngay lập tức (nguồn thay đổi của Drive, vài giây một lần, ở mọi tài khoản). Thông báo Windows, tự tải vào Downloads\DriveLink - Hộp thư đến\<không gian> (trừ tệp chạy được và tệp vượt giới hạn). Tệp ghi tên máy gửi; máy nhận ghi dấu "đã nhận" để người gửi thấy số máy đã nhận.
- Gửi chéo nhiều tệp và cả thư mục (giữ cấu trúc), chọn nơi nhận: Drive của tôi hoặc một không gian của tài khoản đích. Thư mục chỉ cần một quyền xem tạm trên thư mục gốc, thu hồi khi xong.
- Gửi nguyên cây thư mục (tùy chọn trong Cài đặt → Nén thư mục), thay cho nén thành một tệp.
- Mã hóa đầu cuối tùy chọn theo không gian: mật khẩu không gian dùng chung giữa các thành viên, AES-256-GCM theo khối, Google chỉ nhận bản mã (.dlenc). Có mã kiểm tra để các thành viên so với nhau.
- Truyền thẳng trong mạng LAN: tự tìm máy DriveLink cùng mạng, ghép nối bằng mã 6 số, gửi tệp trực tiếp có mã hóa và kiểm SHA-256. Mặc định tắt.
- Giao diện sáng (Cài đặt → Màu giao diện). Menu khay hệ thống kiểu tối, có mục Hàng đợi & Hoạt động.
- Chế độ nhẹ cho máy ít RAM (tự bật khi máy có từ 4 GB RAM trở xuống): vẽ bằng CPU, không nạp trình điều khiển đồ họa, tắt hiệu ứng mờ và chuyển động, trả bộ nhớ cho Windows khi ẩn vào khay. Đo trên cửa sổ chính: bộ nhớ riêng khoảng 87 MB (đầy đủ khoảng 167 MB), đỉnh lúc mở khoảng 186 MB.
- Bộ cài nhỏ hơn: app không kèm .NET (Setup tự cài .NET Desktop Runtime 10 nếu máy chưa có). Gói cập nhật 87 MB → 11 MB, Setup 95 MB → 18,5 MB. Bản cập nhật kế tiếp có gói delta (khoảng 0,5 MB từ 1.2.0).
- Thư mục dự án là git repo, nối với lịch sử phát hành trên GitHub.
- Thêm 57 kiểm thử (tổng 352).

# v1.2.0

DriveLink 1.2.0 — Quang Huy · Bảo mật

- Bản cập nhật phải có chữ ký số ECDSA P-256 của chủ ứng dụng. Trước khi báo có bản mới, app kiểm `DriveLink-release-manifest.json` và tệp `.sig` trên Release, rồi đối chiếu tên, SHA-256 và số phiên bản của gói Velopack. Bản không ký hoặc ký sai bị chặn và ghi vào nhật ký; bản đang dùng được giữ nguyên. Có các script tạo khóa, ký, kiểm và phát hành trên máy: `scripts/New-UpdateSigningKey.ps1`, `Sign-Release.ps1`, `Verify-Release.ps1`, `Publish-Release.ps1`.
- GitHub Actions chỉ chạy kiểm thử (`verify.yml`, quyền `contents: read`, action ghim theo mã commit) và không còn tự phát hành. Thêm Dependabot cho NuGet và GitHub Actions.
- Token Google được mã hóa thêm bằng AES-256-GCM, với khóa Windows Hello (xác nhận khi mở app) hoặc khóa riêng trong Registry khi máy không có Hello. `accounts.dat` dùng thêm khóa phụ DPAPI. Token cũ được mã hóa lại tự động, nhưng bản 1.1.x không đọc được tệp đã nâng cấp. App giờ cần Windows 10 1809 trở lên.
- Gửi chéo ghi quyền tạm vào một nhật ký mã hóa trước khi cấp. Nếu app bị tắt ngang hoặc thu hồi lỗi, lần mở sau tự thu hồi quyền còn sót và dọn thư mục tạm của lần tải qua máy.
- Mật khẩu quản trị Cloud: sai 3 lần phải chờ 15 phút, sai 9 lần thì máy bị khóa hẳn. Trạng thái lưu ở hai nơi; dữ liệu bị sửa thì máy bị khóa. SQL `supabase/upgrade-1.2.0.sql` đếm và chặn theo địa chỉ IP ở máy chủ, chặn cả đọc danh sách thành viên và phát hành cấu hình.
- Tệp đang công khai có biểu tượng quả cầu; bật "Bất kỳ ai có liên kết" phải xác nhận rủi ro. App cảnh báo trước khi tải hoặc mở thư mục của tệp chạy được do người khác gửi.
- Xóa tài khoản giờ thu hồi token tại Google trước. Cài đặt → Tài khoản Google có phần Bảo mật và nút mở nhật ký bảo mật (`security-log.jsonl`, không ghi token hay email đầy đủ).
- Thêm 33 kiểm thử bảo mật (tổng 295).

# v1.1.2

DriveLink 1.1.2 — Quang Huy

- Bấm Tải về hoặc Mở trên Drive khi chưa chọn tệp giờ được báo rõ: thông báo "Chưa chọn tệp", thẻ danh sách tệp sáng lên và dòng trạng thái hướng dẫn chọn tệp (trước đây app không phản hồi gì).
- Đóng Cài đặt không còn làm mất tệp đang chọn; danh sách tải lại sẽ chọn lại đúng tệp đó. Đổi tài khoản hoặc không gian vẫn bỏ chọn như trước.
- Đã thử trên app thật với hai tài khoản Google: gửi chéo (sao chép trên máy chủ, 0 MB qua máy), dung lượng, tải về, menu chuột phải, Cài đặt và đổi tài khoản.

# v1.1.1

DriveLink 1.1.1 — Quang Huy

- Mọi cửa sổ phụ dùng giao diện kính tối như cửa sổ chính: Cài đặt (tab viên thuốc, ô tích và thanh trượt mới), Cập nhật, Chia sẻ, Thông tin, Thành viên, Cloud OAuth, Mở quản trị, Đặt tên và Đăng nhập Google. Thanh tiêu đề của các cửa sổ này chuyển tối trên Windows 11.
- Hộp thông báo/xác nhận của Windows được thay bằng hộp thoại kính có biểu tượng theo loại và nút "Có/Không" tiếng Việt; đóng bằng X chọn phương án an toàn. Ctrl+C chép nội dung thông báo.
- Menu chuột phải, menu con, chú thích và danh sách thả xuống dùng nền kính tối.
- Trang kết quả đăng nhập trên trình duyệt chuyển sang nền tối, thẻ kính và nhãn trạng thái phát sáng.
- Sửa hướng dẫn cũ trong Cài đặt: nút thu phóng nằm ở thanh trên cùng.
- Bộ kiểm tra tự chụp ảnh các cửa sổ phụ, hộp thoại, menu và trang đăng nhập vào artifacts/ui-v[phiên bản].

# v1.1.0

DriveLink 1.1.0 — Quang Huy

- Giao diện kính trên nền tối, bố cục Bento một màn: danh sách tệp là thẻ chính; cột widget gồm Gửi tệp, Truyền tải, Không gian, Gửi chéo và Dung lượng. Thanh trên nổi có tài khoản, tìm kiếm, thu phóng và nút cửa sổ tự vẽ; dock bên trái cho bộ lọc tệp, cài đặt và cập nhật.
- Bố cục tự co giãn: cửa sổ rộng hai cột widget, vừa một cột, hẹp thì thanh trên và tiêu đề tự thu gọn để nút đóng cửa sổ luôn hiện.
- Gửi chéo tài khoản: Google sao chép tệp trên máy chủ sang Drive của tài khoản Google khác đã thêm vào DriveLink, không tốn băng thông máy. Quyền xem tạm chỉ cấp cho đúng email đích, không gửi thư, và luôn được thu hồi kể cả khi lỗi hoặc hủy. Bản sao được đối chiếu MD5/kích thước. Khi Google chặn chia sẻ, app hỏi trước khi tải qua máy. Chưa hỗ trợ thư mục.
- Widget Dung lượng đọc dung lượng thật của tài khoản đang chọn; hiển thị TB cho tài khoản lớn.
- Truyền tải hiển thị tốc độ tải lên/tải về và biểu đồ theo thời gian thực; biểu tượng màu theo loại tệp trong danh sách.
- Chuyển động và tương tác nhỏ: thẻ xuất hiện so le, nổi nhẹ khi rê chuột, thẻ Gửi tệp sáng lên khi kéo tệp, thông báo dạng "đảo" khi gửi/nhận/gửi chéo xong. Nền chuyển động chỉ chạy khi cửa sổ đang dùng (15 khung hình/giây) và tự tắt khi Windows bật giảm hiệu ứng.
- Lỗi Google Drive mang mã HTTP để phân biệt "chưa thấy tệp" và "bị chặn chia sẻ". Bản dựng thử giao diện mở bằng tham số --ui-preview và --ui-preview-glass.

# v1.0.0

DriveLink 1.0.0 — Quang Huy

- Giao diện mới ưu tiên danh sách file: thu nhỏ khối không gian và đưa nút chọn thư mục/file lên trên. Dòng file gọn hơn, thanh bên bỏ các đoạn giới thiệu cũ.
- Thu phóng 60–150%, tự điều chỉnh 80% trên màn hình có vùng hiển thị nhỏ. Nút −/+ ở thanh bên, Ctrl +/−, Ctrl + cuộn chuột và Ctrl + 0. Tỷ lệ được lưu trên máy.
- Danh sách tài khoản có mục thêm tài khoản Google. Thành viên nằm trên thanh công cụ. Email hiển thị tối đa 6 ký tự đầu, tiếp theo đúng 6 dấu * và tên miền.
- Trạng thái làm mới chuyển lên trên, giờ cập nhật tô xanh.
- Menu file: đổi tên, tải xuống, tạo bản sao, chia sẻ, thông tin và chuyển vào Thùng rác.
- Chia sẻ có thêm người/nhóm, người xem/nhận xét/chỉnh sửa, thông báo và lời nhắn; đổi hoặc thu hồi quyền; quyền liên kết bị hạn chế/bất kỳ ai/tên miền Workspace; tùy chọn chia sẻ của người chỉnh sửa và tải/in/sao chép. Quyền kế thừa và chính sách Google được tôn trọng.
- Thông tin chi tiết và khóa/mở khóa chỉnh sửa bằng Google Drive. Khóa không chặn xem hoặc tải xuống.

Đang dùng bản 0.8.0 đã cài bằng Setup: mở Kiểm tra cập nhật trong app để tải và cài 1.0.0. Nếu dùng EXE/ZIP cũ, thoát app và cài **QuangHuy.DriveLink-win-x64-Setup.exe** một lần. Tài khoản, OAuth và cài đặt đã lưu được giữ lại. Không cần nâng cấp SQL Cloud cho bản này.

Những tùy chọn nâng cao như chuyển quyền sở hữu, thời hạn truy cập hoặc quản trị tổ chức có thể mở bằng nút “Các tùy chọn khác trên Google Drive” trong cửa sổ chia sẻ. Tài liệu Google/thư mục vẫn mở trên Drive để xuất hoặc tải phù hợp. Bộ cài Windows x64 đã có .NET; chưa ký mã Windows.


# v0.8.0

Kiểm tra và cài cập nhật từ GitHub Releases công khai bằng Velopack. Thông báo bản mới, xem thay đổi, tiến độ/hủy tải, chờ truyền file hoàn tất và giữ dữ liệu khi nâng cấp. Bộ cài Setup và Portable có sẵn .NET; tự động tạo nguồn cập nhật cho các phiên bản sau.

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


