import { api } from '../api.js';
import { $, esc, emptyState, fmtBytes, fmtDT, fmtRate, fmtDuration, relTime, status, tableHtml, clientName, xferBar } from '../ui.js';
import { shell, loading } from '../app.js';

const byValue = (items, value) => (items || []).find(item => item.value === value)?.count || 0;

const DAY_STATUS = {
  success: { label: '成功', code: '✓', className: 'success' },
  failed: { label: '失败', code: '!', className: 'failed' },
  in_progress: { label: '进行中', code: '…', className: 'in-progress' },
  no_schedule: { label: '当日无计划', code: '—', className: 'no-schedule' }
};

function dateLabel(value) {
  const date = new Date(value);
  return `${date.getMonth() + 1}/${date.getDate()}`;
}

function dayStatus(value) {
  return DAY_STATUS[value] || DAY_STATUS.no_schedule;
}

/* ── 待办队列 ──
   判定规则只在服务端一份（ReportService.GetTodoSummaryAsync），与待办页读同一个端点。
   此前这里不调它，而是拿四个 summary 端点在浏览器里重算，于是和待办页有四处对不上：
   自动登记的客户端完全不统计、问题任务按热力图里有没有 failed 日算、
   严重告警把已确认的也算进来、恢复请求受 pageSize=100 封顶。
   局域网自动登记形态下第一条必然触发——客户端不需要审批，四项全 0，
   概览显示「一切正常」而待办页明明列着事项。 */
// href 指向「能把这件事做完的地方」：审批可以在待办页就地完成，
// 自动登记更是只有待办聚合算得出来（「最近 7 天 lan_simple 登记」没有对应的列表页筛选）；
// 其余三类要么只能过去看（任务），要么在专页上还有后续动作可做，仍指向原来的列表页。
const TODO_SECTIONS = [
  { key: 'pendingApprovalClients', title: '客户端等待审批', detail: '审批后才会签发客户端证书', href: '#/todo', name: c => c.hostname },
  { key: 'recentAutoEnrollments', title: '最近自动登记的客户端', detail: '7 天内自动上线，确认是不是预期内的机器', href: '#/todo', name: c => c.hostname },
  { key: 'problematicTasks', title: '备份任务需要检查', detail: '预检异常，或长时间没扫描且没有成功记录', href: '#/tasks', name: t => t.name },
  { key: 'readyRestores', title: '恢复请求等待签发', detail: '恢复请求已就绪，等待管理员确认', href: '#/restores', name: r => r.backupSetCode },
  { key: 'criticalAlerts', title: '严重告警未确认', detail: '严重告警需要先确认，再进入处理流程', href: '#/alerts', name: a => a.title }
];

const todoTotal = summary => TODO_SECTIONS.reduce((n, section) => n + (summary?.[section.key]?.count || 0), 0);

function renderTodoQueue(summary) {
  const rows = TODO_SECTIONS
    .map(section => ({ section, count: summary?.[section.key]?.count || 0, items: summary?.[section.key]?.items || [] }))
    .filter(row => row.count);

  if (!rows.length) {
    return emptyState('ok', { title: '没有待处理事项', sub: `最后检查 ${new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}` });
  }
  // 服务端一次把 count 和前几条明细都给了，顺手把头两条的名字带出来：
  // 光一个数字看不出「是哪台机器」，还得点进去才知道该不该管。
  return rows.map(({ section, count, items }) => {
    const names = items.slice(0, 2).map(section.name).filter(Boolean).join('、');
    return `<a class="dashboard-todo-item" href="${esc(section.href)}">
    <span class="todo-count">${esc(count)}</span>
    <span class="todo-copy"><strong>${esc(section.title)}</strong><small>${esc(names ? `${names}${count > 2 ? ' 等' : ''}` : section.detail)}</small></span>
    <span aria-hidden="true">→</span>
  </a>`;
  }).join('');
}

function renderMatrix(summary) {
  const dates = summary.dates || [];
  const rows = summary.matrix || [];
  if (!dates.length || !rows.length) {
    return emptyState('first', { title: '还没有可展示的备份任务', sub: '创建并启用任务后，这里会显示最近 14 天的执行状态' });
  }

  const head = dates.map(date => `<th class="day-head" scope="col"><time datetime="${esc(date)}">${dateLabel(date)}</time></th>`).join('');
  const heatRows = rows.map(row => `<tr>
    <td class="task-name"><a href="#/tasks/${esc(row.taskId)}">${esc(row.taskName)}</a><span class="sub">${esc(row.clientHostname || '')}</span></td>
    ${(row.days || []).map(day => {
      const info = dayStatus(day.status);
      const label = `${dateLabel(day.date)} ${info.label}，${esc(day.backupSetCount || 0)} 个备份集`;
      return `<td class="matrix-cell ${info.className}" title="${esc(label)}" aria-label="${esc(label)}"><span class="cell-code">${info.code}</span><span class="sr-only">${esc(info.label)}</span></td>`;
    }).join('')}
  </tr>`).join('');

  const accessibleRows = rows.map(row => `<tr>
    <th scope="row"><a href="#/tasks/${esc(row.taskId)}">${esc(row.taskName)}</a></th>
    ${(row.days || []).map(day => `<td>${esc(dayStatus(day.status).label)}${day.backupSetCount ? `（${esc(day.backupSetCount)} 个）` : ''}</td>`).join('')}
  </tr>`).join('');

  return `<div class="matrix-scroll" role="region" aria-label="最近 14 天备份任务状态矩阵" tabindex="0">
    <table class="matrix-table"><caption class="sr-only">任务 × 日期备份执行状态，符号和文字同时表达状态</caption><thead><tr><th class="task-head" scope="col">任务 / 客户端</th>${head}</tr></thead><tbody>${heatRows}</tbody></table>
  </div>
  <div class="matrix-legend" aria-label="状态图例">
    <span class="success"><i aria-hidden="true"></i>成功</span><span class="failed"><i aria-hidden="true"></i>失败</span><span class="in-progress"><i aria-hidden="true"></i>进行中</span><span class="no-schedule"><i aria-hidden="true"></i>当日无计划</span>
  </div>
  <details class="matrix-a11y"><summary>以文字表格查看（无障碍替代）</summary>
    <div class="matrix-scroll"><table><caption class="sr-only">任务 × 日期状态文字表格</caption><thead><tr><th scope="col">任务</th>${head}</tr></thead><tbody>${accessibleRows}</tbody></table></div>
  </details>`;
}

function renderTrend(dailyUploads) {
  const data = dailyUploads || [];
  if (!data.length) return '<div class="empty">最近 14 天没有入库记录</div>';
  const max = Math.max(1, ...data.map(item => Number(item.count) || 0));
  const points = data.map((item, index) => {
    const x = data.length === 1 ? 50 : (index / (data.length - 1)) * 100;
    const y = 64 - ((Number(item.count) || 0) / max) * 56;
    return `${x},${y}`;
  }).join(' ');
  return `<svg class="trend-svg" viewBox="0 0 100 70" preserveAspectRatio="none" role="img" aria-label="最近 14 天入库量趋势"><line x1="0" y1="64" x2="100" y2="64"></line><polyline points="${points}"></polyline></svg>
    <div class="matrix-legend"><span>最近 ${esc(data.length)} 天</span><span>最高 ${esc(max)} 个备份集 / 日</span></div>`;
}

function renderCapacity(capacity, totalBytes, dailyUploads) {
  const cap = capacity || {};
  const total = Number(cap.diskTotalBytes) || 0;
  const free = Number(cap.diskFreeBytes) || 0;
  const repository = Number(cap.repositoryBytes ?? totalBytes) || 0;
  const usedPercent = total ? Math.min(100, repository / total * 100) : 0;
  const diskText = total ? `磁盘可用 ${fmtBytes(free)} / ${fmtBytes(total)}` : '磁盘容量暂不可用';
  const fullText = cap.estimatedFullAt ? `按近 14 天写入速度预计 ${fmtDT(cap.estimatedFullAt)} 写满` : '写满时间需要更多历史写入数据';
  return `<div class="capacity-card">
    <div class="capacity-stat"><strong>${fmtBytes(repository)}</strong><span>已入库仓库数据<br>${esc(diskText)}</span></div>
    <div class="capacity-meter" aria-label="仓库数据占磁盘容量 ${usedPercent.toFixed(1)}%"><span style="width:${usedPercent.toFixed(1)}%"></span></div>
    <div class="text-muted">${esc(fullText)}</div>
    <div class="dashboard-section-head"><h2>入库趋势</h2><small>最近 14 天</small></div>
    ${renderTrend(dailyUploads)}
  </div>`;
}

/* ── 在线客户端资源 ──
   "现在哪台机器吃紧"是运维每天都要回答的问题，此前只能一台台点进详情去看。
   排序不按名字：有告警的、内存最紧张的排最前，否则真正要看的那台会淹没在列表中间。 */
function renderClientResources(rows) {
  if (!rows || !rows.length)
    return emptyState('first', { title: '还没有客户端上报指标', sub: '客户端完成审批并开始心跳后，这里会显示实时资源占用' });

  const pct = v => (v == null ? '—' : `${Number(v).toFixed(0)}%`);
  // 阈值与服务端告警默认值对齐（CPU 85 / 内存 90 / 源盘可用 10），
  // 免得界面标红的和真正触发告警的不是同一批机器。
  const warn = (v, limit) => (v != null && Number(v) >= limit ? ' class="cell-warn"' : '');

  return tableHtml(
    [
      { l: '客户端', render: r => `<a href="#/clients/${esc(r.id)}"><b>${esc(clientName(r))}</b></a><span class="sub mono">${esc(r.hostname)}</span>` },
      { l: '状态', render: r => status('client_status', r.status) },
      { l: 'CPU', render: r => `<span${warn(r.cpuPercent, 85)}>${pct(r.cpuPercent)}</span>` },
      { l: '内存', render: r => `<span${warn(r.memoryPercent, 90)}>${pct(r.memoryPercent)}</span>`
          + (r.memoryTotalBytes ? `<span class="sub">${fmtBytes(r.memoryTotalBytes - (r.memoryAvailableBytes || 0))} / ${fmtBytes(r.memoryTotalBytes)}</span>` : '') },
      { l: 'Agent 内存', render: r => (r.agentMemoryBytes ? fmtBytes(r.agentMemoryBytes) : '—') },
      { l: '源盘可用', render: r => r.minSourceDiskFreePercent == null
          ? '<span class="sub">无源盘</span>'
          : `<span${r.minSourceDiskFreePercent <= 10 ? ' class="cell-warn"' : ''}>${pct(r.minSourceDiskFreePercent)}</span><span class="sub mono">${esc(r.minSourceDiskName || '')}</span>` },
      { l: '告警', render: r => (r.activeAlertCount ? `<a href="#/alerts" class="cell-warn">${esc(r.activeAlertCount)}</a>` : '—') },
      { l: '最近心跳', render: r => (r.lastHeartbeatAt ? relTime(r.lastHeartbeatAt) : '—') }
    ],
    rows);
}

/* ── 正在传输 ──
   备份在跑的时候，概览页是人第一眼看的地方。此前这里没有任何一个数字会动，
   一份几十 GB 的备份传两小时，概览上和什么都没发生完全一样。
   只放摘要和最该盯的三条，完整列表在「传输中」页。 */
function renderTransfers(rows) {
  if (!rows || !rows.length) return '';
  const total = rows.reduce((n, r) => n + Number(r.totalBytes || 0), 0);
  const done = rows.reduce((n, r) => n + Number(r.uploadedBytes || 0), 0);
  const rated = rows.filter(r => r.bytesPerSecond != null);
  const rate = rated.reduce((n, r) => n + Number(r.bytesPerSecond), 0);
  const stalled = rows.filter(r => r.stalled).length;
  // 服务端已按「卡住的优先、剩余量大的优先」排好，直接取前三条即可。
  const top = rows.slice(0, 3);
  return `<section class="card"><div class="dashboard-section-head">
      <h2>正在传输</h2><a href="#/transfers">查看全部 →</a>
    </div>
    <div class="text-muted">${esc(rows.length)} 条 · ${fmtBytes(done)} / ${fmtBytes(total)} · 合计 ${rated.length ? fmtRate(rate) : '速度计算中'}${stalled ? ` · <span class="cell-warn">${esc(stalled)} 条疑似卡住</span>` : ''}</div>
    ${tableHtml([
      { l: '客户端', render: r => `<a href="#/clients/${esc(r.clientId)}">${esc(r.clientDisplayName || r.hostname)}</a>` },
      { l: '任务', k: 'taskName' },
      { l: '进度', render: r => `${xferBar(r.percent, r.stalled)}<span class="sub">${Number(r.percent).toFixed(1)}% · ${fmtBytes(r.uploadedBytes)} / ${fmtBytes(r.totalBytes)}</span>` },
      { l: '速度', num: true, render: r => fmtRate(r.bytesPerSecond) },
      { l: '预计剩余', num: true, render: r => (r.etaSeconds == null ? '<span class="sub">—</span>' : esc(fmtDuration(r.etaSeconds))) }
    ], top)}</section>`;
}

export async function vDashboard() {
  $('#app').innerHTML = shell('overview', '概览', loading());
  try {
    // alert-summary 与 restore-requests?status=ready 此前只是为了在浏览器里凑待办计数，
    // 待办改读服务端聚合后这两次往返都不再需要，首屏少两次请求。
    const [clientSummary, backupSummary, taskSummary, todoSummary] = await Promise.all([
      api('/api/v1/admin/reports/client-summary'),
      api('/api/v1/admin/reports/backup-summary'),
      api('/api/v1/admin/reports/task-summary'),
      api('/api/v1/admin/reports/todo-summary')
    ]);
    // 资源列表失败不该把整个概览拖垮——它是附加视图，不是概览的前提。
    const clientResources = await api('/api/v1/admin/clients/resource-overview?limit=50').catch(() => []);
    const transfers = (await api('/api/v1/admin/upload-sessions/active').catch(() => [])) || [];
    // 横幅上的「N 项需要关注」与待办页横幅同源，两边不会再各报各的数。
    const todoCount = todoTotal(todoSummary);
    const online = byValue(clientSummary.byStatus, 'online');
    const offline = byValue(clientSummary.byStatus, 'offline') + byValue(clientSummary.byStatus, 'suspected_offline');
    const lastUpload = taskSummary.lastSuccessfulUploadAt ? `最近入库 ${fmtDT(taskSummary.lastSuccessfulUploadAt)}` : '尚无成功入库记录';

    $('#view').innerHTML = `<div class="dashboard-statusbar${todoCount ? ' has-work' : ''}" role="status">
      <span class="status-mark" aria-hidden="true">${todoCount ? '!' : '✓'}</span><strong class="status-title">${todoCount ? `${todoCount} 项需要关注` : '一切正常'}</strong>
      <span class="status-meta"><span>${esc(online)} 台在线${offline ? ` · ${esc(offline)} 台离线或疑似离线` : ''}</span><span>${esc(lastUpload)}</span><span>${esc(taskSummary.totalTasks || 0)} 个备份任务</span></span>
    </div>
    ${renderTransfers(transfers)}
    <div class="dashboard-columns">
      <section class="card"><div class="dashboard-section-head"><h2>待办队列</h2><a href="#/todo">查看全部 →</a></div><div class="dashboard-todo">${renderTodoQueue(todoSummary)}</div></section>
      <section class="card">${renderCapacity(taskSummary.capacity, backupSummary.totalBytes, backupSummary.dailyUploads)}</section>
    </div>
    <section class="card"><div class="dashboard-section-head"><h2>客户端资源</h2><small>最近一次心跳 · 告警与内存吃紧的排前</small></div>${renderClientResources(clientResources)}</section>
    <section class="card"><div class="dashboard-section-head"><h2>备份时序</h2><small>最近 14 天 · 任务按异常优先</small></div>${renderMatrix(taskSummary)}</section>`;
  } catch (e) {
    $('#view').innerHTML = `<div class="empty"><span class="e-glyph" aria-hidden="true">!</span><div class="e-title">概览加载失败</div><div class="e-sub">${esc(e.message)}</div></div>`;
  }
}
