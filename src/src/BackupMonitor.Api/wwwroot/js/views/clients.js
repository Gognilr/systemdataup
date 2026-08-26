/* js/views/clients.js —— 客户端列表 / 详情 / 审批类动作 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtBytes, fmtDT, relTime, shortId,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter, batchBarHtml,
  toast, errToast, confirmModal, formModal, openModal, closeModal, clientName,
  clientCaps, actBtn, runtimeBadge
} from '../ui.js';
import { shell, loading } from '../app.js';
import { renderClientRuntimeDetail } from './client-runtime.js';

export async function vClients() {
  App.state.clients = App.state.clients || { page: 1, pageSize: 20, status: '', keyword: '', totalCount: 0, selected: [] , sortKey: 'createdAt', sortDesc: true};
  const st = App.state.clients;
  $('#app').innerHTML = shell('clients', '客户端管理', `
    <div class="toolbar">
      <select id="f_status"><option value="">全部状态</option>${optsOf(L.client_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <input id="f_kw" placeholder="关键字（主机名等）" value="${esc(st.keyword)}" style="min-width:180px">
      <button class="primary" data-ui-action="loader" data-loader="clients">查询</button>
      <div class="spacer"></div>
      <a class="primary button" href="/downloads/BackupMonitor.Agent.Setup.exe" download>下载客户端安装程序</a>
      <button class="button" id="client-advanced-toggle" data-ui-action="method" data-method="toggleClientAdvanced" aria-expanded="false">高级设置</button>
    </div>
    <div id="client-advanced" class="card" hidden>
      <div class="dashboard-section-head"><h2>高级部署</h2><small>仅在 Secure 模式或运维人员使用</small></div>
      <p class="text-muted">局域网一键安装不需要令牌。只有使用 Secure 部署模式时，才需要创建注册令牌或下载兼容包。</p>
      <div class="toolbar compact-toolbar">
        <a class="button" href="#/registration-tokens">管理注册令牌</a>
        <a class="button" href="/downloads/BackupMonitor.Agent.zip" download>下载兼容 ZIP 包</a>
      </div>
    </div>
    <div id="deployment-status-card" class="card" role="status" aria-live="polite">正在读取部署状态…</div>
    <div class="card onboarding-card">
      <div class="dashboard-section-head"><h2>新电脑怎么装</h2><small>局域网自动登记，无需复制令牌</small></div>
      <ol class="onboarding-steps">
        <li><strong>下载</strong><span>点击上方“下载客户端安装程序”，把 EXE 复制到新电脑。</span></li>
        <li><strong>双击</strong><span>双击 <code>BackupMonitor.Agent.Setup.exe</code>，安装器会自动发现局域网服务器。</span></li>
        <li><strong>安装</strong><span>确认服务器后点击“安装”；Agent 服务和托盘会自动启动，客户端会立即上线。</span></li>
      </ol>
    </div>
    <div id="client-summary-chips" class="filter-chips" aria-label="客户端状态分布"></div>
    <div id="bb-clients">${batchBarHtml('clients')}</div>
    <div id="vwrap">${loading()}</div>`);
  loadDeploymentStatus();
  $('#f_status').onchange = () => { st.status = $('#f_status').value; st.page = 1; LOADERS.clients(); };
  $('#f_kw').onkeydown = ev => { if (ev.key === 'Enter') { st.keyword = ev.target.value.trim(); st.page = 1; LOADERS.clients(); } };
  api('/api/v1/admin/reports/client-summary').then(summary => {
    const wrap = $('#client-summary-chips');
    if (!wrap) return;
    wrap.innerHTML = (summary.byStatus || []).map(item => `<button class="filter-chip${st.status === item.value ? ' active' : ''}" data-ui-action="filter" data-filter-key="clients" data-filter-field="status" data-filter-value="${esc(item.value)}">${esc(L.client_status[item.value] || item.value)} <strong>${esc(item.count)}</strong></button>`).join('');
  }).catch(() => {});
  await LOADERS.clients();
}

App.toggleClientAdvanced = function () {
  const panel = $('#client-advanced');
  const button = $('#client-advanced-toggle');
  if (!panel || !button) return;
  panel.hidden = !panel.hidden;
  button.setAttribute('aria-expanded', String(!panel.hidden));
};

async function loadDeploymentStatus() {
  const wrap = $('#deployment-status-card');
  if (!wrap) return;
  try {
    const d = await api('/api/v1/admin/deployment-status');
    const dbOk = d.databaseStatus === 'healthy';
    const autoText = d.automaticEnrollment ? '局域网自动登记已启用' : 'Secure 模式：需要注册令牌';
    const openButton = d.automaticEnrollment
      ? `<button class="button" id="open-enrollment-window">开放登记 30 分钟</button><span class="text-muted">当前窗口：${d.enrollmentOpenUntilUtc ? fmtDT(d.enrollmentOpenUntilUtc) : '已关闭'}</span>`
      : '';
    wrap.innerHTML = `<div class="dashboard-section-head"><h2>部署状态</h2><small>${d.checkedAtUtc ? fmtDT(d.checkedAtUtc) : ''}</small></div>
      <div class="deployment-status-grid">
        <div><span>服务器地址</span><strong class="mono">${esc(d.serverAddress)}</strong></div>
        <div><span>服务状态</span><strong class="status status--ok">运行中</strong></div>
        <div><span>数据库状态</span><strong class="status status--${dbOk ? 'ok' : 'err pill'}">${dbOk ? '正常' : esc(d.databaseStatus || '未知')}</strong></div>
        <div><span>客户端安装包</span><strong>${esc(d.clientInstallerVersion || '未知')}</strong></div>
      </div>
      <p class="text-muted deployment-mode">${esc(autoText)} · 部署模式：${esc(d.deploymentMode || 'Secure')} ${openButton}</p>`;
    const openWindowButton = $('#open-enrollment-window');
    if (openWindowButton) openWindowButton.addEventListener('click', App.openEnrollmentWindow);
  } catch (e) {
    wrap.innerHTML = `<div class="dashboard-section-head"><h2>部署状态</h2><small class="status status--err pill">读取失败</small></div><p class="text-muted">${esc(e.message)}</p>`;
  }
}

App.openEnrollmentWindow = async function () {
  try {
    const result = await api('/api/v1/admin/deployment-status/enrollment-window', { method: 'POST' });
    toast(`自动登记已开放至 ${fmtDT(result.enrollmentOpenUntilUtc)}`, 'ok');
    await loadDeploymentStatus();
  } catch (e) {
    toast(e.message || '无法开放自动登记窗口', 'err');
  }
};

LOADERS.clients = async function () {
  const st = App.state.clients;
  const wrap = $('#vwrap'); if (!wrap) return;
  clearTimeout(App.timer);
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.sortKey) { q.set('sortBy', st.sortKey); q.set('sortDescending', String(st.sortDesc !== false)); }
    if (st.status) q.set('status', st.status);
    if (st.keyword) q.set('keyword', st.keyword);
    const data = await api('/api/v1/admin/clients?' + q);
    st.totalCount = data.totalCount;
    const filtered = hasFilter(st, ['status', 'keyword']);
    const empty = !filtered
      ? emptyState('first', { glyph: '⌗', title: '还没有客户端', sub: '从上方下载客户端安装程序并完成安装后，客户端会自动出现在这里' })
      : emptyState('filter', { key: 'clients', title: `没有匹配「${st.keyword || L.client_status[st.status] || ''}」的客户端` });
    wrap.innerHTML = tableHtml([
      { l: '客户端', k: 'hostname', sort: true, render: r => `<a href="#/clients/${esc(r.id)}"><b>${esc(clientName(r))}</b></a><span class="sub mono">${esc(r.hostname)}</span>` },
      { l: '分组', k: 'clientGroupName' },
      { l: '系统', render: r => esc(r.osName || '—') },
      { l: 'Agent', k: 'agentVersion', sort: true },
      // 「在线」只回答"连得上吗"。真正决定"现在能不能动它"的是它正在干什么，
      // 所以两个状态并排放：连接状态 + 运行状态。
      { l: '状态', k: 'status', sort: true, render: r => status('client_status', r.status) + runtimeBadge(r) },
      { l: '证书剩余', render: r => r.certificateRemainingDays == null ? '—' : `${esc(r.certificateRemainingDays)} 天` },
      { l: '最近心跳', k: 'lastHeartbeatAt', sort: true, render: r => relTime(r.lastHeartbeatAt) },
      { l: '告警', num: true, render: r => r.activeAlertCount ? `<span class="status status--err pill">${esc(r.activeAlertCount)}</span>` : '0' },
      { l: '任务数', num: true, k: 'taskCount' },
      // 按钮开关一律走 clientCaps：离线的机器下发指令只会静默排队到过期，
      // 已禁用/已注销的会被服务端 409，正在上传的机器不该在这时候被禁用或注销。
      { l: '操作', render: r => {
        const caps = clientCaps(r);
        if (caps.canApprove) {
          return [
            actBtn({ label: '审批', view: 'clients', action: 'approve', id: r.id, cls: 'primary' }),
            actBtn({ label: '拒绝', view: 'clients', action: 'reject', id: r.id, cls: 'danger' })
          ].join(' ');
        }
        const b = [actBtn({
          label: '刷新指标', view: 'clients', action: 'metrics', id: r.id,
          allowed: caps.canDispatch, why: caps.whyDispatch, hint: caps.whyDispatch
        })];
        b.push(caps.canEnable
          ? actBtn({ label: '启用', view: 'clients', action: 'enable', id: r.id, cls: 'primary' })
          : actBtn({ label: '禁用', view: 'clients', action: 'disable', id: r.id, allowed: caps.canDisable, why: caps.whyDisable }));
        b.push(actBtn({ label: '注销', view: 'clients', action: 'revoke', id: r.id, cls: 'danger', allowed: caps.canRevoke, why: caps.whyRevoke }));
        return b.join(' ');
      } }
    ], data.items, { empty, stateKey: 'clients' }) + pagerHtml('clients', st);
    const bb = $('#bb-clients');
    if (bb) bb.outerHTML = `<div id="bb-clients">${batchBarHtml('clients')}</div>`;

    // 运行状态是会自己变的：不刷新的话，界面会一直停在"上传中"，
    // 那几个因此变灰的按钮也就一直灰着，人只能猜要不要手动刷。
    // 只在真有机器在干活时才轮询；route() 切页时会 clearTimeout(App.timer)。
    if (data.items.some(r => r.runtimeState && r.runtimeState !== 'idle'))
      App.timer = setTimeout(() => { if (location.hash === '#/clients') LOADERS.clients(); }, 8000);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

/* §5.2 批量动作：选中行后工具条变形出现（逐条调用既有端点，无需后端改动）*/
App.batchActs = App.batchActs || {};
App.batchActs.clients = [
  { t: '批量预检', primary: true, bulk: true,
    fn: async ids => {
      const d = await api('/api/v1/admin/operations/precheck-batches', { method: 'POST', body: {
        scope: { clientIds: ids }, onlyEnabled: true
      } });
      toast(`已下发 ${d.dispatchedCommands} 条预检指令，跳过 ${d.skippedTasks} 个任务`, 'ok');
    } },
  { t: '批量上传', bulk: true, fn: ids => openClientUploadBatch(ids) },
  { t: '刷新指标', fn: id => api(`/api/v1/admin/clients/${id}/refresh-metrics`, { method: 'POST' }) },
  { t: '禁用', fn: id => api(`/api/v1/admin/clients/${id}/disable`, { method: 'POST', body: { reason: '批量禁用' } }) },
  { t: '注销', danger: true,
    confirm: async () => await confirmModal('批量注销不可恢复，将吊销所选客户端的全部证书，确定继续？'),
    fn: id => api(`/api/v1/admin/clients/${id}/revoke`, { method: 'POST', body: { reason: '批量注销' } }) }
];

/*
  * 批量上传接口接收候选备份集 ID，而客户端列表没有候选集查询端点。因此动作仍从选中的客户端上下文发起，但让操作员粘贴预检产生的候选 ID；不伪造客户端到候选集的映射，也不改后端契约。
 */
function openClientUploadBatch(clientIds) {
  return new Promise(resolve => {
    const ov = openModal('从选中客户端创建批量上传', `
      <p class="text-muted">已选中 ${clientIds.length} 台客户端。请粘贴这些客户端预检产生的候选备份集 ID，每行一个。</p>
      <div class="frow"><label>候选备份集 ID *</label><textarea id="cb_candidate_ids" class="mono" placeholder="每行一个 GUID"></textarea></div>
      <div class="form-grid"><div class="frow"><label>批次名称</label><input id="cb_name" placeholder="可选"></div><div class="frow"><label>并发客户端数</label><input id="cb_cc" type="number" min="1" max="50" value="2"></div></div>
      <div class="frow inline"><label><input type="checkbox" id="cb_skip_busy" checked> 跳过有活动上传的客户端</label></div>`, { okText: '创建批量上传' });
    let settled = false;
    const finish = value => { if (settled) return; settled = true; closeModal(); resolve(value); };
    ov.querySelector('[data-close]').addEventListener('click', () => finish(false), { once: true });
    ov.addEventListener('click', ev => { if (ev.target === ov) finish(false); }, { once: true });
    ov.querySelector('[data-ok]').addEventListener('click', async () => {
      const ids = $('#cb_candidate_ids').value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
      if (!ids.length) { toast('请填写至少一个候选备份集 ID', 'err'); return; }
      const btn = ov.querySelector('[data-ok]'); btn.disabled = true;
      try {
        const d = await api('/api/v1/admin/operations/upload-batches', { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: {
          candidateBackupSetIds: ids, name: $('#cb_name').value.trim() || null,
          maxConcurrentClients: Number($('#cb_cc').value) || 2, skipBusyClients: $('#cb_skip_busy').checked
        } });
        toast(`批量上传已创建（${d.totalItems} 项）`, 'ok');
        finish(true);
      } catch (e) { errToast(e); btn.disabled = false; }
    });
  });
}

ACTIONS['clients:approve'] = async id => {
  if (!await confirmModal('审批通过该客户端并为其签发 mTLS 证书？')) return;
  const d = await api(`/api/v1/admin/clients/${id}/approve`, { method: 'POST' });
  toast(`已审批，证书指纹 ${d.certificateThumbprint}，有效期至 ${fmtDT(d.certificateExpiresAt)}`, 'ok');
  LOADERS.clients();
};
ACTIONS['clients:reject'] = async id => {
  formModal('拒绝注册', [{ name: 'reason', label: '拒绝原因', type: 'text', placeholder: '可选' }], async v => {
    await api(`/api/v1/admin/clients/${id}/reject`, { method: 'POST', body: { reason: v.reason || null } });
    toast('已拒绝该注册请求', 'ok'); LOADERS.clients();
  }, '拒绝');
};
ACTIONS['clients:disable'] = async id => {
  formModal('禁用客户端', [{ name: 'reason', label: '禁用原因', type: 'text', placeholder: '可选' }], async v => {
    await api(`/api/v1/admin/clients/${id}/disable`, { method: 'POST', body: { reason: v.reason || null } });
    toast('客户端已禁用', 'ok'); LOADERS.clients();
  }, '禁用');
};
ACTIONS['clients:enable'] = async id => {
  formModal('启用客户端', [{ name: 'reason', label: '启用原因', type: 'text', placeholder: '可选',
    hint: '会写进客户端备注，方便以后回溯这台机器为什么停过又开了' }], async v => {
    await api(`/api/v1/admin/clients/${id}/enable`, { method: 'POST', body: { reason: v.reason || null } });
    toast('客户端已启用，等下一次心跳就会回到在线', 'ok'); LOADERS.clients();
  }, '启用');
};
ACTIONS['clients:revoke'] = async id => {
  formModal('注销客户端', [{ name: 'reason', label: '注销原因', type: 'text', required: true, hint: '将吊销全部证书，不可恢复' }], async v => {
    if (!await confirmModal('注销不可恢复，确定继续？')) throw new Error('已取消');
    await api(`/api/v1/admin/clients/${id}/revoke`, { method: 'POST', body: { reason: v.reason } });
    toast('客户端已注销', 'ok'); LOADERS.clients();
  }, '注销');
};
ACTIONS['clients:metrics'] = async id => {
  const d = await api(`/api/v1/admin/clients/${id}/refresh-metrics`, { method: 'POST' });
  toast(`刷新指标指令已下发（指令 ${shortId(d.commandId)}）`, 'ok');
};

async function legacyClientDetail(id) {
  $('#app').innerHTML = shell('clients', '客户端详情', loading());
  try {
    const d = await api(`/api/v1/admin/clients/${id}`);
    const m = d.lastMetrics;
    $('#view').innerHTML = `
    <div class="toolbar"><a href="#/clients">← 返回客户端列表</a><div class="spacer"></div>
      <button data-ui-action="act" data-view="clients" data-action="metrics" data-id="${esc(d.id)}">下发刷新指标</button></div>
    <div class="card"><b>${esc(clientName(d))}</b> <span class="sub mono">${esc(d.hostname)}</span> ${status('client_status', d.status)}
      <div class="kv" style="margin-top:12px">
        <div class="row"><div class="k">主机名</div><div class="v mono">${esc(d.hostname)}</div></div>
        <div class="row"><div class="k">机器 ID</div><div class="v mono">${esc(d.machineId || '—')}</div></div>
        <div class="row"><div class="k">系统 / 架构</div><div class="v">${esc(d.osName || '—')} ${esc(d.osVersion || '')} / ${esc(d.architecture || '—')}</div></div>
        <div class="row"><div class="k">IP 地址</div><div class="v">${esc(d.ipAddresses || '—')}</div></div>
        <div class="row"><div class="k">Agent 版本</div><div class="v">${esc(d.agentVersion || '—')}</div></div>
        <div class="row"><div class="k">分组</div><div class="v">${esc(d.clientGroupName || '—')}</div></div>
        <div class="row"><div class="k">最近心跳</div><div class="v">${fmtDT(d.lastHeartbeatAt)}</div></div>
        <div class="row"><div class="k">审批信息</div><div class="v">${d.approvedAt ? fmtDT(d.approvedAt) + '（' + esc(d.approvedByName || '') + '）' : '未审批'}</div></div>
        <div class="row"><div class="k">证书指纹</div><div class="v mono">${esc(d.certificateThumbprint || '—')}</div></div>
        <div class="row"><div class="k">证书有效期</div><div class="v">${fmtDT(d.certificateExpiresAt)}</div></div>
        <div class="row"><div class="k">时钟偏移</div><div class="v">${d.timeOffsetSeconds != null ? esc(d.timeOffsetSeconds) + ' 秒' : '—'}</div></div>
        <div class="row"><div class="k">备注</div><div class="v">${esc(d.notes || '—')}</div></div>
      </div></div>
    ${m ? `<div class="card"><b>最近指标</b>（${fmtDT(m.receivedAt)}）      <div class="kv" style="margin-top:10px">
        <div class="row"><div class="k">CPU</div><div class="v">${m.cpuPercent != null ? esc(m.cpuPercent) + '%' : '—'}</div></div>
        <div class="row"><div class="k">内存使用</div><div class="v">${m.memoryPercent != null ? esc(m.memoryPercent) + '%' : '—'}</div></div>
        <div class="row"><div class="k">可用内存</div><div class="v">${fmtBytes(m.memoryAvailableBytes)}</div></div>
        <div class="row"><div class="k">Agent 占用</div><div class="v">${fmtBytes(m.agentMemoryBytes)}</div></div>
      </div></div>` : ''}
    <div class="card"><b>磁盘</b>${tableHtml([
      { l: '盘符', k: 'driveName' }, { l: '文件系统', k: 'filesystem' },
      { l: '总容量', num: true, render: r => fmtBytes(r.totalBytes) }, { l: '可用', num: true, render: r => fmtBytes(r.freeBytes) },
      { l: '源盘', render: r => r.isSourceVolume ? '是' : '否' }, { l: '采样时间', render: r => fmtDT(r.sampledAt) }
    ], d.disks, { empty: emptyState('first', { title: '暂无磁盘信息', sub: '客户端上报指标后自动填充' }) })}</div>
    <div class="card"><b>关键服务监控</b>${tableHtml([
      { l: '服务', render: r => `${esc(r.displayName)}<span class="sub mono">${esc(r.serviceName)}</span>` },
      { l: '期望状态', k: 'expectedState' }, { l: '实际状态', render: r => r.actualState === 'running' ? status('result', 'success') : r.actualState === 'stopped' ? status('result', 'failure') : esc(r.actualState || '—') },
      { l: '最近采样', render: r => relTime(r.lastSampledAt) }, { l: '异常告警', render: r => r.alertOnMismatch ? '是' : '否' }, { l: '启用', render: r => r.enabled ? '是' : '否' }
    ], d.monitoredServices, { empty: emptyState('first', { title: '未配置关键服务', sub: '在客户端配置中登记需要监控的 Windows 服务' }) })}</div>`;
  } catch (e) { $('#view').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
}

export async function vClientDetail(id) {
  clearTimeout(App.timer);
  $('#app').innerHTML = shell('clients', '客户端详情', loading());
  try {
    const [detail, history] = await Promise.all([
      api(`/api/v1/admin/clients/${id}`),
      api(`/api/v1/admin/clients/${id}/metrics?hours=24&limit=1440`).catch(() => ({ points: [] }))
    ]);
    $('#view').innerHTML = renderClientRuntimeDetail(detail, history);
    // 同列表：正在干活时才轮询，让"上传中"和跟着它变灰的按钮自己走完。
    if (detail.runtimeState && detail.runtimeState !== 'idle')
      App.timer = setTimeout(() => {
        if (location.hash === '#/clients/' + id) vClientDetail(id);
      }, 10000);
  } catch (e) {
    $('#app').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

ACTIONS['clients:refreshDetail'] = async id => vClientDetail(id);

/* ── 关键 Windows 服务监控 ──
   这块配置此前完全没有入口：表在、Agent 会探测、界面有展示，就是没人能往里写。
   空状态那句「在客户端配置中添加需要监控的 Windows 服务」指向的是一个不存在的地方。

   加入口时顺带把「怎么填服务名」这件事解决掉——理由和目录浏览一样：
   要填的是 SCM 服务名而不是显示名，SQL Server 默认实例叫 MSSQLSERVER、
   命名实例叫 MSSQL$SQLEXPRESS，代理服务又是另外两个名字。敲错一个字符的后果
   是监控了一个不存在的服务，而它的表现是状态永远「服务不存在」——
   看起来像服务挂了，实际是名字写错了，几乎没人能靠自查发现。 */

const SERVICE_DISCOVER_TIMEOUT_MS = 90000;

ACTIONS['clients:addService'] = async clientId => {
  const picked = await pickInstalledService(clientId);
  if (!picked) return;
  await openServiceForm(clientId, null, picked);
};

ACTIONS['clients:editService'] = async compound => {
  const [clientId, definitionId] = String(compound).split(':');
  const list = await api(`/api/v1/admin/clients/${clientId}/monitored-services`);
  const current = (list || []).find(x => x.definitionId === definitionId);
  if (!current) { toast('该监控项已不存在', 'err'); return vClientDetail(clientId); }
  await openServiceForm(clientId, current, null);
};

ACTIONS['clients:delService'] = async compound => {
  const [clientId, definitionId] = String(compound).split(':');
  if (!await confirmModal('不再监控这个服务？历史状态记录会保留，但从此不再检查也不再告警。')) return;
  await api(`/api/v1/admin/clients/${clientId}/monitored-services/${definitionId}`, { method: 'DELETE' });
  toast('已移出监控', 'ok');
  vClientDetail(clientId);
};

/* 从客户端真实安装的服务里挑一个。
   客户端离线或版本过旧时退回手工输入——不能因为拿不到列表就把功能整个堵死。 */
function pickInstalledService(clientId) {
  return new Promise(resolve => {
    let settled = false;
    const finish = v => { if (!settled) { settled = true; resolve(v); } };

    // persistent：这个弹窗要等客户端应答十几秒，误点一次遮罩就得从头再等一遍。
    const ov = openModal('添加关键服务 · 读取这台机器上的服务', skeleton(5), { wide: true, persistent: true });
    ov.addEventListener('bm:dismiss', () => finish(null));
    const alive = () => document.body.contains(ov);
    const body = html => { if (alive()) ov.querySelector('.mbody').innerHTML = html; };
    const footer = html => { if (alive()) ov.querySelector('.mfoot').innerHTML = `<button data-close>关闭</button>${html || ''}`; };

    let services = [];
    let keyword = '';

    ov.addEventListener('input', ev => {
      if (!ev.target.matches('[data-svc-filter]')) return;
      keyword = ev.target.value.trim().toLowerCase();
      renderList(true);
    });

    ov.addEventListener('click', ev => {
      const row = ev.target.closest('[data-svc-pick]');
      if (row) {
        const name = row.dataset.svcPick;
        const service = services.find(s => s.serviceName === name);
        finish({ serviceName: name, displayName: service ? service.displayName : name });
        closeModal();
        return;
      }
      if (ev.target.closest('[data-svc-manual]')) {
        finish({ serviceName: '', displayName: '' });
        closeModal();
      }
    });

    (async () => {
      try {
        const dispatched = await api(`/api/v1/admin/clients/${clientId}/monitored-services/discover`, { method: 'POST' });
        const deadline = Date.now() + SERVICE_DISCOVER_TIMEOUT_MS;
        while (Date.now() < deadline) {
          if (!alive()) return;
          await new Promise(r => setTimeout(r, 1500));
          if (!alive()) return;
          const result = await api(
            `/api/v1/admin/clients/${clientId}/monitored-services/discover/${dispatched.commandId}`);
          if (result.status === 'succeeded') {
            services = (result.result && result.result.services) || [];
            renderList(false);
            return;
          }
          if (['failed', 'cancelled', 'expired', 'rejected'].includes(result.status))
            throw new Error(result.resultMessage || `客户端未能返回服务列表（${result.status}）`);
          body(`<div class="hint">已下发指令，等待客户端执行…（${esc(result.status)}）</div>${skeleton(4)}`);
        }
        throw new Error('等待客户端响应超时。客户端可能离线，或 Agent 版本低于 1.1.0。');
      } catch (e) {
        if (!alive()) return;
        body(`<div class="empty">${esc(e.message)}</div>`);
        footer('<button class="primary" data-svc-manual>手工输入服务名</button>');
      }
    })();

    function renderList(keepFocus) {
      if (!alive()) return;
      const filtered = keyword
        ? services.filter(s =>
            s.serviceName.toLowerCase().includes(keyword) || (s.displayName || '').toLowerCase().includes(keyword))
        : services;

      const rows = filtered.slice(0, 300).map(s => `
        <div class="svcrow${s.alreadyMonitored ? ' is-monitored' : ''}"
             ${s.alreadyMonitored ? '' : `data-svc-pick="${esc(s.serviceName)}"`}>
          <div class="svcrow__main">
            <b>${esc(s.displayName)}</b>
            <span class="mono sub">${esc(s.serviceName)}</span>
          </div>
          <span class="dmeta">${esc(L.service_actual[s.status] || s.status)}${
            s.startType ? ' · ' + esc(L.service_start_type[s.startType] || s.startType) : ''}</span>
          ${s.alreadyMonitored ? '<span class="dmeta">已在监控</span>' : '<span class="dmeta">选择 ›</span>'}
        </div>`).join('');

      body(`<p class="hint" style="margin-top:0">这台机器上装了 ${services.length} 个服务，点一个把它加入监控。</p>
        <input data-svc-filter placeholder="按名字筛选，如 sql / 用友 / postgres" value="${esc(keyword)}">
        <div class="svclist">${rows || '<div class="empty">没有匹配的服务</div>'}</div>
        ${filtered.length > 300 ? `<div class="hint">匹配到 ${filtered.length} 个，仅显示前 300 个，请继续输入筛选</div>` : ''}`);
      footer('<button data-svc-manual>找不到？手工输入服务名</button>');

      if (keepFocus) {
        const input = ov.querySelector('[data-svc-filter]');
        if (input) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
      }
    }
  });
}

async function openServiceForm(clientId, current, picked) {
  const isEdit = !!current;
  const fields = [];
  if (isEdit) {
    fields.push({
      name: 'serviceStatic', type: 'static', label: 'Windows 服务名',
      html: `<div class="dirpath">${esc(current.serviceName)}</div>`,
      hint: '服务名不能改——换一个名字等于换一个监控对象，请移除后重新添加'
    });
  } else {
    fields.push({
      name: 'serviceName', label: 'Windows 服务名', type: 'text', required: true,
      value: picked ? picked.serviceName : '',
      placeholder: 'MSSQLSERVER',
      hint: '服务控制管理器里的名称，不是显示名'
    });
  }

  fields.push(
    { name: 'displayName', label: '界面上的称呼', type: 'text',
      value: isEdit ? current.displayName : (picked ? picked.displayName : ''),
      placeholder: '留空则用服务名' },
    { name: 'expectedState', label: '期望状态', type: 'select',
      value: isEdit ? current.expectedState : 'running',
      options: optsOf(L.service_expected),
      hint: '实际状态与期望不符时才算异常' },
    { name: 'alertOnMismatch', label: '告警', labelText: '状态不符时产生告警',
      type: 'checkbox', value: isEdit ? current.alertOnMismatch : true },
    { name: 'enabled', label: '状态', labelText: '启用这条监控',
      type: 'checkbox', value: isEdit ? current.enabled : true, advanced: true }
  );

  formModal(isEdit ? `编辑监控：${current.displayName}` : '添加关键服务', fields, async vals => {
    const body = {
      displayName: vals.displayName || null,
      expectedState: vals.expectedState,
      alertOnMismatch: vals.alertOnMismatch,
      enabled: vals.enabled
    };
    if (isEdit) {
      await api(`/api/v1/admin/clients/${clientId}/monitored-services/${current.definitionId}`,
        { method: 'PUT', body });
    } else {
      body.serviceName = vals.serviceName;
      await api(`/api/v1/admin/clients/${clientId}/monitored-services`, { method: 'POST', body });
    }
    toast(isEdit ? '已保存' : '已加入监控', 'ok');
    vClientDetail(clientId);
  }, isEdit ? '保存' : '添加');
}
