/* js/ui.js —— 组件工厂（UI-REDESIGN §6 / §9）：
   状态标记、表格、分页、模态、抽屉、Toast、骨架、空态、批量动作条。 */
import { App } from './state.js';

export const $ = s => document.querySelector(s);
export const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/* ── 枚举文案 ── */
export const L = {
  client_status: { pending_approval: '待审批', online: '在线', suspected_offline: '疑似离线', offline: '离线', disabled: '已禁用', revoked: '已注销', certificate_expired: '证书过期' },
  backup_set_status: { verifying: '校验中', available: '可用', verification_failed: '校验失败', quarantined: '已隔离', recycle_bin: '回收站', deleted: '已删除' },
  restore_status: { requested: '已请求', verifying: '校验中', ready: '就绪', downloading: '下载中', completed: '已完成', failed: '失败', expired: '已过期' },
  alert_level: { critical: '严重', warning: '警告', notice: '提示' },
  alert_status: { open: '未处理', acknowledged: '已确认', in_progress: '处理中', recovered: '已恢复', closed: '已关闭', ignored: '已忽略' },
  notif_status: { pending: '待发送', sent: '已发送', failed: '失败' },
  task_mode: { automatic: '自动', approval_required: '需审批', manual: '手动', monitor_only: '仅监控', paused: '已暂停' },
  importance: { low: '低', normal: '普通', high: '高', critical: '关键' },
  channel: { email: '邮件', wecom: '企业微信', dingtalk: '钉钉' },
  result: { success: '成功', failure: '失败' },
  batch_status: { pending: '等待', running: '执行中', completed: '已完成', partial: '部分完成', failed: '失败', cancelled: '已取消' },
  recognizer: { latest_single_file: '最新单文件', latest_directory: '最新目录', multi_file_set: '多文件集', subdirectory_units: '子目录单元' },
  service_expected: { running: '运行中', stopped: '已停止' },
  service_actual: { running: '运行中', stopped: '已停止', paused: '已暂停', not_found: '服务不存在', unknown: '未知' },
  service_start_type: { boot: '引导启动', system: '系统启动', auto: '自动', manual: '手动', disabled: '已禁用' },
  client_runtime: { idle: '空闲', uploading: '上传中', working: '执行中' },
  precheck: { not_scanned: '未扫描', passed: '通过', still_changing: '仍在变化', no_new_backup: '没有新备份', required_file_missing: '缺少必需文件', size_abnormal: '大小异常', path_not_found: '路径不存在', access_denied: '拒绝访问', failed: '失败' }
};

/* ── §6.1 状态标记：形状+文字+颜色三重编码。
   ok=● 绿 / busy=◐ 蓝 / wait=○ 琥珀 / err=▲ 红 / off=⊘ 灰 / mut=○ 灰。
   pill（带背景胶囊）只留给真正异常的状态——颜色必须保留报警能力。 ── */
const STATUS_MAP = {
  online: ['ok'], suspected_offline: ['wait'], pending_approval: ['wait'],
  offline: ['err', 'pill'], disabled: ['off'], revoked: ['off'], certificate_expired: ['err', 'pill'],
  available: ['ok'], verifying: ['busy'], verification_failed: ['err', 'pill'],
  quarantined: ['err', 'pill'], recycle_bin: ['wait'], deleted: ['off'],
  requested: ['wait'], ready: ['wait'], downloading: ['busy'],
  completed: ['ok'], failed: ['err', 'pill'], expired: ['off'],
  critical: ['err', 'pill'], warning: ['wait'], notice: ['mut'],
  open: ['err', 'pill'], acknowledged: ['busy'], in_progress: ['busy'],
  recovered: ['ok'], closed: ['off'], ignored: ['off'],
  pending: ['wait'], sent: ['ok'],
  automatic: ['ok'], approval_required: ['busy'], manual: ['mut'], monitor_only: ['mut'], paused: ['wait'],
  success: ['ok'], failure: ['err', 'pill'],
  running: ['busy'], partial: ['wait'], cancelled: ['off'],
  healthy: ['ok'], unhealthy: ['err', 'pill'], degraded: ['wait'],
  uploading: ['busy', 'pill'], working: ['busy'], idle: ['mut'],
  not_scanned: ['mut'], passed: ['ok'], still_changing: ['wait'],
  no_new_backup: ['wait'], required_file_missing: ['err', 'pill'], path_not_found: ['err', 'pill'],
  size_abnormal: ['err', 'pill'], access_denied: ['err', 'pill']
};
export function status(group, val) {
  const label = (L[group] && L[group][val]) || val || '—';
  const m = STATUS_MAP[val] || ['mut'];
  return `<span class="status status--${m[0]}${m[1] === 'pill' ? ' pill' : ''}">${esc(label)}</span>`;
}

/* ── 格式化 ── */
export function fmtBytes(n) {
  if (n == null) return '—';
  n = Number(n);
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (i === 0 ? n : n.toFixed(2)) + ' ' + u[i];
}
export function fmtDT(v) {
  if (!v) return '—';
  const d = new Date(v);
  if (isNaN(d)) return esc(v);
  const p = x => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}
/* §6.2 相对时间：title 挂绝对时间；超过 7 天回退到日期 */
export function relTime(v) {
  if (!v) return '—';
  const d = new Date(v);
  if (isNaN(d)) return esc(v);
  const abs = fmtDT(v);
  const diff = Date.now() - d.getTime();
  if (diff < 0) return `<time title="${abs}">${abs}</time>`;
  const m = Math.floor(diff / 60000);
  if (m < 1) return `<time title="${abs}">刚刚</time>`;
  if (m < 60) return `<time title="${abs}">${m} 分钟前</time>`;
  const h = Math.floor(m / 60);
  if (h < 24) return `<time title="${abs}">${h} 小时前</time>`;
  const dd = Math.floor(h / 24);
  if (dd <= 7) return `<time title="${abs}">${dd} 天前</time>`;
  const p = x => String(x).padStart(2, '0');
  return `<time title="${abs}">${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}</time>`;
}
export function shortId(g) { return g ? String(g).slice(0, 8) : '—'; }

/* 客户端在界面上的称呼。
   显示名是装机时人填的（「财务服务器」），主机名是机器生成的（WIN-CLS1Q76D08E）。
   要认出是哪台机器，人靠的是前者；后者只在重名时用来消歧。
   此前全站一律把主机名摆在主位、显示名当小字，等于把人填的名字架空了。 */
export function clientName(c) {
  const display = (c && (c.displayName || c.display_name) || '').trim();
  const host = (c && c.hostname || '').trim();
  return display || host || '—';
}

/* 带消歧后缀的完整称呼，用于下拉这类没有副标题位置的地方。 */
export function clientLabel(c) {
  const display = (c && (c.displayName || c.display_name) || '').trim();
  const host = (c && c.hostname || '').trim();
  return display && host && display !== host ? `${display}（${host}）` : (display || host || '—');
}
/* ── 客户端能不能动：一处判定，列表 / 详情 / 批量条共用 ──

   此前每个按钮都是无条件画出来的，于是有三类白点：
   离线的机器点「刷新指标」，指令只是进队列，24 小时后静默过期，界面还弹「已下发」；
   已禁用/已注销的机器点「刷新指标」「注销」，服务端直接 409；
   正在上传的机器点「上传」，服务端以 CLIENT_BUSY 回绝——服务端本来就在拦，
   界面却还让人点。判定依据必须和服务端一致，否则就是「按钮亮着，点了报错」。

   两条轴：连接状态（连不连得上）× 运行状态（现在忙不忙）。
   每个 can* 都配一句 why——变灰而不说为什么，比不变灰更让人困惑。 */
export function clientCaps(c) {
  const st = c && c.status;
  const runtime = (c && c.runtimeState) || 'idle';
  const busy = runtime !== 'idle';
  const busyWhy = runtime === 'uploading'
    ? '这台客户端正在上传备份，等它传完再操作'
    : '这台客户端正在执行指令，等它跑完再操作';
  const unreachableWhy = {
    pending_approval: '客户端还没审批通过，不能下发指令',
    offline: '客户端离线，指令下发了也没人执行',
    certificate_expired: '客户端证书已过期，连不上服务端',
    disabled: '客户端已禁用，不能下发指令',
    revoked: '客户端已注销，不能下发指令'
  }[st] || '';
  // 疑似离线只是漏了一次心跳，Agent 多半还活着，指令排队等它回来是合理的——
  // 这里不拦，只在提示里说清楚。
  const reachable = st === 'online' || st === 'suspected_offline';
  // 禁用/注销和「连不连得上」无关：离线的机器照样可以禁用。
  // 待审批的用「拒绝」，已禁用/已注销的服务端会 409。
  const manageable = st !== 'pending_approval' && st !== 'disabled' && st !== 'revoked';
  const dispatchWhy = reachable
    ? (st === 'suspected_offline' ? '客户端心跳已中断，指令会排队等它恢复' : '')
    : unreachableWhy;

  return {
    runtime, busy,
    canApprove: st === 'pending_approval',
    canDispatch: reachable,
    canUpload: reachable && !busy,
    canDisable: manageable && !busy,
    canEnable: st === 'disabled',
    canRevoke: st !== 'revoked' && st !== 'pending_approval' && !busy,
    whyDispatch: dispatchWhy,
    whyUpload: busy ? busyWhy : dispatchWhy,
    whyDisable: busy ? busyWhy : (st === 'disabled' ? '客户端已禁用' : st === 'revoked' ? '客户端已注销' : st === 'pending_approval' ? '待审批的客户端请用「拒绝」' : ''),
    whyEnable: st === 'revoked' ? '注销不可恢复，需要在客户端机器上重新登记' : '',
    whyRevoke: busy ? busyWhy : (st === 'revoked' ? '客户端已注销' : st === 'pending_approval' ? '待审批的客户端请用「拒绝」' : '')
  };
}

/* 「正在干活」的标记。空闲时什么都不画——给每一行都挂一个"空闲"标签，
   等于把真正需要注意的那两行淹掉。 */
export function runtimeBadge(c) {
  const rt = (c && c.runtimeState) || 'idle';
  if (rt === 'idle') return '';
  const detail = rt === 'uploading'
    ? `正在上传 ${c.activeUploadCount || 0} 个备份集`
    : `有 ${c.runningCommandCount || 0} 条指令正在执行`;
  return ` <span title="${esc(detail)}">${status('client_runtime', rt)}</span>`;
}

/* 动作按钮。allowed=false 时不是把按钮藏起来，而是变灰 + title 说明原因——
   藏起来会让人以为功能不存在，转头去翻文档或者问人。 */
export function actBtn({ label, view, action, id, cls = '', allowed = true, why = '', hint = '', small = true }) {
  const klass = ((small ? 'small ' : '') + cls).trim();
  if (!allowed)
    return `<button${klass ? ` class="${esc(klass)}"` : ''} disabled title="${esc(why || '当前状态下不可用')}">${esc(label)}</button>`;
  return `<button${klass ? ` class="${esc(klass)}"` : ''} data-ui-action="act" data-view="${esc(view)}" data-action="${esc(action)}" data-id="${esc(id)}"${hint ? ` title="${esc(hint)}"` : ''}>${esc(label)}</button>`;
}

export function prettyJson(s) {
  if (!s) return '—';
  try { return JSON.stringify(JSON.parse(s), null, 2); } catch (e) { return String(s); }
}
export function optsOf(map) { return Object.entries(map).map(([v, t]) => ({ v, t })); }

/* ── Toast ── */
export function toast(msg, type = 'info') {
  const el = document.createElement('div');
  el.className = 'toast ' + type;
  el.setAttribute('role', type === 'err' ? 'alert' : 'status');
  el.textContent = msg;
  $('#toasts').appendChild(el);
  setTimeout(() => el.remove(), 4200);
}
export function errToast(e) { toast(e && e.message ? e.message : String(e), 'err'); }

/* ── 扫描计划：从手写 cron 改成选择 ──

   `0 2 * * *` 这种东西没人愿意每次都想一遍，而写错了也没有即时反馈：
   服务端只能告诉你「不是合法 cron」，告诉不了你「你想要每天两点，但写成了每两小时」。
   段数记反（把秒当第一段）、星期写成 1-7、日和周同时限定，都是常见错法。

   这里把它拆成「多久一次 + 几点」两个选择器，cron 由界面合成，天生合法；
   真需要写复杂表达式的人，选「自己写 cron」照旧。 */

const CRON_DOW = [['1', '周一'], ['2', '周二'], ['3', '周三'], ['4', '周四'], ['5', '周五'], ['6', '周六'], ['0', '周日']];
const CRON_FREQ = [
  ['', '不自动扫描（只手动预检）'],
  ['hourly', '每小时'],
  ['daily', '每天'],
  ['weekly', '每周'],
  ['monthly', '每月'],
  ['raw', '自己写 cron 表达式']
];
const cronPad = n => String(n).padStart(2, '0');

/* 已存的表达式 → 界面上的几个选择。认不出来的一律落到「自己写」，不猜。 */
function cronParse(expr) {
  const base = { freq: '', minute: '0', time: '02:00', dow: '1', dom: '1', raw: '' };
  const text = String(expr || '').trim();
  if (!text) return base;
  const raw = Object.assign({}, base, { freq: 'raw', raw: text });
  const f = text.split(/\s+/);
  if (f.length !== 5) return raw;
  const num = v => (/^\d+$/.test(v) ? Number(v) : null);
  const [m, h, dom, mon, dow] = f;
  const mn = num(m);
  if (mn === null || mn > 59 || mon !== '*') return raw;
  if (h === '*' && dom === '*' && dow === '*')
    return Object.assign({}, base, { freq: 'hourly', minute: String(mn) });
  const hn = num(h);
  if (hn === null || hn > 23) return raw;
  const time = `${cronPad(hn)}:${cronPad(mn)}`;
  if (dom === '*' && dow === '*') return Object.assign({}, base, { freq: 'daily', time });
  const dowN = num(dow);
  if (dom === '*' && dowN !== null && dowN <= 7)
    return Object.assign({}, base, { freq: 'weekly', time, dow: String(dowN % 7) });
  const domN = num(dom);
  if (dow === '*' && domN !== null && domN >= 1 && domN <= 31)
    return Object.assign({}, base, { freq: 'monthly', time, dom: String(domN) });
  return raw;
}

function cronCompose(s) {
  const parts = String(s.time || '02:00').split(':');
  const hh = String(Number(parts[0]) || 0);
  const mm = String(Number(parts[1]) || 0);
  switch (s.freq) {
    case 'hourly': return `${Number(s.minute) || 0} * * * *`;
    case 'daily': return `${mm} ${hh} * * *`;
    case 'weekly': return `${mm} ${hh} * * ${s.dow}`;
    case 'monthly': return `${mm} ${hh} ${s.dom} * *`;
    case 'raw': return String(s.raw || '').trim();
    default: return '';
  }
}

function cronEcho(s, expr) {
  if (!expr) return '不自动扫描。任务仍然可以在列表里手动点「预检」';
  const tz = '（按客户端所在时区）';
  const dowText = (CRON_DOW.find(d => d[0] === String(s.dow)) || ['', ''])[1];
  let text;
  switch (s.freq) {
    case 'hourly': text = `每小时的第 ${Number(s.minute) || 0} 分钟扫描一次`; break;
    case 'daily': text = `每天 ${s.time} 扫描`; break;
    case 'weekly': text = `每${dowText} ${s.time} 扫描`; break;
    case 'monthly': text = `每月 ${s.dom} 号 ${s.time} 扫描`; break;
    default: return `cron：${expr}${tz}`;
  }
  return `${text}${tz} · ${expr}`;
}

/* 详情页把扫描计划也说成人话——列表和详情读到的是同一段表达式，
   没理由一边给选择器、另一边还是甩一串 `0 2 * * *`。 */
export function describeCron(expr) {
  const s = cronParse(expr);
  return cronEcho(s, String(expr || '').trim());
}

function cronFieldHtml(name, value) {
  const s = cronParse(value);
  const opt = (list, cur) => list.map(([v, t]) =>
    `<option value="${esc(v)}" ${String(v) === String(cur) ? 'selected' : ''}>${esc(t)}</option>`).join('');
  const range = (from, to, suffix) => {
    const out = [];
    for (let i = from; i <= to; i++) out.push([String(i), i + suffix]);
    return out;
  };
  const minutes = [];
  for (let i = 0; i < 60; i += 5) minutes.push([String(i), `第 ${i} 分`]);
  return `<div class="cronfield" data-cron>
    <input type="hidden" name="${esc(name)}" value="${esc(cronCompose(s))}">
    <div class="cronrow">
      <select data-cron-freq aria-label="扫描频率">${opt(CRON_FREQ, s.freq)}</select>
      <select data-cron-dow aria-label="星期几">${opt(CRON_DOW, s.dow)}</select>
      <select data-cron-dom aria-label="每月几号">${opt(range(1, 31, ' 号'), s.dom)}</select>
      <input type="time" data-cron-time value="${esc(s.time)}" aria-label="扫描时刻">
      <select data-cron-minute aria-label="每小时第几分钟">${opt(minutes, s.minute)}</select>
      <input type="text" class="mono" data-cron-raw value="${esc(s.raw)}" placeholder="0 2 * * *" aria-label="cron 表达式">
    </div>
    <div class="hint" data-cron-echo></div>
  </div>`;
}

/* 把选择同步进隐藏的 input——readValues 按 name 取值，不需要为此改取值逻辑。 */
export function initCronFields(root) {
  root.querySelectorAll('[data-cron]').forEach(box => {
    const hidden = box.querySelector('input[type="hidden"]');
    const freq = box.querySelector('[data-cron-freq]');
    const parts = {
      dow: box.querySelector('[data-cron-dow]'),
      dom: box.querySelector('[data-cron-dom]'),
      time: box.querySelector('[data-cron-time]'),
      minute: box.querySelector('[data-cron-minute]'),
      raw: box.querySelector('[data-cron-raw]')
    };
    const shown = {
      '': [],
      hourly: ['minute'],
      daily: ['time'],
      weekly: ['dow', 'time'],
      monthly: ['dom', 'time'],
      raw: ['raw']
    };
    const sync = () => {
      const state = {
        freq: freq.value, dow: parts.dow.value, dom: parts.dom.value,
        time: parts.time.value, minute: parts.minute.value, raw: parts.raw.value
      };
      const visible = shown[state.freq] || [];
      Object.keys(parts).forEach(k => { parts[k].hidden = !visible.includes(k); });
      const expr = cronCompose(state);
      hidden.value = expr;
      box.querySelector('[data-cron-echo]').textContent = cronEcho(state, expr);
    };
    box.addEventListener('change', sync);
    box.addEventListener('input', sync);
    sync();
  });
}

/* ── 表单类模态（详情类请用抽屉，§6.3） ── */
export function openModal(title, bodyHtml, opts = {}) {
  closeModal();
  const ov = document.createElement('div');
  ov.className = 'overlay';
  ov.id = 'overlay';
  ov.innerHTML = `<div class="modal ${opts.wide ? 'wide' : ''}" role="dialog" aria-modal="true">
    <h3>${esc(title)}</h3>
    <div class="mbody">${bodyHtml}</div>
    <div class="mfoot">
      <button data-close>关闭</button>
      ${opts.okText ? `<button class="primary" data-ok>${esc(opts.okText)}</button>` : ''}
    </div>
  </div>`;
  document.body.appendChild(ov);
  ov.addEventListener('click', ev => {
    // opts.persistent：多步流程（向导、要等客户端应答的选择器）里，点空白处误关一次，
    // 前面几步连同等了十几秒的结果一起没了，而人并没有表达过"我要放弃"。
    // 关闭仍然有两条明路——「关闭」按钮和 Esc，两者都是明确的动作。
    if (ev.target === ov && opts.persistent) return;
    if (ev.target === ov || ev.target.closest('[data-close]')) closeModal();
  });
  return ov;
}
/* target 可选：只关闭指定的那一层遮罩。
   向导式流程里，onSubmit 解决的 Promise 会让调用方在同一批微任务里打开下一个弹窗，
   随后 onSubmit 的调用点才执行 closeModal()——不指定目标的话，关掉的是刚打开的
   下一步，用户看到的就是「点下一步，窗口直接关回主页面」。 */
export function closeModal(target) {
  const o = target || $('#overlay');
  if (!o || !o.isConnected) return;
  // 先派发再移除：confirmModal 等待方靠这个事件把 Promise 落定为「取消」（P2-11）
  o.dispatchEvent(new CustomEvent('bm:dismiss'));
  o.remove();
}

export function confirmModal(msg) {
  return new Promise(resolve => {
    const ov = openModal('请确认', `<p style="line-height:1.7">${esc(msg)}</p>`, { okText: '确定' });
    let settled = false;
    const settle = value => {
      if (settled) return;
      settled = true;
      ov.removeEventListener('bm:dismiss', onDismiss);
      resolve(value);
    };
    // P2-11：原先只在点遮罩/关闭按钮时 resolve(false)，而全局 Esc 处理器走的是 closeModal()，
    // 于是按 Esc 关掉弹窗后 Promise 永不落定——所有 `if (!await confirmModal(...)) return;`
    // 的调用方会静默永久挂起，用户既得不到结果也没有任何提示。
    // closeModal 现在会派发 bm:dismiss，任何关闭途径都能让它落定为「取消」。
    const onDismiss = () => settle(false);
    ov.addEventListener('bm:dismiss', onDismiss);
    ov.querySelector('[data-ok]').addEventListener('click', () => { settle(true); closeModal(); });
    ov.addEventListener('click', ev => {
      if (ev.target === ov || ev.target.closest('[data-close]')) setTimeout(() => settle(false), 0);
    });
  });
}

/* 通用表单弹窗：fields=[{name,label,type,value,options,required,placeholder,hint,advanced}]
   advanced:true 的字段收进「高级选项」折叠区。这类字段都带可用默认值，
   平时不该出现在视野里——一次性铺开十几个输入框会让「必须填什么」这件事消失，
   使用者只能逐个猜。取值逻辑不受影响：折叠区里的控件同样在 DOM 中。

   type='static' 是只读展示块（html 字段直接插入），用来放实时预览这类不参与取值的内容。
   type='cron' 是扫描计划组合控件（频率 + 时刻），取值时拿到的是合成好的 cron 表达式。
   opts.onMount(ov, readValues) 在渲染完成后调用一次，给需要联动的表单挂事件。
   opts.validate(vals) 返回一句话表示"这组值不能一起提交"，返回空表示通过——
   单字段的必填在下面已经管了，跨字段的约束（比如时间窗口要么都填要么都空）放这里。 */
export function formModal(title, fields, onSubmit, okText = '保存', opts = {}) {
  const renderField = f => {
    let input;
    const v = f.value != null ? f.value : '';
    if (f.type === 'static') {
      return `<div class="frow" data-static="${esc(f.name)}">${f.label ? `<label>${esc(f.label)}</label>` : ''}${f.html || ''}${f.hint ? `<div class="hint">${esc(f.hint)}</div>` : ''}</div>`;
    }
    if (f.type === 'cron') {
      input = cronFieldHtml(f.name, v);
    } else if (f.type === 'select') {
      input = `<select name="${f.name}">${(f.options || []).map(o =>
        `<option value="${esc(o.v)}" ${String(o.v) === String(v) ? 'selected' : ''}>${esc(o.t)}</option>`).join('')}</select>`;
    } else if (f.type === 'textarea') {
      input = `<textarea name="${f.name}" placeholder="${esc(f.placeholder || '')}" ${f.rows ? `rows="${f.rows}"` : ''}>${esc(v)}</textarea>`;
    } else if (f.type === 'checkbox') {
      input = `<label class="frow inline" style="margin:0"><input type="checkbox" name="${f.name}" ${v ? 'checked' : ''}>${esc(f.labelText || '启用')}</label>`;
    } else {
      input = `<input type="${f.type || 'text'}" name="${f.name}" value="${esc(v)}" placeholder="${esc(f.placeholder || '')}" ${f.type === 'number' ? 'step="any"' : ''}>`;
    }
    return `<div class="frow"><label>${esc(f.label)}${f.required ? ' *' : ''}</label>${input}${f.hint ? `<div class="hint">${esc(f.hint)}</div>` : ''}</div>`;
  };
  const basic = fields.filter(f => !f.advanced);
  const advanced = fields.filter(f => f.advanced);
  const html = basic.map(renderField).join('')
    + (advanced.length
      ? `<details class="fadv"><summary>高级选项（${advanced.length} 项，均有默认值）</summary>${advanced.map(renderField).join('')}</details>`
      : '');
  const ov = openModal(title, html, { okText, wide: opts.wide, persistent: opts.persistent });
  initCronFields(ov);

  const readValues = () => {
    const vals = {};
    for (const f of fields) {
      if (f.type === 'static') continue;
      const el = ov.querySelector(`[name="${f.name}"]`);
      if (!el) continue;
      vals[f.name] = f.type === 'checkbox' ? el.checked : el.value.trim();
    }
    return vals;
  };

  if (typeof opts.onMount === 'function') opts.onMount(ov, readValues);

  ov.querySelector('[data-ok]').addEventListener('click', async () => {
    const vals = readValues();
    for (const f of fields) {
      if (f.type === 'static' || !f.required) continue;
      const el = ov.querySelector(`[name="${f.name}"]`);
      if (!el) continue;
      if (vals[f.name] === '' || vals[f.name] == null) {
        // 字段可能收在折叠区里；不展开的话提示指向一个看不见的输入框。
        el.closest('details')?.setAttribute('open', '');
        el.focus();
        toast(`「${f.label}」为必填项`, 'err'); return;
      }
    }
    if (typeof opts.validate === 'function') {
      const problem = opts.validate(vals);
      if (problem) {
        // 跨字段约束多半落在高级选项里，不展开的话人看不到自己被说的是哪一项。
        ov.querySelector('details.fadv')?.setAttribute('open', '');
        toast(problem, 'err');
        return;
      }
    }
    const btn = ov.querySelector('[data-ok]');
    btn.disabled = true;
    try { await onSubmit(vals); closeModal(ov); }
    catch (e) { errToast(e); btn.disabled = false; }
  });

  // 返回遮罩层，调用方可以监听 bm:dismiss 把「关掉了」和「提交了」区分开。
  return ov;
}

/* ── §6.3 详情抽屉：右侧滑入 / focus trap / Esc / 焦点归还 ── */
let drawerEl = null, drawerRestore = null, drawerOnClose = null;
function escCloseDrawer(e) { if (e.key === 'Escape') closeDrawer(); }
function trapTab(e) {
  if (e.key !== 'Tab' || !drawerEl) return;
  const f = [...drawerEl.dr.querySelectorAll('button,a[href],input,select,textarea,[tabindex]:not([tabindex="-1"])')].filter(x => !x.disabled && x.offsetParent !== null);
  if (!f.length) return;
  const first = f[0], last = f[f.length - 1];
  if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
  else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
}
export function openDrawer({ title, bodyHtml, wide = false, onClose = null }) {
  closeDrawer(true);
  drawerRestore = document.activeElement;
  drawerOnClose = onClose;
  const ov = document.createElement('div'); ov.className = 'drawer-ov';
  const dr = document.createElement('div');
  dr.className = 'drawer' + (wide ? ' wide' : '');
  dr.setAttribute('role', 'dialog'); dr.setAttribute('aria-modal', 'true');
  dr.innerHTML = `<div class="dhead"><h3>${esc(title)}</h3><button class="small" data-dclose aria-label="关闭抽屉">✕</button></div><div class="dbody">${bodyHtml}</div>`;
  document.body.appendChild(ov); document.body.appendChild(dr);
  requestAnimationFrame(() => { ov.classList.add('open'); dr.classList.add('open'); });
  ov.addEventListener('click', () => closeDrawer());
  dr.querySelector('[data-dclose]').addEventListener('click', () => closeDrawer());
  dr.addEventListener('keydown', trapTab);
  document.addEventListener('keydown', escCloseDrawer);
  drawerEl = { ov, dr };
  dr.querySelector('[data-dclose]').focus();
}
export function closeDrawer(skipOnClose = false) {
  if (!drawerEl) return;
  const { ov, dr } = drawerEl; drawerEl = null;
  document.removeEventListener('keydown', escCloseDrawer);
  const cb = drawerOnClose; drawerOnClose = null;
  ov.classList.remove('open'); dr.classList.remove('open');
  setTimeout(() => { ov.remove(); dr.remove(); }, 200);
  if (drawerRestore && drawerRestore.focus) drawerRestore.focus();
  drawerRestore = null;
  if (!skipOnClose && cb) cb();
}
export function drawerOpen() { return !!drawerEl; }

/* ── §6.2 表格：32px 行高 / 粘性表头 / 数字右对齐 / 排序列 aria-sort / 多选列 ── */
export function tableHtml(cols, rows, opts = {}) {
  if (!rows || !rows.length) return opts.empty || '<div class="empty">暂无数据</div>';
  const st = opts.stateKey ? App.state[opts.stateKey] : null;
  const sel = new Set(st && st.selected ? st.selected : []);
  const head = cols.map(c => {
    const cls = [c.num ? 'num' : '', c.sort ? 'sortable' : ''].filter(Boolean).join(' ');
    let inner = esc(c.l), attrs = '';
    if (c.sort) {
      const active = st && st.sortKey === c.k;
      const dir = active ? (st.sortDesc === false ? 'ascending' : 'descending') : 'none';
      attrs = ` aria-sort="${dir}"`;
      inner += `<span class="sarrow" aria-hidden="true">${active ? (st.sortDesc === false ? '↑' : '↓') : '↕'}</span>`;
      inner = `<span data-ui-action="sort" data-sort-key="${esc(opts.stateKey)}" data-sort-column="${esc(c.k)}" style="display:inline-block">${inner}</span>`;
    }
    return `<th${cls ? ` class="${cls}"` : ''}${attrs}>${inner}</th>`;
  }).join('');
  const selHead = opts.stateKey
    ? `<th class="tsel"><input type="checkbox" data-selall="${esc(opts.stateKey)}" data-ids="${esc(rows.map(r => r.id).join(','))}" aria-label="全选" ${rows.every(r => sel.has(r.id)) ? 'checked' : ''}></th>` : '';
  const body = rows.map(r => `<tr>${opts.stateKey ? `<td class="tsel"><input type="checkbox" data-sel="${esc(r.id)}" data-selkey="${esc(opts.stateKey)}" aria-label="选择该行" ${sel.has(r.id) ? 'checked' : ''}></td>` : ''}${cols.map(c => `<td${c.num ? ' class="num"' : ''}>${c.render ? c.render(r) : esc(r[c.k] ?? '—')}</td>`).join('')}</tr>`).join('');
  return `<table><thead><tr>${selHead}${head}</tr></thead><tbody>${body}</tbody></table>`;
}

export function pagerHtml(id, st) {
  const pages = Math.max(1, Math.ceil(st.totalCount / st.pageSize));
  return `<div class="pager">
    <span>共 ${st.totalCount} 条 · 第 ${st.page}/${pages} 页</span>
    <button class="small" data-ui-action="page" data-page-key="${esc(id)}" data-page-delta="-1" ${st.page <= 1 ? 'disabled' : ''}>上一页</button>
    <button class="small" data-ui-action="page" data-page-key="${esc(id)}" data-page-delta="1" ${st.page >= pages ? 'disabled' : ''}>下一页</button>
  </div>`;
}

/* ── §6.5 骨架屏（形状同真实内容，无闪烁动画） ── */
export function skeleton(n = 4) {
  const w = [96, 82, 90, 74, 88, 80];
  let rows = '';
  for (let i = 0; i < n; i++) rows += `<div class="sk-row" style="width:${w[i % w.length]}%"></div>`;
  return `<div class="sk" aria-hidden="true">${rows}</div>`;
}

/* ── §6.5 空态三分：first=首次为空 / filter=筛选为空 / ok=正常的空（好消息） ── */
export function emptyState(kind, o = {}) {
  if (kind === 'ok') {
    return `<div class="empty ok" role="status"><span class="e-glyph" aria-hidden="true">✓</span><div class="e-title">${esc(o.title || '没有待处理事项')}</div>${o.sub ? `<div class="e-sub">${esc(o.sub)}</div>` : ''}</div>`;
  }
  if (kind === 'filter') {
    return `<div class="empty"><span class="e-glyph" aria-hidden="true">⌕</span><div class="e-title">${esc(o.title || '没有匹配的结果')}</div><div class="e-act"><button data-clearfilter="${esc(o.key || '')}">清除筛选</button></div></div>`;
  }
  return `<div class="empty"><span class="e-glyph" aria-hidden="true">${esc(o.glyph || '▦')}</span><div class="e-title">${esc(o.title || '暂无数据')}</div>${o.sub ? `<div class="e-sub">${esc(o.sub)}</div>` : ''}${o.actHtml ? `<div class="e-act">${o.actHtml}</div>` : ''}</div>`;
}
export function hasFilter(st, keys) { return keys.some(k => st[k] !== undefined && st[k] !== ''); }

/* ── §6.2 批量动作条：选中 ≥1 项时工具条原地变形 ── */
export function batchBarHtml(key) {
  const st = App.state[key];
  const n = st && st.selected ? st.selected.length : 0;
  if (!n) return '';
  const acts = (App.batchActs && App.batchActs[key]) || [];
  return `<div class="batchbar" role="toolbar" aria-label="批量操作">
    <span class="count">已选 ${n} 项</span>
    ${acts.map((a, i) => `<button class="small${a.primary ? ' primary' : ''}${a.danger ? ' danger' : ''}" data-ui-action="batch" data-batch-key="${esc(key)}" data-batch-index="${i}">${esc(a.t)}</button>`).join('')}
    <div class="spacer"></div>
    <button class="small" data-ui-action="clear-selection" data-batch-key="${esc(key)}">取消选择</button>
  </div>`;
}
