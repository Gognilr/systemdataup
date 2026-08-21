/* js/views/retention.js —— 保留策略管理 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, fmtDT, tableHtml, emptyState, toast, errToast,
  confirmModal, formModal
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vRetention() {
  $('#app').innerHTML = shell('retention', '保留策略', `
    <div class="toolbar"><div class="spacer"></div><button class="primary" data-ui-action="act" data-view="retention" data-action="create">＋ 新建策略</button></div>
    <div id="vwrap">${loading()}</div>`);
  await LOADERS.retention();
}
LOADERS.retention = async function () {
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const list = await api('/api/v1/admin/retention-policies');
    wrap.innerHTML = tableHtml([
      { l: '策略名', render: r => `<b>${esc(r.name)}</b>` },
      { l: '最近 N 个', render: r => r.keepLastCount ?? '—' },
      { l: '周版本', render: r => r.keepWeeklyCount ?? '—' },
      { l: '月版本', render: r => r.keepMonthlyCount ?? '—' },
      { l: '年版本', render: r => r.keepYearlyCount ?? '—' },
      { l: '最短保留(天)', num: true, k: 'minimumRetentionDays' },
      { l: '回收区(天)', num: true, k: 'recycleBinDays' },
      { l: '绑定任务数', num: true, render: r => r.boundTaskCount ? `<span class="status status--busy pill">${esc(r.boundTaskCount)}</span>` : '0' },
      { l: '更新时间', render: r => fmtDT(r.updatedAt) },
      { l: '操作', render: r => `<button class="small" data-ui-action="act" data-view="retention" data-action="edit" data-id="${esc(r.id)}">编辑</button> <button class="small danger" data-ui-action="act" data-view="retention" data-action="del" data-id="${esc(r.id)}">删除</button>` }
    ], list, { empty: emptyState('first', { glyph: '◫', title: '暂无保留策略', sub: '为备份任务配置可审计的版本保留规则' }) });
    App.state.retentionItems = list;
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};
function retentionFields(v = {}) {
  return [
    { name: 'name', label: '策略名称', type: 'text', value: v.name, required: true },
    { name: 'keepLastCount', label: '保留最近 N 个版本', type: 'number', value: v.keepLastCount ?? '', hint: '留空表示不限' },
    { name: 'keepWeeklyCount', label: '保留周版本数', type: 'number', value: v.keepWeeklyCount ?? '' },
    { name: 'keepMonthlyCount', label: '保留月末版本数', type: 'number', value: v.keepMonthlyCount ?? '' },
    { name: 'keepYearlyCount', label: '保留年度版本数', type: 'number', value: v.keepYearlyCount ?? '' },
    { name: 'minimumRetentionDays', label: '最短保留天数', type: 'number', value: v.minimumRetentionDays ?? 30, hint: '小于此天数的版本不回收' },
    { name: 'recycleBinDays', label: '回收区保留天数', type: 'number', value: v.recycleBinDays ?? 30 }
  ];
}
function retentionBody(v) {
  const num = s => s === '' || s == null ? null : Number(s);
  return {
    name: v.name, keepLastCount: num(v.keepLastCount), keepWeeklyCount: num(v.keepWeeklyCount),
    keepMonthlyCount: num(v.keepMonthlyCount), keepYearlyCount: num(v.keepYearlyCount),
    minimumRetentionDays: Number(v.minimumRetentionDays) || 0, recycleBinDays: Number(v.recycleBinDays) || 0
  };
}
ACTIONS['retention:create'] = async () => {
  formModal('新建保留策略', retentionFields(), async v => {
    await api('/api/v1/admin/retention-policies', { method: 'POST', body: retentionBody(v) });
    toast('策略已创建', 'ok'); LOADERS.retention();
  }, '创建');
};
ACTIONS['retention:edit'] = async id => {
  const item = (App.state.retentionItems || []).find(x => x.id === id) || await api(`/api/v1/admin/retention-policies/${id}`);
  formModal(`编辑策略：${item.name}`, retentionFields(item), async v => {
    await api(`/api/v1/admin/retention-policies/${id}`, { method: 'PUT', body: retentionBody(v) });
    toast('策略已保存', 'ok'); LOADERS.retention();
  }, '保存');
};
ACTIONS['retention:del'] = async id => {
  if (!await confirmModal('删除该保留策略？若仍有任务绑定将被拒绝（409）。')) return;
  await api(`/api/v1/admin/retention-policies/${id}`, { method: 'DELETE' });
  toast('策略已删除', 'ok'); LOADERS.retention();
};
