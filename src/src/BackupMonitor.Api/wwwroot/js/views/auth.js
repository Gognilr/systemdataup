/* js/views/auth.js —— 登录 / 强制改密（OPEN-ISSUES #2） */
import { store, api, setAccessToken, clearAuth } from '../api.js';
import { App } from '../state.js';
import { $, esc, toast, errToast } from '../ui.js';

/* 指纹按 4 位分组（实施方案 U4）。一串 64 位十六进制、比例字体、不分组，
   要拿它和部署记录逐字核对基本保证会看错。复制时给的仍是不带空格的原值。 */
function groupHex(hex, size = 4) {
  const clean = String(hex).replace(/[^0-9a-fA-F]/g, '').toUpperCase();
  return clean.replace(new RegExp(`(.{${size}})`, 'g'), '$1 ').trim();
}

/* 身份区的内容异步补进来：取不到就整块留空，绝不能因为一个附加信息让人登不上去。
   显示指纹不是装饰——LAN Turnkey 形态下「我连的是不是那台服务器」本来就要人工核对，
   而此前管理网页上没有任何地方看得到它。 */
async function fillServerIdentity() {
  let identity;
  try {
    identity = await api('/api/v1/public/server-identity', { noAuth: true });
  } catch (e) {
    return;
  }
  const box = $('#lg_facts');
  if (!box || !identity) return;

  const rows = [];
  // Secure 形态下 TLS 由 nginx 终结，服务端手里没有证书、指纹为 null。
  // 那时整行不显示——一个空着的指纹栏比没有这一栏更容易被误读成「核对过了」。
  if (identity.fingerprint)
    rows.push(`<dt>服务端指纹</dt><dd class="mono">${esc(groupHex(identity.fingerprint))}</dd>`);
  if (identity.version)
    rows.push(`<dt>版本</dt><dd class="mono">${esc(identity.version)}</dd>`);
  if (!rows.length) return;

  box.innerHTML = rows.join('');
  if (identity.fingerprint)
    $('#lg_hint').textContent = '登录前请核对指纹与部署记录一致。';
}

export function vLogin() {
  $('#app').innerHTML = `<div class="login-wrap"><div class="login-shell">
    <aside class="login-brand">
      <div class="brand-name">BackupMonitor</div>
      <div class="brand-sub">轻量级集中备份采集与监控</div>
      <dl class="brand-facts" id="lg_facts"></dl>
      <p class="brand-hint" id="lg_hint"></p>
    </aside>
    <div class="login-box">
    <h1>登 录</h1>
    <div class="sub">管理控制台</div>
    <div id="lg_err"></div>
    <div class="frow"><label>用户名</label><input id="lg_user" autocomplete="username" value=""></div>
    <div class="frow"><label>密码</label><input id="lg_pass" type="password" autocomplete="current-password"></div>
    <button class="primary" style="width:100%;margin-top:8px" id="lg_btn">登 录</button>
  </div></div></div>`;
  fillServerIdentity();
  const doLogin = async () => {
    const btn = $('#lg_btn'); btn.disabled = true;
    $('#lg_err').innerHTML = '';
    try {
      const data = await api('/api/v1/auth/login', { method: 'POST', noAuth: true,
        body: { username: $('#lg_user').value.trim(), password: $('#lg_pass').value } });
      setAccessToken(data.accessToken);
      App.user = data.user;
      if (data.mustChangePassword) {
        // OPEN-ISSUES #2：初始口令必须先修改，改密前拦截一切路由
        store.set('mustChange', '1');
        location.hash = '#/change-password';
      } else {
        store.del('mustChange');
        location.hash = '#/dashboard';
      }
    } catch (e) {
      // 登录失败就地显示在表单上方（不飘走的 Toast），§7.5
      errToast(e);
      $('#lg_err').innerHTML = `<p role="alert" style="color:var(--err-fg);font:var(--t-sm);margin-bottom:var(--s-3)">${esc(e && e.message ? e.message : '登录失败')}</p>`;
      btn.disabled = false;
    }
  };
  $('#lg_btn').addEventListener('click', doLogin);
  $('#lg_pass').onkeydown = ev => { if (ev.key === 'Enter') doLogin(); };
  $('#lg_user').focus();
}

export function vChangePassword() {
  $('#app').innerHTML = `<div class="login-wrap"><div class="login-box">
    <h1>修改登录口令</h1>
    <div class="sub">为保障账户安全，请立即设置新口令。新口令长度不少于 12 位，且不能与用户名相同。</div>
    <div class="frow"><label>当前口令</label><input id="cp_old" type="password" autocomplete="current-password"></div>
    <div class="frow"><label>新口令</label><input id="cp_new" type="password" autocomplete="new-password"></div>
    <div class="frow"><label>确认新口令</label><input id="cp_new2" type="password" autocomplete="new-password"></div>
    <button class="primary" style="width:100%;margin-top:8px" id="cp_btn">确认修改</button>
  </div></div>`;
  const doChange = async () => {
    const oldPw = $('#cp_old').value, np = $('#cp_new').value, np2 = $('#cp_new2').value;
    if (np !== np2) { toast('两次输入的新口令不一致', 'err'); return; }
    const btn = $('#cp_btn'); btn.disabled = true;
    try {
      await api('/api/v1/auth/change-password', { method: 'POST',
        body: { oldPassword: oldPw, newPassword: np } });
      // 成功后服务端已吊销全部刷新令牌，本地清空登录态，用新口令重新登录
      clearAuth();
      toast('口令修改成功，请使用新口令重新登录', 'ok');
      location.hash = '#/login';
      setTimeout(() => location.reload(), 1200);
    } catch (e) { errToast(e); btn.disabled = false; }
  };
  $('#cp_btn').addEventListener('click', doChange);
  $('#cp_new2').onkeydown = ev => { if (ev.key === 'Enter') doChange(); };
  $('#cp_old').focus();
}
