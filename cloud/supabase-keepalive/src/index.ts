// Gọi hàm public.drivelink_keepalive() (supabase/keepalive.sql) để Supabase ghi nhận có hoạt động cơ sở dữ liệu.
// Thử tối đa 3 lần; nếu vẫn lỗi thì báo lỗi để lần chạy hiện "failed" trong Cloudflare (Workers → drivelink-supabase-keepalive → Logs).

interface Env {
  SUPABASE_URL: string;
  SUPABASE_PUBLISHABLE_KEY: string;
}

const attempts = 3;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function ping(env: Env): Promise<string> {
  const response = await fetch(`${env.SUPABASE_URL.replace(/\/+$/, "")}/rest/v1/rpc/drivelink_keepalive`, {
    method: "POST",
    headers: { apikey: env.SUPABASE_PUBLISHABLE_KEY, "Content-Type": "application/json" },
    body: "{}",
  });
  const text = (await response.text()).slice(0, 300);
  if (response.ok) return text;
  if (response.status === 404) throw new Error("Chưa có hàm drivelink_keepalive. Chạy supabase/keepalive.sql trong Supabase → SQL Editor.");
  if (response.status === 540 || response.status === 503) throw new Error(`Supabase trả ${response.status}: project có thể đang bị tạm dừng. Khôi phục trong trang quản lý Supabase.`);
  throw new Error(`Supabase trả ${response.status}: ${text}`);
}

export default {
  async scheduled(_controller: unknown, env: Env): Promise<void> {
    let last: unknown;
    for (let i = 1; i <= attempts; i++) {
      try {
        const stamp = await ping(env);
        console.log(`Supabase keepalive OK (lần thử ${i}): ${stamp}`);
        return;
      } catch (error) {
        last = error;
        console.warn(`Supabase keepalive lỗi (lần thử ${i}/${attempts}): ${error instanceof Error ? error.message : String(error)}`);
        if (i < attempts) await wait(30_000 * i);
      }
    }
    throw last instanceof Error ? last : new Error(String(last));
  },
};
