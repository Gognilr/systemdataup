/* js/ui.js —— 组件工厂（UI-REDESIGN §6 / §9）：
   状态标记、表格、分页、模态、抽屉、Toast、骨架、空态、批量动作条。 */
import { App } from './state.js';

export const $ = s => document.querySelector(s);
export const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/* ── 枚举文案 ── */
export const L = {
  client_status: { pending_approval: '待审批', online: '在线', suspected_offline: '疑似离线', offline: '离线', disabled: '已禁用', revoked: '已注销', certificate_expired: '证书过期' },
  backup_set_status: { verifying: '校验中', available: '可用', verification_failed: '校验失败', quarantined: '已隔离', retention_pending: '保留待定', recycle_bin: '回收站', deleted: '已删除' },
  restore_status: { requested: '已请求', verifying: '校验中', ready: '就绪', downloading: '下载中', completed: '已完成', failed: '失败', expired: '已过期' },
  alert_level: { critical: '严重', warning: '警告', notice: '提示' },
  alert_status: { open: '未处理', acknowledged: '已确认', in_progress: '处理中', recovered: '已恢复', closed: '已关闭', ignored: '已忽略' },
  notif_status: { pending: '待发送', sent: '已发送', failed: '失败' },
  task_mode: { automatic: '自动', approval_required: '需审批', manual: '手动', monitor_only: '仅监控', paused: '已暂停' },
  importance: { low: '低', normal: '普通', high: '高', critical: '关键' },
  channel: { email: '邮件', wecom: '企业微信', dingtalk: '钉钉' },
  result: { success: '成功', failure: '失败' },
  batch_status: { pending: '等待', running: '执行中', completed: '已完成', partial: '部分完成', failed: '失败', cancelled: '已取消' },
  recognizer: { latest_single_file: '最新单文件', latest_directory: '最新目录', multi_file_set: '多文件集', subdirectory_units: '子目录单元' }
};

/* ── §6.1 状态标记：形状+文字+颜色三重编码。
   ok=● 绿 / busy=◐ 蓝 / wait=○ 琥珀 / err=▲ 红 / off=⊘ 灰 / mut=○ 灰。
   pill（带背景胶囊）只留给真正异常的状态——颜色必须保留报警能力。 ── */
const STATUS_MAP = {
  online: ['ok'], suspected_offline: ['wait'], pending_approval: ['wait'],
  offline: ['err', 'pill'], disabled: ['off'], revoked: ['off'], certificate_expired: ['err', 'pill'],
  available: ['ok'], verifying: ['busy'], verification_failed: ['err', 'pill'],
  quarantined: ['err', 'pill'], retention_pending: ['wait'], recycle_bin: ['wait'], deleted: ['off'],
  requested: ['wait'], ready: ['wait'], downloading: ['busy'],
  completed: ['ok'], failed: ['err', 'pill'], expired: ['off'],
  critical: ['err', 'pill'], warning: ['wait'], notice: ['mut'],
  open: ['err', 'pill'], acknowledged: ['busy'], in_progress: ['busy'],
  recovered: ['ok'], closed: ['off'], ignored: ['off'],
  pending: ['wait'], sent: ['ok'],
  automatic: ['ok'], approval_required: ['busy'], manual: ['mut'], monitor_only: ['mut'], paused: ['wait'],
  success: ['ok'], failure: ['err', 'pill'],
  running: ['busy'], partial: ['wait'], cancelled: ['off'],
  healthy: ['ok'], unhealthy: ['err', 'pill'], degraded: ['wait']
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
    if (ev.target === ov || ev.target.closest('[data-close]')) closeModal();
  });
  return ov;
}
export function closeModal() {
  const o = $('#overlay');
  if (!o) return;
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

/* 通用表单弹窗：fields=[{name,label,type,value,options,required,placeholder,hint}] */
export function formModal(title, fields, onSubmit, okText = '保存') {
  const html = fields.map(f => {
    let input;
    const v = f.value != null ? f.value : '';
    if (f.type === 'select') {
      input = `<select name="${f.name}">${(f.options || []).map(o =>
        `<option value="${esc(o.v)}" ${String(o.v) === String(v) ? 'selected' : ''}>${esc(o.t)}</option>`).join('')}</select>`;
    } else if (f.type === 'textarea') {
      input = `<textarea name="${f.name}" placeholder="${esc(f.placeholder || '')}">${esc(v)}</textarea>`;
    } else if (f.type === 'checkbox') {
      input = `<label class="frow inline" style="margin:0"><input type="checkbox" name="${f.name}" ${v ? 'checked' : ''}>${esc(f.labelText || '启用')}</label>`;
    } else {
      input = `<input type="${f.type || 'text'}" name="${f.name}" value="${esc(v)}" placeholder="${esc(f.placeholder || '')}" ${f.type === 'number' ? 'step="any"' : ''}>`;
    }
    return `<div class="frow"><label>${esc(f.label)}${f.required ? ' *' : ''}</label>${input}${f.hint ? `<div class="hint">${esc(f.hint)}</div>` : ''}</div>`;
  }).join('');
  const ov = openModal(title, html, { okText });
  ov.querySelector('[data-ok]').addEventListener('click', async () => {
    const vals = {};
    for (const f of fields) {
      const el = ov.querySelector(`[name="${f.name}"]`);
      if (f.type === 'checkbox') { vals[f.name] = el.checked; }
      else { vals[f.name] = el.value.trim(); }
      if (f.required && (vals[f.name] === '' || vals[f.name] == null)) {
        toast(`「${f.label}」为必填项`, 'err'); return;
      }
    }
    const btn = ov.querySelector('[data-ok]');
    btn.disabled = true;
    try { await onSubmit(vals); closeModal(); }
    catch (e) { errToast(e); btn.disabled = false; }
  });
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
