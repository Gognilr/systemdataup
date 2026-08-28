import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import { $, esc, fmtDT, toast, confirmModal, formModal, openModal, closeModal, emptyState } from '../ui.js';
import { shell, loading } from '../app.js';

export async function vRegistrationTokens() {
  App.state.registrationTokens = App.state.registrationTokens || { includeInactive: false };
  const st = App.state.registrationTokens;
  $('#app').innerHTML = shell('registration-tokens', '客户端注册令牌', `
    <div class="toolbar">
      <label class="inline"><input id="rt_inactive" type="checkbox" ${st.includeInactive ? 'checked' : ''}> 显示已撤销/过期</label>
      <div class="spacer"></div>
      <button class="primary" id="rt_create">创建注册令牌</button>
    </div>
    <div class="card"><div class="tip">令牌原文只在刚创建时显示一次，服务器不会再显示第二次——请当场复制保存。装客户端时填这个令牌，用过即失效。</div></div>
    <div id="rt_wrap">${loading()}</div>`);
  $('#rt_inactive').onchange = ev => { st.includeInactive = ev.target.checked; LOADERS.registrationTokens(); };
  $('#rt_create').addEventListener('click', createToken);
  await LOADERS.registrationTokens();
}

LOADERS.registrationTokens = async function () {
  const st = App.state.registrationTokens;
  const wrap = $('#rt_wrap');
  if (!wrap) return;
  try {
    const data = await api('/api/v1/admin/registration-tokens?includeInactive=' + (st.includeInactive ? 'true' : 'false')) || [];
    if (!data.length) {
      wrap.innerHTML = emptyState('first', { title: '暂无注册令牌', sub: '创建令牌后分发给客户端 Agent 安装程序' });
      return;
    }
    wrap.innerHTML = `<div class="card"><table><thead><tr><th>名称</th><th>客户端分组</th><th>使用次数</th><th>有效期</th><th>状态</th><th>创建时间</th><th>操作</th></tr></thead><tbody>${data.map(token => `
      <tr><td><b>${esc(token.name)}</b><span class="sub mono">${esc(token.id)}</span></td>
        <td>${esc(token.clientGroupName || '全部分组')}</td>
        <td class="num">${esc(token.usedCount)} / ${esc(token.maxUses)}</td>
        <td>${token.expiresAt ? fmtDT(token.expiresAt) : '永久'}</td>
        <td><span class="status ${token.status === 'active' ? 'status--ok' : 'status--muted'}">${esc(token.status)}</span></td>
        <td>${fmtDT(token.createdAt)}</td>
        <td>${token.status === 'active' ? `<button class="small danger" data-ui-action="act" data-view="registration-tokens" data-action="revoke" data-id="${esc(token.id)}">撤销</button>` : '—'}</td>
      </tr>`).join('')}</tbody></table></div>`;
  } catch (e) {
    wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
};

async function createToken() {
  formModal('创建客户端注册令牌', [
    { name: 'name', label: '令牌名称', required: true, placeholder: '例如：生产环境 Windows Agent' },
    { name: 'maxUses', label: '最大使用次数', type: 'number', value: '1', required: true, hint: '每成功提交一次注册申请消耗一次。' },
    { name: 'expiresAt', label: '过期时间', type: 'datetime-local', hint: '留空表示永久有效' }
  ], async values => {
    const maxUses = Math.max(1, Number(values.maxUses) || 1);
    const expiresAt = values.expiresAt ? new Date(values.expiresAt).toISOString() : null;
    const result = await api('/api/v1/admin/registration-tokens', {
      method: 'POST', body: { name: values.name, maxUses, expiresAt }
    });
    const overlay = openModal('注册令牌已创建', `<p>请立即复制下面的明文令牌并交给 Agent 安装人员。关闭此窗口后，服务器不会再次显示明文。</p>
      <div class="frow"><label>注册令牌</label><textarea id="rt_plain" class="mono" readonly>${esc(result.token)}</textarea></div>
      <div class="frow"><label>客户端安装程序</label><a href="/downloads/BackupMonitor.Agent.Setup.exe" download>下载 BackupMonitor.Agent.Setup.exe</a></div>
      <div class="frow"><label>高级部署</label><a href="/downloads/BackupMonitor.Agent.zip" download>下载 ZIP / PowerShell 脚本包</a></div>
      <div class="frow"><label>系统要求</label><p class="hint">Windows Server 2016 及以上可直接安装。Server 2012 / 2012 R2 需先打系统补丁 KB2999226（走 Windows Update 全量更新即可）并安装 <a href="/downloads/VC_redist.x64.exe" download>VC++ 2015-2022 运行库</a>，否则安装程序无法启动。装不上时先在目标机上双击 <a href="/downloads/check-prereq.cmd" download>check-prereq.cmd</a> 自检。</p></div>`, { okText: '复制令牌并关闭' });
    overlay.querySelector('[data-ok]').addEventListener('click', async () => {
      try { await navigator.clipboard.writeText(result.token); toast('令牌已复制', 'ok'); } catch { toast('浏览器未授予剪贴板权限，请手动复制', 'err'); }
      closeModal();
    });
    LOADERS.registrationTokens();
  }, '创建并显示令牌');
}

ACTIONS['registration-tokens:revoke'] = async id => {
  if (!await confirmModal('撤销后，使用该令牌的 Agent 将无法注册，确定继续吗？')) return;
  await api(`/api/v1/admin/registration-tokens/${id}/revoke`, { method: 'POST', body: { reason: '管理员手动撤销' } });
  toast('注册令牌已撤销', 'ok');
  LOADERS.registrationTokens();
};


