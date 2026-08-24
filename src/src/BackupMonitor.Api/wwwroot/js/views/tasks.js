/* js/views/tasks.js —— 备份任务列表 / 表单 / 抽屉详情 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtBytes, fmtDT, relTime, shortId, prettyJson,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter, batchBarHtml,
  toast, errToast, confirmModal, formModal, openDrawer
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vTasks() {
  App.state.tasks = App.state.tasks || { page: 1, pageSize: 20, mode: '', totalCount: 0, selected: [] , sortKey: 'createdAt', sortDesc: true};
  const st = App.state.tasks;
  $('#app').innerHTML = shell('tasks', '备份任务', `
    <div class="toolbar">
      <select id="f_mode"><option value="">全部模式</option>${optsOf(L.task_mode).map(o => `<option value="${o.v}" ${st.mode === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <button class="primary" data-ui-action="loader" data-loader="tasks">查询</button>
      <div class="spacer"></div>
      <button class="primary" data-ui-action="act" data-view="tasks" data-action="create">＋ 新建任务</button>
    </div>
    <div id="bb-tasks">${batchBarHtml('tasks')}</div>
    <div id="vwrap">${loading()}</div>`);
  $('#f_mode').onchange = () => { st.mode = $('#f_mode').value; st.page = 1; LOADERS.tasks(); };
  await LOADERS.tasks();
}

LOADERS.tasks = async function () {
  const st = App.state.tasks;
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.sortKey) { q.set('sortBy', st.sortKey); q.set('sortDescending', String(st.sortDesc !== false)); }
    if (st.mode) q.set('taskMode', st.mode);
    const data = await api('/api/v1/admin/backup-tasks?' + q);
    st.totalCount = data.totalCount;
    const empty = !hasFilter(st, ['mode'])
      ? emptyState('first', { glyph: '⧉', title: '还没有备份任务', sub: '创建任务后，Agent 将按计划扫描并采集备份产物', actHtml: '<button class="primary" data-ui-action="act" data-view="tasks" data-action="create">＋ 新建任务</button>' })
      : emptyState('filter', { key: 'tasks', title: `没有匹配「${L.task_mode[st.mode] || ''}」的任务` });
    wrap.innerHTML = tableHtml([
      { l: '任务名', k: 'name', sort: true, render: r => `<b>${esc(r.name)}</b><span class="sub">${esc(r.applicationName)}</span>` },
      { l: '客户端', k: 'clientHostname' },
      { l: '源路径', render: r => `<span class="mono" style="font-size:12px">${esc(r.sourcePath)}</span>` },
      { l: '识别器', render: r => esc(L.recognizer[r.recognizerType] || r.recognizerType) },
      { l: '模式', k: 'taskMode', sort: true, render: r => status('task_mode', r.taskMode) },
      { l: '启用', k: 'enabled', sort: true, render: r => r.enabled ? '是' : '否' },
      { l: '重要级', render: r => esc(L.importance[r.importanceLevel] || r.importanceLevel) },
      { l: '最近预检', render: r => r.lastPrecheckStatus ? status('result', r.lastPrecheckStatus) : '—' },
      { l: '最近成功', k: 'lastSuccessAt', sort: true, render: r => relTime(r.lastSuccessAt) },
      { l: '操作', render: r => {
        const b = [`<button class="small" data-ui-action="act" data-view="tasks" data-action="detail" data-id="${esc(r.id)}">详情</button>`,
          `<button class="small" data-ui-action="act" data-view="tasks" data-action="edit" data-id="${esc(r.id)}">编辑</button>`];
        b.push(r.taskMode === 'paused'
          ? `<button class="small primary" data-ui-action="act" data-view="tasks" data-action="resume" data-id="${esc(r.id)}">恢复</button>`
          : `<button class="small" data-ui-action="act" data-view="tasks" data-action="pause" data-id="${esc(r.id)}">暂停</button>`);
        b.push(`<button class="small" data-ui-action="act" data-view="tasks" data-action="precheck" data-id="${esc(r.id)}">预检</button>`);
        b.push(`<button class="small danger" data-ui-action="act" data-view="tasks" data-action="del" data-id="${esc(r.id)}">删除</button>`);
        return b.join(' ');
      } }
    ], data.items, { empty, stateKey: 'tasks' }) + pagerHtml('tasks', st);
    const bb = $('#bb-tasks');
    if (bb) bb.outerHTML = `<div id="bb-tasks">${batchBarHtml('tasks')}</div>`;
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

App.batchActs = App.batchActs || {};
App.batchActs.tasks = [
  { t: '批量预检', fn: id => api(`/api/v1/admin/backup-tasks/${id}/precheck`, { method: 'POST' }) },
  { t: '暂停', fn: id => api(`/api/v1/admin/backup-tasks/${id}/pause`, { method: 'POST' }) },
  { t: '恢复', fn: id => api(`/api/v1/admin/backup-tasks/${id}/resume`, { method: 'POST' }) }
];

async function taskFormFields(initial) {
  const [clients, policies] = await Promise.all([
    api('/api/v1/admin/clients?page=1&pageSize=200'),
    api('/api/v1/admin/retention-policies')
  ]);
  const clientOpts = clients.items.map(c => ({ v: c.id, t: `${c.hostname}（${L.client_status[c.status] || c.status}）` }));
  const policyOpts = [{ v: '', t: '（不绑定）' }].concat((policies || []).map(p => ({ v: p.id, t: p.name })));
  const v = initial || {};
  const fields = [];
  // 建任务只有四件事必须由人决定：哪台机器、叫什么、备份的是什么应用、备份文件落在哪个目录。
  // 其余十项全部有可用默认值，收进「高级选项」——铺开十四个输入框会把「必须填什么」淹掉。
  if (!initial) {
    fields.push({
      name: 'clientId', label: '客户端', type: 'select', options: clientOpts, value: v.clientId, required: true,
      hint: clientOpts.length ? '任务在哪台机器上执行' : '暂无已登记的客户端——请先在客户端机器上安装 Agent 并完成审批'
    });
  }
  fields.push(
    { name: 'name', label: '任务名称', type: 'text', value: v.name, required: true, placeholder: '如 财务库每日全备' },
    { name: 'applicationName', label: '应用名称', type: 'text', value: v.applicationName, required: true, placeholder: '如 SQLServer / Oracle / FileSet' },
    { name: 'sourcePath', label: '源路径', type: 'text', value: v.sourcePath, required: true,
      placeholder: 'D:\\backup\\finance', hint: '客户端机器上的路径，不是服务端上的路径' },
    { name: 'recognizerType', label: '识别器类型', type: 'select', value: v.recognizerType || 'latest_directory', required: true,
      options: optsOf(L.recognizer), hint: '源路径下如何认出「一份备份」' },
    { name: 'taskMode', label: '任务模式', type: 'select', value: v.taskMode || 'approval_required',
      options: optsOf(L.task_mode).filter(o => o.v !== 'paused'),
      hint: '需审批：扫到的备份要人工确认后才入库；自动：扫到即入库' },
    { name: 'importanceLevel', label: '重要级', type: 'select', value: v.importanceLevel || 'normal', options: optsOf(L.importance), advanced: true },
    { name: 'enabled', label: '状态', labelText: '启用该任务', type: 'checkbox', value: v.enabled !== false, advanced: true },
    { name: 'priority', label: '优先级', type: 'number', value: v.priority ?? 100, hint: '数值小的优先', advanced: true },
    { name: 'scanSchedule', label: '扫描计划（cron，可选）', type: 'text', value: v.scanSchedule || '', placeholder: '0 2 * * *', advanced: true,
      hint: '五段式：分 时 日 月 周，按客户端所在时区触发。留空则只能手动点预检' },
    { name: 'uploadWindowStart', label: '上传窗口开始', type: 'text', value: v.uploadWindowStart || '', placeholder: 'HH:mm:ss，可选', advanced: true },
    { name: 'uploadWindowEnd', label: '上传窗口结束', type: 'text', value: v.uploadWindowEnd || '', placeholder: 'HH:mm:ss，可选', advanced: true },
    { name: 'retentionPolicyId', label: '保留策略', type: 'select', value: v.retentionPolicyId || '', options: policyOpts, advanced: true },
    { name: 'recognizerConfig', label: '识别规则配置（JSON）', type: 'textarea', value: v.recognizerConfig || '{}', advanced: true,
      hint: '留空或 {} 表示用识别器默认规则' }
  );
  return fields;
}
function taskFormValues(vals) {
  const body = {
    name: vals.name, applicationName: vals.applicationName, sourcePath: vals.sourcePath,
    recognizerType: vals.recognizerType, taskMode: vals.taskMode, enabled: vals.enabled,
    priority: Number(vals.priority) || 100, importanceLevel: vals.importanceLevel,
    scanSchedule: vals.scanSchedule || null,
    uploadWindowStart: vals.uploadWindowStart || null, uploadWindowEnd: vals.uploadWindowEnd || null,
    retentionPolicyId: vals.retentionPolicyId || null,
    recognizerConfig: vals.recognizerConfig || '{}'
  };
  if (vals.clientId) body.clientId = vals.clientId;
  return body;
}

/* §6.3 任务详情抽屉（替换详情模态）；viaNav=true 表示从 #/tasks/{id} 直达 */
export async function openTaskDrawer(id, viaNav = false) {
  try {
    const d = await api(`/api/v1/admin/backup-tasks/${id}`);
    openDrawer({
      title: `任务详情：${d.name}`,
      wide: true,
      onClose: () => { if (location.hash === '#/tasks/' + id) history.replaceState(null, '', '#/tasks'); },
      bodyHtml: `
    <div class="kv">
      <div class="row"><div class="k">客户端</div><div class="v">${esc(d.clientHostname)}</div></div>
      <div class="row"><div class="k">应用</div><div class="v">${esc(d.applicationName)}</div></div>
      <div class="row"><div class="k">源路径</div><div class="v mono">${esc(d.sourcePath)}</div></div>
      <div class="row"><div class="k">识别器</div><div class="v">${esc(L.recognizer[d.recognizerType] || d.recognizerType)}</div></div>
      <div class="row"><div class="k">模式</div><div class="v">${status('task_mode', d.taskMode)}</div></div>
      <div class="row"><div class="k">启用</div><div class="v">${d.enabled ? '是' : '否'}</div></div>
      <div class="row"><div class="k">重要级 / 优先级</div><div class="v">${esc(L.importance[d.importanceLevel] || d.importanceLevel)} / ${esc(d.priority)}</div></div>
      <div class="row"><div class="k">扫描计划</div><div class="v">${esc(d.scanSchedule || '—')}</div></div>
      <div class="row"><div class="k">上传窗口</div><div class="v">${esc(d.uploadWindowStart || '—')} ~ ${esc(d.uploadWindowEnd || '—')}（${esc(d.scheduleTimezone)}）</div></div>
      <div class="row"><div class="k">稳定判定</div><div class="v">${esc(d.stabilityIntervalSeconds)}s / 最长 ${esc(d.maxStabilityWaitSeconds)}s</div></div>
      <div class="row"><div class="k">大小限制</div><div class="v">${fmtBytes(d.minTotalBytes)} ~ ${fmtBytes(d.maxTotalBytes)}，文件数 ≥ ${esc(d.minFileCount ?? '—')}</div></div>
      <div class="row"><div class="k">限速 / 分块</div><div class="v">${d.bandwidthLimitKbps ? esc(d.bandwidthLimitKbps) + ' KB/s' : '不限'} / ${fmtBytes(d.chunkSizeBytes)}</div></div>
      <div class="row"><div class="k">重试</div><div class="v">${esc(d.retryCount)} 次，间隔 ${esc(d.retryIntervalSeconds)}s</div></div>
      <div class="row"><div class="k">保留策略</div><div class="v">${esc(d.retentionPolicyName || '未绑定')}</div></div>
      <div class="row"><div class="k">最近扫描 / 成功</div><div class="v">${fmtDT(d.lastScanAt)} / ${fmtDT(d.lastSuccessAt)}</div></div>
      <div class="row"><div class="k">配置版本 / rowVersion</div><div class="v">${esc(d.configVersion)} / ${esc(d.rowVersion)}</div></div>
    </div>
    <h3>识别规则配置</h3><pre class="json">${esc(prettyJson(d.recognizerConfig))}</pre>
    ${d.alertConfig ? `<h3>告警配置</h3><pre class="json">${esc(prettyJson(d.alertConfig))}</pre>` : ''}`
    });
    if (!viaNav) history.replaceState(null, '', '#/tasks/' + id);
  } catch (e) { errToast(e); }
}

ACTIONS['tasks:create'] = async () => {
  const fields = await taskFormFields(null);
  formModal('新建备份任务', fields, async vals => {
    const d = await api('/api/v1/admin/backup-tasks', { method: 'POST', body: taskFormValues(vals) });
    toast(`任务「${d.name}」已创建`, 'ok'); LOADERS.tasks();
  }, '创建');
};
ACTIONS['tasks:edit'] = async id => {
  const detail = await api(`/api/v1/admin/backup-tasks/${id}`);
  const fields = await taskFormFields(detail);
  formModal(`编辑任务：${detail.name}`, fields, async vals => {
    const body = taskFormValues(vals);
    body.rowVersion = detail.rowVersion;
    const d = await api(`/api/v1/admin/backup-tasks/${id}`, { method: 'PUT', body });
    toast(`任务「${d.name}」已保存`, 'ok'); LOADERS.tasks();
  }, '保存');
};
ACTIONS['tasks:detail'] = async id => openTaskDrawer(id);
ACTIONS['tasks:pause'] = async id => { await api(`/api/v1/admin/backup-tasks/${id}/pause`, { method: 'POST' }); toast('任务已暂停', 'ok'); LOADERS.tasks(); };
ACTIONS['tasks:resume'] = async id => { await api(`/api/v1/admin/backup-tasks/${id}/resume`, { method: 'POST' }); toast('任务已恢复', 'ok'); LOADERS.tasks(); };
ACTIONS['tasks:precheck'] = async id => {
  const d = await api(`/api/v1/admin/backup-tasks/${id}/precheck`, { method: 'POST' });
  toast(`预检指令已下发（指令 ${shortId(d.commandId)}，${d.status}）`, 'ok');
};
ACTIONS['tasks:del'] = async id => {
  if (!await confirmModal('删除该备份任务？此操作不可恢复。')) return;
  await api(`/api/v1/admin/backup-tasks/${id}`, { method: 'DELETE' });
  toast('任务已删除', 'ok'); LOADERS.tasks();
};
