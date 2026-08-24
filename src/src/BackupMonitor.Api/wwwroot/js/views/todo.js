/* js/views/todo.js —— 待办聚合：把需要人做决定的事项放到一个入口 */
import { api } from '../api.js';
import { App, LOADERS } from '../state.js';
import { $, esc, status, relTime, emptyState, toast } from '../ui.js';
import { shell, loading } from '../app.js';

const list = data => data?.items || [];
const ageHours = value => value ? (Date.now() - new Date(value).getTime()) / 3600000 : Infinity;

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
  const errors = [];
  const read = async (path, fallback) => { try { return await api(path); } catch (e) { errors.push(e); return fallback; } };
  const [pendingClients, tasksData, readyRestores, criticalAlerts, recentAutoData] = await Promise.all([
    read('/api/v1/admin/clients?status=pending_approval&page=1&pageSize=100', { items: [] }),
    read('/api/v1/admin/backup-tasks?page=1&pageSize=200', { items: [] }),
    read('/api/v1/admin/restore-requests?status=ready&page=1&pageSize=100', { items: [] }),
    read('/api/v1/admin/alerts?level=critical&status=open&page=1&pageSize=100', { items: [] }),
    read('/api/v1/admin/clients/recent-auto-enrollments?days=7&limit=20', [])
  ]);
  const clients = list(pendingClients);
  const restores = list(readyRestores);
  const alerts = list(criticalAlerts);
  const recentAutoEnrollments = Array.isArray(recentAutoData) ? recentAutoData : [];
  const failedTasks = list(tasksData).filter(t => t.enabled && (
    ['failed', 'failure', 'error'].includes(String(t.lastPrecheckStatus || '').toLowerCase()) ||
    (t.lastScanAt && ageHours(t.lastScanAt) > 24 && ageHours(t.lastSuccessAt) > 72)
  ));
  // P2-12：recentAutoEnrollments 会被渲染进列表，却没算进计数，
  // 于是顶部横幅显示「没有待处理事项」，下方却列着若干条。
  const count = clients.length + failedTasks.length + restores.length + alerts.length + recentAutoEnrollments.length;
  const checkedAt = new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
  const parts = [];
    if (clients.length) parts.push(todoItem('○', 'wait', `${clients.length} 台客户端等待审批`, '注册请求已经进入管理端，审批后才会签发 mTLS 证书。', '去审批', { kind: 'navigate', hash: '#/clients' }));
  for (const client of recentAutoEnrollments) {
        parts.push(todoItem('○', 'info', `最近自动登记：${esc(client.hostname)}`, `${esc(client.displayName || '')} · 登记：${relTime(client.createdAt)} · 最近心跳：${relTime(client.lastHeartbeatAt)}`, '查看', { kind: 'navigate', hash: `#/clients/${client.id}` }));
  }
  for (const task of failedTasks.slice(0, 20)) {
        const reason = task.lastPrecheckStatus ? `最近预检：${esc(task.lastPrecheckStatus)}` : `最近成功：${relTime(task.lastSuccessAt)}`;
        parts.push(todoItem('▲', 'danger', `任务「${esc(task.name)}」需要检查`, `${esc(task.clientHostname || '')} · ${reason}`, '查看', { kind: 'navigate', hash: `#/tasks/${task.id}` }));
  }
  for (const restore of restores.slice(0, 20)) {
        parts.push(todoItem('○', 'wait', `恢复请求 ${esc(restore.backupSetCode || String(restore.id).slice(0, 8))} 等待签发`, `请求人：${esc(restore.requestedByName || '—')} · ${relTime(restore.requestedAt)}`, '签发', { kind: 'act', view: 'restores', name: 'token', id: restore.id, afterLoader: 'todo' }));
  }
  if (alerts.length) {
        parts.push(todoItem('▲', 'danger', `${alerts.length} 条严重告警未确认`, '严重告警需要先确认，再进入处理流程。', '去确认', { kind: 'navigate', hash: '#/alerts' }));
  }
    const note = errors.length ? '<div class="notice warning">部分事项因当前账号权限不可见；已显示可读取范围内的待办。</div>' : '';
    wrap.innerHTML = `<div class="todo-toolbar"><div><h2>待办</h2><p class="text-muted">把等待决定的事项集中在这里，处理后会从队列移除。</p></div><button data-ui-action="loader" data-loader="todo">重新检查</button></div>${note}<div class="todo-summary ${count ? 'has-work' : 'is-clear'}"><span class="todo-summary-icon" aria-hidden="true">${count ? '!' : '✓'}</span><strong>${count ? `${count} 项需要处理` : '没有待处理事项'}</strong><span>最后检查 ${checkedAt}</span></div><section class="todo-list" aria-label="待办事项">${parts.join('') || emptyState('ok', { title: '没有待处理事项', sub: `最后检查 ${checkedAt}` })}</section>`;
};





