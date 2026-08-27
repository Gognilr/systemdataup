/* js/views/restores.js —— 恢复请求列表 / 抽屉详情 / 签发下载令牌 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtBytes, fmtDT, relTime,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter,
  toast, errToast, confirmModal, openModal, openDrawer
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vRestores() {
  App.state.restores = App.state.restores || { page: 1, pageSize: 20, status: '', totalCount: 0 };
  const st = App.state.restores;
  $('#app').innerHTML = shell('restores', '恢复请求', `
    <div class="toolbar">
      <select id="f_status"><option value="">全部状态</option>${optsOf(L.restore_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <button class="primary" data-ui-action="loader" data-loader="restores">查询</button>
      <div class="spacer"></div>
      <span class="tip">在「备份集」页对可用备份集发起恢复；校验中状态自动刷新</span>
    </div>
    <div id="vwrap">${loading()}</div>`);
  $('#f_status').onchange = () => { st.status = $('#f_status').value; st.page = 1; LOADERS.restores(); };
  await LOADERS.restores();
}

LOADERS.restores = async function () {
  const st = App.state.restores;
  const wrap = $('#vwrap'); if (!wrap) return;
  clearTimeout(App.timer);
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.status) q.set('status', st.status);
    const data = await api('/api/v1/admin/restore-requests?' + q);
    st.totalCount = data.totalCount;
    const empty = !hasFilter(st, ['status'])
      ? emptyState('first', { glyph: '↺', title: '暂无恢复请求', sub: '在「备份集」页对可用备份集点「恢复」即可发起' })
      : emptyState('filter', { key: 'restores', title: `没有匹配「${L.restore_status[st.status] || ''}」的恢复请求` });
    wrap.innerHTML = tableHtml([
      { l: '备份集', render: r => `<span class="mono">${esc(r.backupSetCode || '—')}</span><span class="sub">${esc(r.clientHostname || '')}</span>` },
      { l: '用途', render: r => esc((r.purpose || '').slice(0, 60)) + (r.purpose && r.purpose.length > 60 ? '…' : '') },
      { l: '状态', render: r => status('restore_status', r.status) },
      { l: '请求时间', render: r => relTime(r.requestedAt) },
      { l: '校验完成', render: r => relTime(r.verifiedAt) },
      { l: '下载有效期至', render: r => relTime(r.downloadExpiresAt) },
      { l: '已下载', num: true, render: r => fmtBytes(r.downloadedBytes) },
      { l: '请求人', k: 'requestedByName' },
      { l: '操作', render: r => {
        const b = [`<button class="small" data-ui-action="act" data-view="restores" data-action="detail" data-id="${esc(r.id)}">详情</button>`];
        if (r.status === 'ready') b.push(`<button class="small primary" data-ui-action="act" data-view="restores" data-action="token" data-id="${esc(r.id)}">签发下载令牌</button>`);
        if (r.status === 'verifying' || r.status === 'requested' || r.status === 'downloading')
          b.push(`<button class="small" data-ui-action="act" data-view="restores" data-action="refresh" data-id="${esc(r.id)}">刷新</button>`);
        return b.join(' ');
      } }
    ], data.items, { empty }) + pagerHtml('restores', st);
    if (data.items.some(r => ['requested', 'verifying', 'downloading'].includes(r.status))) {
      App.timer = setTimeout(() => { if (location.hash === '#/restores') LOADERS.restores(); }, 5000);
    }
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};
ACTIONS['restores:refresh'] = async () => LOADERS.restores();

/* §6.3 恢复请求详情抽屉 */
export async function openRestoreDrawer(id, viaNav = false) {
  try {
    const d = await api(`/api/v1/admin/restore-requests/${id}`);
    openDrawer({
      title: '恢复请求详情',
      wide: true,
      onClose: () => { if (location.hash === '#/restores/' + id) history.replaceState(null, '', '#/restores'); },
      bodyHtml: `
    <div class="kv">
      <div class="row"><div class="k">备份集</div><div class="v mono">${esc(d.backupSetCode || '—')} · ${esc(d.clientHostname || '')}</div></div>
      <div class="row"><div class="k">状态</div><div class="v">${status('restore_status', d.status)}</div></div>
      <div class="row"><div class="k">用途</div><div class="v">${esc(d.purpose)}</div></div>
      <div class="row"><div class="k">请求时间</div><div class="v">${fmtDT(d.requestedAt)}</div></div>
      <div class="row"><div class="k">校验完成</div><div class="v">${fmtDT(d.verifiedAt)}</div></div>
      ${d.status === 'verifying' && d.verificationQueueLength != null
        ? `<div class="row"><div class="k">校验队列</div><div class="v">${d.verificationQueueLength > 0
            ? `前面还有 ${d.verificationQueueLength} 个任务`
            : '正在校验'}</div></div>`
        : ''}
      <div class="row"><div class="k">下载有效期至</div><div class="v">${fmtDT(d.downloadExpiresAt)}</div></div>
      <div class="row"><div class="k">已下载</div><div class="v">${fmtBytes(d.downloadedBytes)}</div></div>
      <div class="row"><div class="k">完成时间</div><div class="v">${fmtDT(d.completedAt)}</div></div>
      <div class="row"><div class="k">下载端 IP</div><div class="v">${esc(d.clientIp || '—')}</div></div>
      <div class="row"><div class="k">请求人</div><div class="v">${esc(d.requestedByName || '—')}</div></div>
      ${d.errorMessage ? `<div class="row"><div class="k">错误</div><div class="v" style="color:var(--err-fg)">${esc(d.errorMessage)}</div></div>` : ''}
    </div>`
    });
    if (!viaNav) history.replaceState(null, '', '#/restores/' + id);
  } catch (e) { errToast(e); }
}
ACTIONS['restores:detail'] = async id => openRestoreDrawer(id);

ACTIONS['restores:token'] = async id => {
  if (!await confirmModal('签发新下载令牌？签发后旧令牌立即失效，明文令牌仅显示这一次。')) return;
  const d = await api(`/api/v1/admin/restore-requests/${id}/download-token`, { method: 'POST' });
  const url = location.origin + d.downloadUrl;
  openModal('下载令牌已签发', `
    <p style="margin-bottom:10px">有效期至 <b>${fmtDT(d.expiresAt)}</b>。整套备份集下载（ZIP）：</p>
    <div class="frow"><input readonly value="${esc(url)}" data-ui-action="select-self" class="mono"></div>
    <p style="margin:10px 0;color:var(--text-muted);font:var(--t-sm)">单文件下载：在 URL 后追加 <span class="mono">?path=相对路径</span>（支持 Range 断点续传）。</p>
    <div style="display:flex;gap:10px">
      <a class="primary" style="padding:var(--s-1) var(--s-3);border-radius:var(--r-md);color:var(--accent-fg);background:var(--accent)" href="${esc(url)}" target="_blank">打开下载</a>
      <button data-ui-action="copy" data-copy-value="${esc(url)}">复制链接</button>
    </div>`);
  LOADERS.restores();
};



