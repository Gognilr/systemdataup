/* js/views/audit.js —— 审计日志列表与只读详情 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, status, fmtDT, relTime, prettyJson, tableHtml, pagerHtml,
  emptyState, hasFilter, openDrawer, errToast
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vAudit() {
  App.state.audit = App.state.audit || { page: 1, pageSize: 20, action: '', result: '', totalCount: 0, items: [] };
  const st = App.state.audit;
  $('#app').innerHTML = shell('audit', '审计日志', `
    <div class="toolbar">
      <input id="f_action" placeholder="动作（如 restore.create）" value="${esc(st.action)}" style="min-width:190px">
      <select id="f_result"><option value="">全部结果</option><option value="success" ${st.result === 'success' ? 'selected' : ''}>成功</option><option value="failure" ${st.result === 'failure' ? 'selected' : ''}>失败</option></select>
      <button class="primary" data-ui-action="loader" data-loader="audit">查询</button>
    </div>
    <div id="vwrap">${loading()}</div>`);
  $('#f_action').onkeydown = ev => { if (ev.key === 'Enter') { st.action = ev.target.value.trim(); st.page = 1; LOADERS.audit(); } };
  $('#f_result').onchange = () => { st.result = $('#f_result').value; st.page = 1; LOADERS.audit(); };
  await LOADERS.audit();
}

LOADERS.audit = async function () {
  const st = App.state.audit;
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize, sortDescending: 'true' });
    if (st.action) q.set('action', st.action);
    if (st.result) q.set('result', st.result);
    const data = await api('/api/v1/admin/audit-logs?' + q);
    st.totalCount = data.totalCount;
    st.items = data.items || [];
    const empty = hasFilter(st, ['action', 'result'])
      ? emptyState('filter', { key: 'audit', title: '没有匹配当前筛选条件的审计记录' })
      : emptyState('first', { glyph: '≡', title: '暂无审计记录', sub: '管理员动作会在这里留下只读、不可修改的记录' });
    wrap.innerHTML = tableHtml([
      { l: '时间', render: r => relTime(r.occurredAt) },
      { l: '用户', k: 'usernameSnapshot' },
      { l: '动作', render: r => `<span class="mono">${esc(r.action)}</span>` },
      { l: '资源', render: r => `${esc(r.resourceType || '—')}<br><span class="mono sub">${esc(String(r.resourceId || '').slice(0, 8) || '—')}</span>` },
      { l: '结果', render: r => status('result', r.result) },
      { l: '错误码', render: r => esc(r.errorCode || '—') },
      { l: '来源 IP', k: 'clientIp' },
      { l: '请求 ID', render: r => `<span class="mono">${esc(String(r.requestId || '').slice(0, 8) || '—')}</span>` },
      { l: '操作', render: r => (r.beforeData || r.afterData) ? `<button class="small" data-ui-action="act" data-view="audit" data-action="detail" data-id="${esc(r.id)}">变更</button>` : '—' }
    ], st.items, { empty }) + pagerHtml('audit', st);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

export async function openAuditDrawer(id, viaNav = false) {
  try {
    let item = (App.state.audit?.items || []).find(x => x.id === id);
    if (!item) {
      const q = new URLSearchParams({ page: 1, pageSize: 1, resourceId: id });
      const data = await api('/api/v1/admin/audit-logs?' + q);
      item = data.items?.[0];
    }
    if (!item) throw new Error('找不到该审计记录');
    openDrawer({
      title: `审计变更：${item.action}`,
      wide: true,
      onClose: () => { if (location.hash === '#/audit/' + id) history.replaceState(null, '', '#/audit'); },
      bodyHtml: `<div class="kv">
        <div class="row"><div class="k">时间</div><div class="v">${fmtDT(item.occurredAt)}</div></div>
        <div class="row"><div class="k">用户</div><div class="v">${esc(item.usernameSnapshot || '—')}</div></div>
        <div class="row"><div class="k">动作</div><div class="v mono">${esc(item.action)}</div></div>
        <div class="row"><div class="k">结果</div><div class="v">${status('result', item.result)}</div></div>
        <div class="row"><div class="k">错误</div><div class="v">${esc(item.errorMessage || '—')}</div></div>
      </div><h3>变更前</h3><pre class="json">${esc(prettyJson(item.beforeData))}</pre><h3>变更后</h3><pre class="json">${esc(prettyJson(item.afterData))}</pre>`
    });
    if (!viaNav) history.replaceState(null, '', '#/audit/' + id);
  } catch (e) { errToast(e); }
}
ACTIONS['audit:detail'] = async id => openAuditDrawer(id);

