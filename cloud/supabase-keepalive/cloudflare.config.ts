// Worker hẹn giờ giữ project Supabase miễn phí của DriveLink không bị tạm dừng vì ít hoạt động.
// Triển khai: xem README.md trong thư mục này.
import { bindings, defineConfig, triggers } from "cf/config";
import * as entrypoint from "./src/index.ts" with { type: "cf-worker" };

export default defineConfig({
  worker: {
    name: "drivelink-supabase-keepalive",
    compatibilityDate: "2026-11-01",
    entrypoint,
    // Không mở địa chỉ web công khai: Worker chỉ chạy theo lịch.
    workersDev: false,
    // Lưu nhật ký mỗi lần chạy để xem trong Cloudflare → Workers → drivelink-supabase-keepalive → Logs.
    observability: { enabled: true, logs: { enabled: true } },
    // 03:17 UTC (10:17 giờ Việt Nam) mỗi 2 ngày; Supabase chỉ tạm dừng sau khoảng 7 ngày ít hoạt động.
    triggers: [triggers.scheduled({ schedule: "17 3 */2 * *" })],
    env: {
      SUPABASE_URL: bindings.text("https://rjpnjclnifhnvolgbrom.supabase.co"),
      // Khóa sb_publishable_ (công khai, giống khóa trong app). Không bao giờ dùng secret/service_role key ở đây.
      SUPABASE_PUBLISHABLE_KEY: bindings.secret(),
    },
  },
});
