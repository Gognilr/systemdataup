/* js/views/upgrades.js —— Agent 升级下发（R20 改：分批推进 + 按上报版本判成功） */
import { api } from '../api.js';
import { $, esc, tableHtml, toast, errToast, clientLabel, absTime, shortId, openDrawer, searchPickerHtml, initSearchPicker } from '../ui.js';
import { shell, loading } from '../app.js';

/* 一次下发的整体状态。这里的措辞刻意不写「已下发」——
   R20 之前界面上那句「下发成功」正是问题本身：它说的是指令发出去了，
   而人读到的是「升级装好了」。 */
const ROLLOUT_STATUS = {
  pending: '<span class="status status--wait">准备中</span>',
  running: '<span class="status status--busy">推进中</span>',
  succeeded: '<span class="status status--ok">全部完成</span>',
  failed: '<span class="status status--err pill">已停止</span>',
  cancelled: '<span class="status status--off">已取消</span>'
};

const TARGET_STATUS = {
  waiting: '<span class="status status--wait">等待本批</span>',
  dispatched: '<span class="status status--busy">已下发，等版本号</span>',
  succeeded: '<span class="status status--ok">已升级</span>',
  failed: '<span class="status status--err pill">失败</span>',
  cancelled: '<span class="status status--off">未下发</span>'
};

export async function vUpgrades() {
  $('#app').innerHTML = shell('upgrades', 'Agent 升级下发', loading());
  try {
    $('#view').innerHTML = `<div class="card">
      <p class="text-muted">给选中的客户端下发升级。<b>下发只是开始</b>：每一批都要等这些机器心跳回来、
        并且自报的版本号真的变成目标版本，才会放行下一批；哪一批失败，后面的批次就停在那里。</p>
      <div id="ug_pkg" class="hint"></div>
      <div class="frow"><label>选择客户端</label>${searchPickerHtml('ug-clients', { multi: true, placeholder: '输入主机名或显示名搜索，可多选' })}
        <div class="hint">搜索后点选，已选的机器会显示在输入框下方</div></div>
      <div class="form-grid"><div class="frow"><label>目标版本 *</label><input id="ug_ver" placeholder="如 1.2.0"></div><div class="frow"><label>升级包 SHA-256 *</label><input id="ug_sha" class="mono" required></div></div>
      <div class="frow"><label>升级包 URL *</label><input id="ug_url" class="mono" placeholder="如 https://backup.example.com/downloads/BackupMonitor.Agent.zip">
        <div class="hint">客户端只接受<strong>和它自己连服务端时用的 host:port 完全一致</strong>的地址（只比字符串，主机名和 IP 算不同）。
          下发后若回报 <span class="mono">UPGRADE_PACKAGE_URL_FORBIDDEN</span>，就是这里的写法和客户端记的那个对不上，换成它认的那个即可。</div></div>
      <div class="form-grid"><div class="frow"><label>分批计划</label><input id="ug_batch" class="mono" placeholder="1,5,0">
        <div class="hint">每批台数，逗号分隔，末位 0 表示「剩下的全放」。留空按 1,5,0——先 1 台金丝雀。</div></div>
        <div class="frow"><label>备注</label><input id="ug_note"></div></div>
      <button class="primary" id="ug_btn">下发升级</button><div id="ug_result" class="result-block"></div>
    </div>
    <div class="card"><h3>最近的升级下发</h3><div id="ug_list">${loading()}</div></div>`;
    // B5：此前一次拉 200 台铺成勾选网格，超过 200 台的部署会静默漏掉后面的机器。
    // 改成按关键字问服务端要匹配项，选中的机器以 chip 形式留在输入框下方。
    const picker = initSearchPicker($('#view'), 'ug-clients', { multi: true, search: searchClients });

    fillPackageInfo();
    renderList();

    $('#ug_btn').addEventListener('click', async () => {
      const ids = picker.selected().map(c => c.id);
      if (!ids.length) { toast('请至少选择一台客户端', 'err'); return; }
      try {
        const d = await api('/api/v1/admin/agent-upgrades', { method: 'POST', body: {
          clientIds: ids, targetVersion: $('#ug_ver').value.trim(), packageUrl: $('#ug_url').value.trim(),
          packageSha256: $('#ug_sha').value.trim(), batchPlan: $('#ug_batch').value.trim() || null,
          note: $('#ug_note').value.trim() || null
        } });
        toast(`第一批已下发 ${d.dispatched} 台，失败 ${d.failed} 台`, d.failed ? 'err' : 'ok');
        $('#ug_result').innerHTML = tableHtml([
          { l: '客户端', k: 'hostname' },
          { l: '第一批下发', render: r => r.success ? '<span class="status status--ok">已下发</span>' : '<span class="status status--err pill">失败</span>' },
          { l: '指令 ID', render: r => `<span class="mono">${esc(shortId(r.commandId))}</span>` },
          { l: '错误', render: r => esc(r.errorMessage ? `${r.errorCode || ''} ${r.errorMessage}` : '—') }
        ], d.items || []);
        renderList();
      } catch (e) { errToast(e); }
    });
  } catch (e) { $('#view').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
}

/* 版本 / URL / 哈希三项自动填。手抄一个 64 位十六进制哈希本身就是个故障源：
   抄错一位的表现是每台机器都下载成功、校验失败，而那时人只会怀疑包坏了。 */
async function fillPackageInfo() {
  try {
    const p = await api('/api/v1/admin/agent-upgrades/package');
    if (p.version && !$('#ug_ver').value) $('#ug_ver').value = p.version;
    if (p.packageUrl && !$('#ug_url').value) $('#ug_url').value = p.packageUrl;
    if (p.packageSha256 && !$('#ug_sha').value) $('#ug_sha').value = p.packageSha256;
    $('#ug_pkg').innerHTML = p.unavailable
      ? `<span class="status status--wait">${esc(p.unavailable)}</span>`
      : `已按服务端随附的客户端包填好版本 <b>${esc(p.version || '—')}</b>、下载地址与 SHA-256，可直接下发。`;
  } catch (e) {
    $('#ug_pkg').innerHTML = `读取随附包信息失败，三项需要手工填写：${esc(e.message)}`;
  }
}

async function renderList() {
  try {
    const rows = await api('/api/v1/admin/agent-upgrades?limit=20');
    $('#ug_list').innerHTML = tableHtml([
      { l: '目标版本', render: r => `<span class="mono">${esc(r.targetVersion)}</span>` },
      { l: '状态', render: r => ROLLOUT_STATUS[r.status] || esc(r.status) },
      { l: '进度', render: r => `${r.succeededCount} / ${r.totalCount} 台`
          + (r.failedCount ? ` <span class="status status--err pill">失败 ${r.failedCount}</span>` : '')
          + (r.pendingCount ? ` <span class="hint">在途 ${r.pendingCount}</span>` : '') },
      { l: '分批', render: r => `<span class="mono">${esc(r.batchPlan)}</span> <span class="hint">第 ${r.currentBatch + 1} 批</span>` },
      { l: '发起', render: r => `${esc(r.createdByName || '—')}<br>${absTime(r.createdAt)}` },
      { l: '', render: r => `<button class="small" data-ug-detail="${esc(r.id)}">详情</button>`
          + (r.status === 'running' || r.status === 'pending'
            ? ` <button class="small" data-ug-cancel="${esc(r.id)}">停止后续批次</button>` : '') }
    ], rows, { empty: '<div class="empty">还没有下发过升级</div>' });

    $('#ug_list').querySelectorAll('[data-ug-detail]').forEach(b =>
      b.addEventListener('click', () => showDetail(b.getAttribute('data-ug-detail'))));
    $('#ug_list').querySelectorAll('[data-ug-cancel]').forEach(b =>
      b.addEventListener('click', async () => {
        try {
          await api(`/api/v1/admin/agent-upgrades/${b.getAttribute('data-ug-cancel')}/cancel`, { method: 'POST' });
          toast('已停止后续批次；已经发出去的指令无法撤回', 'ok');
          renderList();
        } catch (e) { errToast(e); }
      }));
  } catch (e) {
    $('#ug_list').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

async function showDetail(id) {
  try {
    const d = await api(`/api/v1/admin/agent-upgrades/${id}`);
    const table = tableHtml([
      { l: '批次', render: t => `第 ${t.batchIndex + 1} 批` },
      { l: '客户端', render: t => esc(t.displayName || t.hostname || '—') },
      { l: '状态', render: t => TARGET_STATUS[t.status] || esc(t.status) },
      { l: '版本', render: t => `${esc(t.versionBefore || '未知')} → ${esc(t.reportedVersion || '—')}` },
      { l: '下发时间', render: t => absTime(t.dispatchedAt) },
      { l: '结果', render: t => esc(t.errorMessage ? `${t.errorCode || ''} ${t.errorMessage}` : '—') }
    ], d.targets || []);
    openDrawer({
      title: `升级下发 ${esc(d.targetVersion)}`,
      wide: true,
      bodyHtml: `<div class="card">
        <p>${ROLLOUT_STATUS[d.status] || esc(d.status)}
          分批 <span class="mono">${esc(d.batchPlan)}</span>，当前第 ${d.currentBatch + 1} 批。</p>
        ${d.failureReason ? `<p class="status status--err pill">${esc(d.failureReason)}</p>` : ''}
        <p class="hint">升级包：<span class="mono">${esc(d.packageUrl)}</span>${d.note ? `<br>备注：${esc(d.note)}` : ''}</p>
      </div>${table}`
    });
  } catch (e) { errToast(e); }
}

/* 客户端搜索：只取前 20 条，总数由服务端给，剩下的靠继续输入关键字收敛。 */
async function searchClients(keyword) {
  const qs = `page=1&pageSize=20${keyword ? `&keyword=${encodeURIComponent(keyword)}` : ''}`;
  const d = await api(`/api/v1/admin/clients?${qs}`);
  return {
    total: d.totalCount,
    items: (d.items || []).map(c => ({
      v: c.id, raw: c,
      t: clientLabel(c),
      sub: c.agentVersion || '版本未知'
    }))
  };
}
