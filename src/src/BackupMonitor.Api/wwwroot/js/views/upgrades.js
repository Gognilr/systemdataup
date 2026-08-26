/* js/views/upgrades.js —— Agent 升级下发 */
import { api } from '../api.js';
import { $, esc, tableHtml, toast, errToast, clientLabel, searchPickerHtml, initSearchPicker } from '../ui.js';
import { shell, loading } from '../app.js';

export async function vUpgrades() {
  $('#app').innerHTML = shell('upgrades', 'Agent 升级下发', loading());
  try {
    $('#view').innerHTML = `<div class="card" style="max-width:860px">
      <p class="text-muted">经指令通道向选定客户端下发 upgrade_agent 指令，Agent 自行下载并安装升级包。</p>
      <div class="frow"><label>选择客户端</label>${searchPickerHtml('ug-clients', { multi: true, placeholder: '输入主机名或显示名搜索，可多选' })}
        <div class="hint">搜索后点选，已选的机器会显示在输入框下方</div></div>
      <div class="form-grid"><div class="frow"><label>目标版本 *</label><input id="ug_ver" placeholder="如 1.2.0"></div><div class="frow"><label>升级包 SHA-256 *</label><input id="ug_sha" class="mono" required></div></div>
      <div class="frow"><label>升级包 URL *</label><input id="ug_url" class="mono" placeholder="如 https://backup.example.com/downloads/BackupMonitor.Agent.zip"></div>
      <div class="frow"><label>备注</label><input id="ug_note"></div>
      <button class="primary" id="ug_btn">下发升级指令</button><div id="ug_result" class="result-block"></div>
    </div>`;
    // B5：此前一次拉 200 台铺成勾选网格，超过 200 台的部署会静默漏掉后面的机器。
    // 改成按关键字问服务端要匹配项，选中的机器以 chip 形式留在输入框下方。
    const picker = initSearchPicker($('#view'), 'ug-clients', { multi: true, search: searchClients });

    $('#ug_btn').addEventListener('click', async () => {
      const ids = picker.selected().map(c => c.id);
      if (!ids.length) { toast('请至少选择一台客户端', 'err'); return; }
      try {
        const d = await api('/api/v1/admin/agent-upgrades', { method: 'POST', body: {
          clientIds: ids, targetVersion: $('#ug_ver').value.trim(), packageUrl: $('#ug_url').value.trim(),
          packageSha256: $('#ug_sha').value.trim(), note: $('#ug_note').value.trim() || null
        } });
        toast(`下发完成：成功 ${d.dispatched}，失败 ${d.failed}`, d.failed ? 'err' : 'ok');
        $('#ug_result').innerHTML = tableHtml([
          { l: '客户端', k: 'hostname' },
          { l: '结果', render: r => r.success ? '<span class="status status--ok">成功</span>' : '<span class="status status--err pill">失败</span>' },
          { l: '指令 ID', render: r => `<span class="mono">${esc(String(r.commandId || '').slice(0, 8) || '—')}</span>` },
          { l: '错误', render: r => esc(r.errorMessage ? `${r.errorCode || ''} ${r.errorMessage}` : '—') }
        ], d.items || []);
      } catch (e) { errToast(e); }
    });
  } catch (e) { $('#view').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
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
