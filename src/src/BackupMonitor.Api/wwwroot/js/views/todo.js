/* js/views/todo.js —— 待办聚合：把需要人做决定的事项放到一个入口
   B5 中期方案：改用服务端聚合端点 GET /api/v1/admin/reports/todo-summary。
   此前拉 backup-tasks?pageSize=200 再在浏览器里过滤，PagedQuery.MaxPageSize=200
   是硬上限——超过 200 个任务时待办会静默漏掉后面的。服务端聚合用 CountAsync 得出计数，
   不受分页影响；"哪些任务算有问题"的判定也一并搬到服务端（BackupTaskService.OkPrecheckStatuses），
   不再有浏览器/服务端两份规则。 */
import { api } from '../api.js';
import { ACTIONS, LOADERS } from '../state.js';
import { $, esc, L, relTime, emptyState, confirmModal, toast } from '../ui.js';
import { shell, loading } from '../app.js';

const items = section => section?.items || [];
const count = section => section?.count ?? 0;

function todoItem(icon, type, title, detail, actionText, action) {
  const attrs = action.kind === 'navigate'
    ? `data-ui-action="navigate" data-hash="${esc(action.hash)}"`
    : `data-ui-action="act" data-view="${esc(action.view)}" data-action="${esc(action.name)}" data-id="${esc(action.id)}" data-after-loader="${esc(action.afterLoader || '')}"`;
  return `<div class="todo-item"><span class="todo-icon" aria-hidden="true">${icon}</span><div class="todo-main"><div class="todo-title">${title}</div><div class="todo-detail">${detail}</div></div><button class="small ${type === 'danger' ? 'danger' : 'primary'}" ${attrs}>${esc(actionText)}</button></div>`;
}

export async function vTodo() {
    $('#app').innerHTML = shell('todo', '待办', `<div id="todoWrap">${loading()}</div>`);
  await LOADERS.todo();
}

LOADERS.todo = async function () {
  const wrap = $('#todoWrap'); if (!wrap) return;
  let summary;
  try {
    summary = await api('/api/v1/admin/reports/todo-summary');
  } catch (e) {
    wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
    return;
  }

  const clients = items(summary.pendingApprovalClients);
  const clientsCount = count(summary.pendingApprovalClients);
  const problematicTasks = items(summary.problematicTasks);
  const restores = items(summary.readyRestores);
  const alerts = items(summary.criticalAlerts);
  const alertsCount = count(summary.criticalAlerts);
  const recentAutoEnrollments = items(summary.recentAutoEnrollments);

  // 各分组计数一律用服务端 count（不受 preview items 截断影响），
  // 之前 recentAutoEnrollments 渲染进列表却没算进总数导致横幅显示矛盾（P2-12）的问题
  // 服务端聚合天然不会再发生：每一段都是"count + 同一批 items"。
  const totalCount = clientsCount + count(summary.problematicTasks)
    + count(summary.readyRestores) + alertsCount + count(summary.recentAutoEnrollments);
  const checkedAt = new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
  const parts = [];
  // 待办页的价值在于「在这里做完」：等待审批的机器逐台列出来就地审批，
  // 而不是聚合成一行再把人踢回客户端列表自己找一遍。
  for (const client of clients) {
        parts.push(todoItem('○', 'wait', `客户端「${esc(client.hostname)}」等待审批`, `${esc(client.displayName || '—')} · 注册：${relTime(client.createdAt)} · 审批后才会签发 mTLS 证书`, '审批', { kind: 'act', view: 'clients', name: 'approve', id: client.id, afterLoader: 'todo' }));
  }
  if (clientsCount > clients.length) {
        parts.push(todoItem('○', 'wait', `还有 ${clientsCount - clients.length} 台客户端等待审批`, '这里只显示最近的一批，其余请到客户端列表查看。', '查看全部', { kind: 'navigate', hash: '#/clients' }));
  }
  for (const client of recentAutoEnrollments) {
        parts.push(todoItem('○', 'info', `最近自动登记：${esc(client.hostname)}`, `${esc(client.displayName || '')} · 登记：${relTime(client.createdAt)} · 最近心跳：${relTime(client.lastHeartbeatAt)}`, '查看', { kind: 'navigate', hash: `#/clients/${client.id}` }));
  }
  for (const task of problematicTasks) {
        const reason = task.lastPrecheckStatus ? `上次检查：${esc(L.precheck[task.lastPrecheckStatus] || task.lastPrecheckStatus)}` : `最近成功：${relTime(task.lastSuccessAt)}`;
        parts.push(todoItem('▲', 'danger', `任务「${esc(task.name)}」需要检查`, `${esc(task.clientHostname || '')} · ${reason}`, '查看', { kind: 'navigate', hash: `#/tasks/${task.id}` }));
  }
  if (count(summary.problematicTasks) > problematicTasks.length) {
        parts.push(todoItem('▲', 'danger', `还有 ${count(summary.problematicTasks) - problematicTasks.length} 个任务需要检查`, '这里只显示最近的一批，其余请到任务列表查看。', '查看全部', { kind: 'navigate', hash: '#/tasks' }));
  }
  for (const restore of restores) {
        parts.push(todoItem('○', 'wait', `恢复请求 ${esc(restore.backupSetCode || String(restore.id).slice(0, 8))} 等待签发`, `请求人：${esc(restore.requestedByName || '—')} · ${relTime(restore.requestedAt)}`, '签发', { kind: 'act', view: 'restores', name: 'token', id: restore.id, afterLoader: 'todo' }));
  }
  // 告警同理：确认这个动作本身不需要看详情，就地做完让它从队列里消失；
  // 需要进一步处理（指派 / 静默 / 关闭）的再去告警页。
  for (const alert of alerts) {
        parts.push(todoItem('▲', 'danger', `严重告警：${esc(alert.title)}`, `${esc(alert.clientHostname || '—')} · ${relTime(alert.lastOccurredAt)} · 已发生 ${esc(alert.occurrenceCount)} 次`, '确认', { kind: 'act', view: 'alerts', name: 'ack', id: alert.id, afterLoader: 'todo' }));
  }
  if (alertsCount > alerts.length) {
        parts.push(todoItem('▲', 'danger', `还有 ${alertsCount - alerts.length} 条严重告警未确认`, '这里只显示最近的一批，其余请到告警页查看。', '查看全部', { kind: 'navigate', hash: '#/alerts' }));
  }
  // 局域网里一批机器同时上线是常态，逐台点「审批」很快就会让人放弃用待办页。
  const batchApprove = clients.length > 1
    ? `<button class="primary" data-ui-action="act" data-view="todo" data-action="approve-all" data-id="${esc(clients.map(c => c.id).join(','))}" data-after-loader="todo">全部审批（${clients.length} 台）</button>`
    : '';
    wrap.innerHTML = `<div class="todo-toolbar"><div><h2>待办</h2><p class="text-muted">把等待决定的事项集中在这里，处理后会从队列移除。</p></div><div style="display:flex;gap:8px">${batchApprove}<button data-ui-action="loader" data-loader="todo">重新检查</button></div></div><div class="todo-summary ${totalCount ? 'has-work' : 'is-clear'}"><span class="todo-summary-icon" aria-hidden="true">${totalCount ? '!' : '✓'}</span><strong>${totalCount ? `${totalCount} 项需要处理` : '没有待处理事项'}</strong><span>最后检查 ${checkedAt}</span></div><section class="todo-list" aria-label="待办事项">${parts.join('') || emptyState('ok', { title: '没有待处理事项', sub: `最后检查 ${checkedAt}` })}</section>`;
};

/* 批量审批只处理当前列出的这一批（服务端预览上限 20 条），处理完刷新后下一批自然补位。
   不在浏览器里另拉一遍分页客户端列表——那正是待办改走服务端聚合要甩掉的东西：
   界面看到的和服务端认定的不能是两份口径。
   这里直接调审批接口而不复用 clients:approve，是因为后者每台都要弹一次确认框，
   批量的意义就是只确认一次。 */
ACTIONS['todo:approve-all'] = async idList => {
  const ids = String(idList || '').split(',').filter(Boolean);
  if (!ids.length) return;
  if (!await confirmModal(`审批通过这 ${ids.length} 台客户端并为其签发 mTLS 证书？`)) return;
  // 一台失败（比如注册请求已被别人处理掉）不该中断整批，逐台记账最后一次性汇报。
  let ok = 0, fail = 0;
  for (const id of ids) {
    try { await api(`/api/v1/admin/clients/${id}/approve`, { method: 'POST' }); ok++; }
    catch (e) { fail++; }
  }
  toast(`已审批 ${ok} 台${fail ? `，失败 ${fail} 台` : ''}`, fail ? 'err' : 'ok');
};
