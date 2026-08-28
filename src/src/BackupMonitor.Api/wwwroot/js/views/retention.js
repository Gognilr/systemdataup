/* js/views/retention.js —— 保留策略管理 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, fmtDT, tableHtml, emptyState, toast, errToast,
  confirmModal, formModal, actBtn
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
    const def = (list || []).find(p => p.isDefault);
    // 「哪份是默认」此前只有行内一个徽标，要先扫完整张表才看得出来。默认策略管着所有
    // 没单独配过的任务，值得在页顶直接说清楚。
    const notice = def
      ? `<div class="notice">新建任务、以及保留策略选「跟随默认」的任务，都按 <strong>${esc(def.name)}</strong> 清理。想换一份，在下面那一行点「设为默认」。</div>`
      : `<div class="notice warning">当前没有配置默认策略。不选策略的任务将<strong>永不清理</strong>，仓库会一直涨——请在下面挑一份点「设为默认」。</div>`;
    wrap.innerHTML = notice + tableHtml([
      { l: '策略名', render: r => `<b>${esc(r.name)}</b>${r.isDefault ? ' <span class="status status--ok pill">新建任务默认</span>' : ''}` },
      { l: '最近 N 份', render: r => r.keepLastCount ?? '—' },
      { l: '周版本', render: r => r.keepWeeklyCount ?? '—' },
      { l: '月版本', render: r => r.keepMonthlyCount ?? '—' },
      { l: '年版本', render: r => r.keepYearlyCount ?? '—' },
      { l: '最短保留(天)', num: true, k: 'minimumRetentionDays' },
      { l: '回收站(天)', num: true, k: 'recycleBinDays' },
      { l: '绑定任务数', num: true, render: r => r.boundTaskCount ? `<span class="status status--busy pill">${esc(r.boundTaskCount)}</span>` : '0' },
      { l: '更新时间', render: r => fmtDT(r.updatedAt) },
      { l: '操作', render: r => [
        actBtn({
          label: '设为默认', view: 'retention', action: 'setdefault', id: r.id,
          allowed: !r.isDefault,
          why: '这份已经是新建任务的默认策略',
          hint: '立即生效：此后新建的任务、以及所有「跟随默认」的任务都按这份策略清理'
        }),
        `<button class="small" data-ui-action="act" data-view="retention" data-action="edit" data-id="${esc(r.id)}">编辑</button>`,
        `<button class="small danger" data-ui-action="act" data-view="retention" data-action="del" data-id="${esc(r.id)}">删除</button>`
      ].join(' ') }
    ], list, { empty: emptyState('first', { glyph: '◫', title: '暂无保留策略', sub: '为备份任务配置可审计的版本保留规则' }) });
    App.state.retentionItems = list;
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};
// 绝大多数人要的只有一句话：「留最近几份，删掉的先放回收站几天」。
// 周/月/年那三项是 GFS 祖父-父-子模型，收进高级选项——铺开七个输入框
// 会把「必须填什么」淹掉，而它正是这个表单唯一真正要人回答的问题。
function retentionFields(v = {}) {
  return [
    { name: 'name', label: '策略名称', type: 'text', value: v.name, required: true, placeholder: '如 只留最近 7 份' },
    { name: 'keepLastCount', label: '保留最近 N 份', type: 'number', value: v.keepLastCount ?? 7,
      hint: '超出这个份数的旧备份会被移进回收站。留空则改由下面高级选项里的周/月/年规则决定' },
    { name: 'recycleBinDays', label: '删掉的先放回收站几天', type: 'number', value: v.recycleBinDays ?? 7,
      hint: '这段时间内还能捞回来，到期才真正从磁盘上删除' },
    { name: 'keepWeeklyCount', label: '保留周版本数', type: 'number', value: v.keepWeeklyCount ?? '', advanced: true,
      hint: '每周额外留一份最新的，共留几周。留空表示不按周留' },
    { name: 'keepMonthlyCount', label: '保留月末版本数', type: 'number', value: v.keepMonthlyCount ?? '', advanced: true },
    { name: 'keepYearlyCount', label: '保留年度版本数', type: 'number', value: v.keepYearlyCount ?? '', advanced: true },
    { name: 'minimumRetentionDays', label: '最短保留天数', type: 'number', value: v.minimumRetentionDays ?? 0, advanced: true,
      hint: '不满这个天数的备份一律不回收——它会盖过上面的份数设置：填 30 的话，每天备份也会攒到 30 份以上，「只留 7 份」不生效。默认 0 表示按份数说了算' }
  ];
}
function retentionBody(v) {
  const num = s => s === '' || s == null ? null : Number(s);
  return {
    name: v.name, keepLastCount: num(v.keepLastCount), keepWeeklyCount: num(v.keepWeeklyCount),
    keepMonthlyCount: num(v.keepMonthlyCount), keepYearlyCount: num(v.keepYearlyCount),
    minimumRetentionDays: Number(v.minimumRetentionDays) || 0, recycleBinDays: Number(v.recycleBinDays) || 7
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
// 换默认策略不只影响以后新建的任务：所有保留策略留空（= 跟随默认）的任务会立刻改按新策略清理，
// 所以先说清楚影响面再动手。它不是危险动作——两边都是有效策略，换错了再换回来即可。
ACTIONS['retention:setdefault'] = async id => {
  const item = (App.state.retentionItems || []).find(x => x.id === id);
  if (!await confirmModal(
    `把「${item ? item.name : '这份策略'}」设为新建任务的默认策略？`
    + '此后新建的任务默认按它清理；已有任务里保留策略选了「跟随默认」的，下一轮清理起也改按它走；'
    + '单独配过策略的任务不受影响。')) return;
  await api(`/api/v1/admin/retention-policies/${id}/set-default`, { method: 'POST' });
  toast('默认策略已切换', 'ok'); LOADERS.retention();
};
ACTIONS['retention:del'] = async id => {
  if (!await confirmModal('删除该保留策略？仍有任务绑定、或它是新建任务的默认策略时会被拒绝（409）。')) return;
  await api(`/api/v1/admin/retention-policies/${id}`, { method: 'DELETE' });
  toast('策略已删除', 'ok'); LOADERS.retention();
};
