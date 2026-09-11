/* js/views/alerts.js —— 告警列表（可排序/多选批量确认）/ 抽屉详情 / 处理动作 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtDT, absTime, prettyJson,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter, batchBarHtml,
  toast, errToast, formModal, openDrawer
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vAlerts() {
  App.state.alerts = App.state.alerts || { page: 1, pageSize: 20, level: '', status: '', assignedToMe: false, totalCount: 0, selected: [], sortKey: 'lastOccurredAt', sortDesc: true };
  const st = App.state.alerts;
  $('#app').innerHTML = shell('alerts', '告警', `
    <div class="toolbar">
      <select id="f_level"><option value="">全部等级</option>${optsOf(L.alert_level).map(o => `<option value="${o.v}" ${st.level === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <select id="f_status"><option value="">全部状态</option>${optsOf(L.alert_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <label class="inline"><input id="f_mine" type="checkbox" ${st.assignedToMe ? 'checked' : ''}> 指派给我</label>
      <button class="primary" data-ui-action="loader" data-loader="alerts">查询</button>
    </div>
    <div id="alert-summary-chips" class="filter-chips" aria-label="告警摘要筛选"></div>
    <div id="bb-alerts">${batchBarHtml('alerts')}</div>
    <div id="vwrap">${loading()}</div>`);
  $('#f_level').onchange = () => { st.level = $('#f_level').value; st.page = 1; LOADERS.alerts(); };
  $('#f_status').onchange = () => { st.status = $('#f_status').value; st.page = 1; LOADERS.alerts(); };
  $('#f_mine').onchange = () => { st.assignedToMe = $('#f_mine').checked; st.page = 1; LOADERS.alerts(); };
  api('/api/v1/admin/reports/alert-summary').then(summary => {
    const root = $('#alert-summary-chips');
    if (!root) return;
    const byLevel = (summary.byLevel || []).map(item =>
      `<button class="filter-chip ${st.level === item.value ? 'active' : ''}" data-ui-action="filter" data-filter-key="alerts" data-filter-field="level" data-filter-value="${esc(item.value)}">${esc(L.alert_level[item.value] || item.value)} <strong>${esc(item.count)}</strong></button>`).join('');
    const byStatus = (summary.byStatus || []).map(item =>
      `<button class="filter-chip ${st.status === item.value ? 'active' : ''}" data-ui-action="filter" data-filter-key="alerts" data-filter-field="status" data-filter-value="${esc(item.value)}">${esc(L.alert_status[item.value] || item.value)} <strong>${esc(item.count)}</strong></button>`).join('');
    root.innerHTML = byLevel + byStatus;
  }).catch(() => {});
  await LOADERS.alerts();
}

LOADERS.alerts = async function () {
  const st = App.state.alerts;
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize, sortDescending: String(st.sortDesc !== false) });
    if (st.sortKey) q.set('sortBy', st.sortKey);
    if (st.level) q.set('level', st.level);
    if (st.status) q.set('status', st.status);
    if (st.assignedToMe && App.user?.id) q.set('assignedTo', App.user.id);
    const data = await api('/api/v1/admin/alerts?' + q);
    st.totalCount = data.totalCount;
    const filtered = hasFilter(st, ['level', 'status']);
    // 「正常的空」是好消息，要说得像好消息（§6.5）
    const empty = filtered
      ? emptyState('filter', { key: 'alerts', title: '没有匹配当前筛选条件的告警' })
      : emptyState('ok', { title: '没有告警', sub: '系统运行正常，暂无需要关注的异常' });
    wrap.innerHTML = tableHtml([
      { l: '等级', render: r => status('alert_level', r.level) },
      { l: '状态', render: r => `${status('alert_status', r.status)}${r.isSilenced ? ' <span class="status status--mut pill">静默中</span>' : ''}` },
      { l: '标题', render: r => `<b>${esc(r.title)}</b><span class="sub">${esc((r.message || '').slice(0, 80))}</span>` },
      { l: '类别', render: r => esc(L.alert_category[r.category] || r.category || '—') },
      { l: '客户端 / 任务', render: r => `${esc(r.clientHostname || '—')}<span class="sub">${esc(r.taskName || '')}</span>` },
      { l: '指派给', render: r => esc(r.assignedToName || '—') },
      { l: '次数', num: true, k: 'occurrenceCount' },
      // 告警列表最需要绝对时间：排障时全靠它跟服务端日志、Windows 事件查看器对时刻，
      // 而「3 分钟前」要人在心里先做一次减法——那一步错一次就是找错方向（R19）。
      { l: '最近发生', sort: true, k: 'lastOccurredAt', render: r => absTime(r.lastOccurredAt) },
      { l: '操作', render: r => {
        const b = [`<button class="small" data-ui-action="act" data-view="alerts" data-action="detail" data-id="${esc(r.id)}">详情</button>`];
        if (r.status === 'open') b.push(`<button class="small primary" data-ui-action="act" data-view="alerts" data-action="ack" data-id="${esc(r.id)}">确认</button>`);
        if (['open', 'acknowledged'].includes(r.status)) {
          b.push(`<button class="small" data-ui-action="act" data-view="alerts" data-action="handle" data-id="${esc(r.id)}">处理</button>`);
          b.push(`<button class="small" data-ui-action="act" data-view="alerts" data-action="close" data-id="${esc(r.id)}">关闭</button>`);
        }
        b.push(r.assignedTo
          ? `<button class="small" data-ui-action="act" data-view="alerts" data-action="unassign" data-id="${esc(r.id)}">取消指派</button>`
          : `<button class="small" data-ui-action="act" data-view="alerts" data-action="assign-me" data-id="${esc(r.id)}">指派给我</button>`);
        if (!r.isSilenced) b.push(`<button class="small" data-ui-action="act" data-view="alerts" data-action="silence" data-id="${esc(r.id)}">静默</button>`);
        return b.join(' ');
      } }
    ], data.items, { empty, stateKey: 'alerts' }) + pagerHtml('alerts', st);
    const bb = $('#bb-alerts');
    if (bb) bb.outerHTML = `<div id="bb-alerts">${batchBarHtml('alerts')}</div>`;
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

App.batchActs = App.batchActs || {};
App.batchActs.alerts = [
  { t: '批量确认', primary: true,
    fn: async id => { await api(`/api/v1/admin/alerts/${id}/acknowledge`, { method: 'POST' }); } }
];

/* §6.3 告警详情抽屉 */
export async function openAlertDrawer(id, viaNav = false) {
  try {
    const d = await api(`/api/v1/admin/alerts/${id}`);
    openDrawer({
      title: '告警详情',
      wide: true,
      onClose: () => { if (location.hash === '#/alerts/' + id) history.replaceState(null, '', '#/alerts'); },
      bodyHtml: `
    <div class="kv">
      <div class="row"><div class="k">标题</div><div class="v"><b>${esc(d.title)}</b></div></div>
      <div class="row"><div class="k">等级 / 状态</div><div class="v">${status('alert_level', d.level)} ${status('alert_status', d.status)}</div></div>
      <div class="row"><div class="k">类别</div><div class="v">${esc(L.alert_category[d.category] || d.category || '—')}</div></div>
      <div class="row"><div class="k">客户端 / 任务</div><div class="v">${esc(d.clientHostname || '—')} / ${esc(d.taskName || '—')}</div></div>
      <div class="row"><div class="k">首次 / 最近发生</div><div class="v">${fmtDT(d.firstOccurredAt)} / ${fmtDT(d.lastOccurredAt)}</div></div>
      <div class="row"><div class="k">发生次数</div><div class="v">${esc(d.occurrenceCount)}</div></div>
      <div class="row"><div class="k">确认</div><div class="v">${d.acknowledgedAt ? fmtDT(d.acknowledgedAt) + '（' + esc(d.acknowledgedByName || '') + '）' : '—'}</div></div>
      <div class="row"><div class="k">指派</div><div class="v">${d.assignedTo ? esc(d.assignedToName || '') + '（' + fmtDT(d.assignedAt) + '）' : '—'}
        ${d.assignedTo
          ? ` <button class="small" data-ui-action="act" data-view="alerts" data-action="unassign" data-id="${esc(d.id)}">取消指派</button>`
          : ` <button class="small" data-ui-action="act" data-view="alerts" data-action="assign-me" data-id="${esc(d.id)}">指派给我</button>`}</div></div>
      <div class="row"><div class="k">静默</div><div class="v">${d.isSilenced ? '<span class="status status--mut pill">静默中</span>' : '未静默'}
        ${!d.isSilenced ? ` <button class="small" data-ui-action="act" data-view="alerts" data-action="silence" data-id="${esc(d.id)}">创建临时静默</button>` : ''}</div></div>
      <div class="row"><div class="k">恢复 / 关闭</div><div class="v">${fmtDT(d.recoveredAt)} / ${fmtDT(d.closedAt)}</div></div>
      <div class="row"><div class="k">处理备注</div><div class="v">${esc(d.handlingNote || '—')}</div></div>
    </div>
    <h3>告警消息</h3><pre class="json">${esc(d.message || '—')}</pre>
    ${d.metadata ? `<h3>附加信息</h3><pre class="json">${esc(prettyJson(d.metadata))}</pre>` : ''}
    <details class="fadv"><summary>内部标识（排查用）</summary><div class="kv">
      <div class="row"><div class="k">去重键</div><div class="v mono">${esc(d.alertKey)}</div></div>
      <div class="row"><div class="k">类别标识</div><div class="v mono">${esc(d.category || '—')}</div></div>
    </div></details>`
    });
    if (!viaNav) history.replaceState(null, '', '#/alerts/' + id);
  } catch (e) { errToast(e); }
}
ACTIONS['alerts:detail'] = async id => openAlertDrawer(id);
ACTIONS['alerts:ack'] = async id => {
  await api(`/api/v1/admin/alerts/${id}/acknowledge`, { method: 'POST' });
  toast('告警已确认', 'ok'); LOADERS.alerts();
};
ACTIONS['alerts:handle'] = async id => {
  formModal('更新告警处理状态', [
    { name: 'status', label: '目标状态', type: 'select', options: [{ v: 'in_progress', t: '处理中' }, { v: 'ignored', t: '忽略' }] },
    { name: 'note', label: '处理备注', type: 'textarea' }
  ], async v => {
    await api(`/api/v1/admin/alerts/${id}/handle`, { method: 'POST', body: { status: v.status, note: v.note || null } });
    toast('告警状态已更新', 'ok'); LOADERS.alerts();
  }, '提交');
};
ACTIONS['alerts:close'] = async id => {
  formModal('关闭告警', [{ name: 'note', label: '关闭说明', type: 'textarea' }], async v => {
    await api(`/api/v1/admin/alerts/${id}/close`, { method: 'POST', body: { note: v.note || null } });
    toast('告警已关闭', 'ok'); LOADERS.alerts();
  }, '关闭');
};
/* D5：指派给自己——没有用户管理模块可供挑选"指派给谁"，
   验收标准只要求"指派后可按指派给我筛选"，因此这里做成快捷动作而不是一个人员下拉框。 */
ACTIONS['alerts:assign-me'] = async id => {
  if (!App.user?.id) { toast('无法识别当前用户', 'err'); return; }
  await api(`/api/v1/admin/alerts/${id}/assign`, { method: 'POST', body: { assignedTo: App.user.id } });
  toast('已指派给我', 'ok'); LOADERS.alerts();
};
ACTIONS['alerts:unassign'] = async id => {
  await api(`/api/v1/admin/alerts/${id}/assign`, { method: 'POST', body: { assignedTo: null } });
  toast('已取消指派', 'ok'); LOADERS.alerts();
};
ACTIONS['alerts:silence'] = async id => {
  formModal('创建临时静默', [
    { name: 'hours', label: '静默时长（小时）', type: 'number', value: 2, required: true,
      hint: '1~720 小时（30 天）。默认按该告警所属客户端的全部后续告警静默，覆盖维护窗口场景' },
    { name: 'reason', label: '原因', type: 'text', placeholder: '如：本周六凌晨系统维护窗口' }
  ], async v => {
    await api(`/api/v1/admin/alerts/${id}/silence`, { method: 'POST',
      body: { hours: Number(v.hours) || 2, reason: v.reason || null } });
    toast('静默已创建，期间新告警不再发通知（计数仍会累加）', 'ok'); LOADERS.alerts();
  }, '创建静默');
};
