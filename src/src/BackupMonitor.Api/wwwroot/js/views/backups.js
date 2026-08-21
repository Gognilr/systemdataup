/* js/views/backups.js —— 备份集列表 / 详情 / 锁定 / 校验 / 发起恢复 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtBytes, fmtDT, relTime,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter,
  toast, errToast, confirmModal, formModal
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vBackups() {
  App.state.backups = App.state.backups || { page: 1, pageSize: 20, status: '', keyword: '', totalCount: 0 };
  const st = App.state.backups;
  $('#app').innerHTML = shell('backups', '备份集', `
    <div class="toolbar">
      <select id="f_status"><option value="">全部状态</option>${optsOf(L.backup_set_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <input id="f_kw" placeholder="关键字" value="${esc(st.keyword)}">
      <button class="primary" data-ui-action="loader" data-loader="backups">查询</button>
    </div>
    <div id="backup-summary-chips" class="filter-chips" aria-label="备份集状态分布"></div>
    <div id="vwrap">${loading()}</div>`);
  $('#f_status').onchange = () => { st.status = $('#f_status').value; st.page = 1; LOADERS.backups(); };
  $('#f_kw').onkeydown = ev => { if (ev.key === 'Enter') { st.keyword = ev.target.value.trim(); st.page = 1; LOADERS.backups(); } };
  api('/api/v1/admin/reports/backup-summary').then(summary => {
    const wrap = $('#backup-summary-chips');
    if (!wrap) return;
    wrap.innerHTML = (summary.byStatus || []).map(item => `<button class="filter-chip${st.status === item.value ? ' active' : ''}" data-ui-action="filter" data-filter-key="backups" data-filter-field="status" data-filter-value="${esc(item.value)}">${esc(L.backup_set_status[item.value] || item.value)} <strong>${esc(item.count)}</strong></button>`).join('');
  }).catch(() => {});
  await LOADERS.backups();
}

LOADERS.backups = async function () {
  const st = App.state.backups;
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.status) q.set('status', st.status);
    if (st.keyword) q.set('keyword', st.keyword);
    const data = await api('/api/v1/admin/backups?' + q);
    st.totalCount = data.totalCount;
    const empty = !hasFilter(st, ['status', 'keyword'])
      ? emptyState('first', { glyph: '❏', title: '暂无备份集', sub: '备份任务成功入库后，备份集会出现在这里' })
      : emptyState('filter', { key: 'backups', title: `没有匹配「${esc(st.keyword || L.backup_set_status[st.status] || '')}」的备份集` });
    wrap.innerHTML = tableHtml([
      { l: '备份集编号', render: r => `<a href="#/backups/${esc(r.id)}"><b class="mono">${esc(r.backupSetCode)}</b></a>` },
      { l: '客户端 / 任务', render: r => `${esc(r.clientHostname)}<span class="sub">${esc(r.taskName)}</span>` },
      { l: '应用', k: 'applicationName' },
      { l: '状态', render: r => status('backup_set_status', r.status) },
      { l: '业务时间', render: r => relTime(r.backupBusinessTime) },
      { l: '大小', num: true, render: r => `${esc(r.totalFiles)} 个文件 / ${fmtBytes(r.totalBytes)}` },
      { l: '锁定', render: r => r.locked ? '<span class="status status--wait pill">已锁定</span>' : '—' },
      { l: '保留至', render: r => relTime(r.retentionUntil) },
      { l: '操作', render: r => {
        const b = [];
        if (r.status === 'available') {
          b.push(`<button class="small primary" data-ui-action="act" data-view="backups" data-action="restore" data-id="${esc(r.id)}">恢复</button>`);
          b.push(`<button class="small" data-ui-action="act" data-view="backups" data-action="verify" data-id="${esc(r.id)}">重新校验</button>`);
        }
        if (r.locked) b.push(`<button class="small" data-ui-action="act" data-view="backups" data-action="unlock" data-id="${esc(r.id)}">解锁</button>`);
        else if (r.status === 'available') b.push(`<button class="small" data-ui-action="act" data-view="backups" data-action="lock" data-id="${esc(r.id)}">锁定</button>`);
        return b.join(' ') || '—';
      } }
    ], data.items, { empty }) + pagerHtml('backups', st);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

ACTIONS['backups:lock'] = async id => {
  formModal('锁定备份集', [
    { name: 'reason', label: '锁定原因', type: 'text', required: true },
    { name: 'expiresAt', label: '锁到期时间（留空为永久）', type: 'datetime-local' }
  ], async v => {
    await api(`/api/v1/admin/backups/${id}/lock`, { method: 'POST',
      body: { reason: v.reason, expiresAt: v.expiresAt ? new Date(v.expiresAt).toISOString() : null } });
    toast('备份集已锁定', 'ok'); LOADERS.backups();
  }, '锁定');
};
ACTIONS['backups:unlock'] = async id => {
  if (!await confirmModal('解除该备份集的锁定？')) return;
  await api(`/api/v1/admin/backups/${id}/unlock`, { method: 'POST' });
  toast('已解锁', 'ok'); LOADERS.backups();
};
ACTIONS['backups:verify'] = async id => {
  if (!await confirmModal('对该备份集发起重新校验（异步 SHA-256 复核）？')) return;
  const d = await api(`/api/v1/admin/backups/${id}/verify`, { method: 'POST' });
  toast(`校验已开始（操作 ${d.operationId ? String(d.operationId).slice(0, 8) : ''}）`, 'ok'); LOADERS.backups();
};
ACTIONS['backups:restore'] = async id => createRestore(id);
function createRestore(backupSetId) {
  formModal('创建恢复请求', [
    { name: 'purpose', label: '恢复用途', type: 'textarea', required: true, placeholder: '说明恢复原因，将记入审计日志' }
  ], async v => {
    const d = await api('/api/v1/admin/restore-requests', { method: 'POST',
      headers: { 'Idempotency-Key': crypto.randomUUID() },
      body: { backupSetId, purpose: v.purpose, selection: { type: 'full' } } });
    toast(`恢复请求已创建（${L.restore_status[d.status] || d.status}），校验通过后可签发下载令牌`, 'ok');
    location.hash = '#/restores';
  }, '创建');
}

export async function vBackupDetail(id) {
  $('#app').innerHTML = shell('backups', '备份集详情', loading());
  try {
    const d = await api(`/api/v1/admin/backups/${id}`);
    $('#view').innerHTML = `
    <div class="toolbar"><a href="#/backups">← 返回备份集列表</a><div class="spacer"></div>
      ${d.status === 'available' ? `<button class="primary" data-ui-action="act" data-view="backups" data-action="restore" data-id="${esc(d.id)}">发起恢复</button>
      <button data-ui-action="act" data-view="backups" data-action="verify" data-id="${esc(d.id)}">重新校验</button>` : ''}
      ${d.locked ? `<button data-ui-action="act" data-view="backups" data-action="unlock" data-id="${esc(d.id)}">解锁</button>`
        : (d.status === 'available' ? `<button data-ui-action="act" data-view="backups" data-action="lock" data-id="${esc(d.id)}">锁定</button>` : '')}
    </div>
    <div class="card"><b class="mono">${esc(d.backupSetCode)}</b> ${status('backup_set_status', d.status)}
      ${d.locked ? '<span class="status status--wait pill">已锁定</span>' : ''}
      <div class="kv" style="margin-top:12px">
        <div class="row"><div class="k">客户端 / 任务</div><div class="v">${esc(d.clientHostname)} / ${esc(d.taskName)}</div></div>
        <div class="row"><div class="k">应用</div><div class="v">${esc(d.applicationName)}</div></div>
        <div class="row"><div class="k">业务时间</div><div class="v">${fmtDT(d.backupBusinessTime)}</div></div>
        <div class="row"><div class="k">上传时间</div><div class="v">${fmtDT(d.uploadedAt)}</div></div>
        <div class="row"><div class="k">规模</div><div class="v">${esc(d.totalFiles)} 个文件 · ${fmtBytes(d.totalBytes)}</div></div>
        <div class="row"><div class="k">校验时间</div><div class="v">${fmtDT(d.verifiedAt)}</div></div>
        <div class="row"><div class="k">仓库路径</div><div class="v mono">${esc(d.repositoryPath || '—')}</div></div>
        <div class="row"><div class="k">清单 SHA-256</div><div class="v mono">${esc(d.manifestSha256 || '—')}</div></div>
        <div class="row"><div class="k">保留至</div><div class="v">${fmtDT(d.retentionUntil)}</div></div>
      </div></div>
    ${d.retentionLocks.length ? `<div class="card"><b>保留锁</b>${tableHtml([
      { l: '原因', k: 'reason' }, { l: '锁定人', k: 'lockedByName' }, { l: '锁定时间', render: r => fmtDT(r.lockedAt) },
      { l: '到期', render: r => fmtDT(r.expiresAt) }, { l: '有效', render: r => r.active ? '是' : '否' }
    ], d.retentionLocks)}</div>` : ''}
    <div class="card"><b>文件明细（${esc(d.files.length)}）</b>${tableHtml([
      { l: '相对路径', render: r => `<span class="mono" style="font-size:12px">${esc(r.relativePath)}</span>` },
      { l: '大小', num: true, render: r => fmtBytes(r.sizeBytes) }, { l: '修改时间', render: r => fmtDT(r.lastModifiedAt) },
      { l: 'SHA-256', render: r => `<span class="mono" style="font-size:11px">${esc((r.sha256 || '').slice(0, 16))}…</span>` },
      { l: '校验', render: r => status('result', r.verificationStatus) }
    ], d.files, { empty: emptyState('first', { title: '无文件记录' }) })}</div>`;
  } catch (e) { $('#view').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
}

