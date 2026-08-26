/* js/views/todo.js —— 待办聚合：把需要人做决定的事项放到一个入口
   B5 中期方案：改用服务端聚合端点 GET /api/v1/admin/reports/todo-summary。
   此前拉 backup-tasks?pageSize=200 再在浏览器里过滤，PagedQuery.MaxPageSize=200
   是硬上限——超过 200 个任务时待办会静默漏掉后面的。服务端聚合用 CountAsync 得出计数，
   不受分页影响；"哪些任务算有问题"的判定也一并搬到服务端（BackupTaskService.OkPrecheckStatuses），
   不再有浏览器/服务端两份规则。 */
import { api } from '../api.js';
import { LOADERS } from '../state.js';
import { $, esc, L, relTime, emptyState } from '../ui.js';
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
  const problematicTasks = items(summary.problematicTasks);
  const restores = items(summary.readyRestores);
  const alertsCount = count(summary.criticalAlerts);
  const recentAutoEnrollments = items(summary.recentAutoEnrollments);

  // 各分组计数一律用服务端 count（不受 preview items 截断影响），
  // 之前 recentAutoEnrollments 渲染进列表却没算进总数导致横幅显示矛盾（P2-12）的问题
  // 服务端聚合天然不会再发生：每一段都是"count + 同一批 items"。
  const totalCount = count(summary.pendingApprovalClients) + count(summary.problematicTasks)
    + count(summary.readyRestores) + alertsCount + count(summary.recentAutoEnrollments);
  const checkedAt = new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
  const parts = [];
  if (clients.length) parts.push(todoItem('○', 'wait', `${count(summary.pendingApprovalClients)} 台客户端等待审批`, '注册请求已经进入管理端，审批后才会签发 mTLS 证书。', '去审批', { kind: 'navigate', hash: '#/clients' }));
  for (const client of recentAutoEnrollments) {
        parts.push(todoItem('○', 'info', `最近自动登记：${esc(client.hostname)}`, `${esc(client.displayName || '')} · 登记：${relTime(client.createdAt)} · 最近心跳：${relTime(client.lastHeartbeatAt)}`, '查看', { kind: 'navigate', hash: `#/clients/${client.id}` }));
  }
  for (const task of problematicTasks) {
        const reason = task.lastPrecheckStatus ? `最近预检：${esc(L.precheck[task.lastPrecheckStatus] || task.lastPrecheckStatus)}` : `最近成功：${relTime(task.lastSuccessAt)}`;
        parts.push(todoItem('▲', 'danger', `任务「${esc(task.name)}」需要检查`, `${esc(task.clientHostname || '')} · ${reason}`, '查看', { kind: 'navigate', hash: `#/tasks/${task.id}` }));
  }
  if (count(summary.problematicTasks) > problematicTasks.length) {
        parts.push(todoItem('▲', 'danger', `还有 ${count(summary.problematicTasks) - problematicTasks.length} 个任务需要检查`, '这里只显示最近的一批，其余请到任务列表查看。', '查看全部', { kind: 'navigate', hash: '#/tasks' }));
  }
  for (const restore of restores) {
        parts.push(todoItem('○', 'wait', `恢复请求 ${esc(restore.backupSetCode || String(restore.id).slice(0, 8))} 等待签发`, `请求人：${esc(restore.requestedByName || '—')} · ${relTime(restore.requestedAt)}`, '签发', { kind: 'act', view: 'restores', name: 'token', id: restore.id, afterLoader: 'todo' }));
  }
  if (alertsCount) {
        parts.push(todoItem('▲', 'danger', `${alertsCount} 条严重告警未确认`, '严重告警需要先确认，再进入处理流程。', '去确认', { kind: 'navigate', hash: '#/alerts' }));
  }
    wrap.innerHTML = `<div class="todo-toolbar"><div><h2>待办</h2><p class="text-muted">把等待决定的事项集中在这里，处理后会从队列移除。</p></div><button data-ui-action="loader" data-loader="todo">重新检查</button></div><div class="todo-summary ${totalCount ? 'has-work' : 'is-clear'}"><span class="todo-summary-icon" aria-hidden="true">${totalCount ? '!' : '✓'}</span><strong>${totalCount ? `${totalCount} 项需要处理` : '没有待处理事项'}</strong><span>最后检查 ${checkedAt}</span></div><section class="todo-list" aria-label="待办事项">${parts.join('') || emptyState('ok', { title: '没有待处理事项', sub: `最后检查 ${checkedAt}` })}</section>`;
};
