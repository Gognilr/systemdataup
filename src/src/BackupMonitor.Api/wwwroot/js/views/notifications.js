/* js/views/notifications.js —— 通知设置 + 通知投递，两 Tab 同页呈现 */
import { api } from '../api.js';
import { App, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, shortId, fmtDT, tableHtml, pagerHtml,
  emptyState, toast, errToast
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vNotifications(tab = 'settings') {
  App.state.notifications = App.state.notifications || { tab: 'settings', page: 1, pageSize: 15, status: '', channel: '', totalCount: 0 };
  const st = App.state.notifications;
  st.tab = tab === 'deliveries' ? 'deliveries' : 'settings';
  $('#app').innerHTML = shell('notifications', '通知', `<div class="tabbar" id="notificationTabs" role="tablist">
    <a role="tab" aria-selected="${st.tab === 'settings'}" class="tab ${st.tab === 'settings' ? 'active' : ''}" href="#/notifications/settings">通知设置</a>
    <a role="tab" aria-selected="${st.tab === 'deliveries'}" class="tab ${st.tab === 'deliveries' ? 'active' : ''}" href="#/notifications/deliveries">通知投递</a>
  </div><div id="notificationBody">${loading()}</div>`);
  if (st.tab === 'settings') await renderSettings();
  else await renderDeliveries();
}

async function renderSettings() {
  const body = $('#notificationBody');
  try {
    const s = await api('/api/v1/admin/notification-settings');
    body.innerHTML = `<div class="card notification-settings">
      <p class="text-muted">设置是配置，投递 Tab 是该配置产生的运行结果。修改后约 1 分钟内对新告警生效。</p>
      <h3>邮件</h3><div class="frow inline"><label><input type="checkbox" id="n_email_en" ${s.email.enabled ? 'checked' : ''}> 启用邮件渠道</label></div>
      <div class="frow"><label>收件人（每行一个）</label><textarea id="n_email_to">${esc((s.email.recipients || []).join('\n'))}</textarea></div>
      <div class="form-grid"><div class="frow"><label>SMTP 服务器</label><input id="n_email_host" value="${esc(s.email.smtpHost || '')}"></div><div class="frow"><label>SMTP 端口</label><input id="n_email_port" type="number" value="${esc(s.email.smtpPort || 25)}"></div>
      <div class="frow"><label>SMTP 用户名</label><input id="n_email_user" value="${esc(s.email.smtpUsername || '')}"></div><div class="frow"><label>SMTP 密码</label><input id="n_email_pass" type="password" value="${esc(s.email.smtpPassword || '')}" placeholder="留空则匿名发送"></div>
      <div class="frow"><label>发件人地址</label><input id="n_email_from" value="${esc(s.email.fromAddress || '')}"></div></div>
      <h3>企业微信机器人</h3><div class="frow inline"><label><input type="checkbox" id="n_wecom_en" ${s.wecom.enabled ? 'checked' : ''}> 启用企业微信</label></div><div class="frow"><label>Webhook 地址</label><input id="n_wecom_url" class="mono" value="${esc(s.wecom.webhookUrl || '')}"></div>
      <h3>钉钉机器人</h3><div class="frow inline"><label><input type="checkbox" id="n_ding_en" ${s.dingtalk.enabled ? 'checked' : ''}> 启用钉钉</label></div><div class="frow"><label>Webhook 地址</label><input id="n_ding_url" class="mono" value="${esc(s.dingtalk.webhookUrl || '')}"></div>
      <button class="primary" id="n_save">保存设置</button>
    </div>`;
    $('#n_save').addEventListener('click', async () => {
      try {
        await api('/api/v1/admin/notification-settings', { method: 'PUT', body: {
          email: { enabled: $('#n_email_en').checked, recipients: $('#n_email_to').value.split('\n').map(x => x.trim()).filter(Boolean), smtpHost: $('#n_email_host').value.trim() || null, smtpPort: Number($('#n_email_port').value) || 25, smtpUsername: $('#n_email_user').value.trim() || null, smtpPassword: $('#n_email_pass').value || null, fromAddress: $('#n_email_from').value.trim() || null },
          wecom: { enabled: $('#n_wecom_en').checked, webhookUrl: $('#n_wecom_url').value.trim() || null },
          dingtalk: { enabled: $('#n_ding_en').checked, webhookUrl: $('#n_ding_url').value.trim() || null }
        } });
        toast('通知设置已保存', 'ok');
      } catch (e) { errToast(e); }
    });
  } catch (e) { body.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
}

async function renderDeliveries() {
  const st = App.state.notifications;
  $('#notificationBody').innerHTML = `<div class="toolbar"><select id="n_status"><option value="">全部状态</option>${optsOf(L.notif_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
    <select id="n_channel"><option value="">全部渠道</option>${optsOf(L.channel).map(o => `<option value="${o.v}" ${st.channel === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select><button class="primary" id="n_query">查询</button><span class="tip">通知派发工作器按配置间隔驱动，失败自动重试</span></div><div id="nwrap">${loading()}</div>`;
  $('#n_status').onchange = () => { st.status = $('#n_status').value; st.page = 1; LOADERS.notifications(); };
  $('#n_channel').onchange = () => { st.channel = $('#n_channel').value; st.page = 1; LOADERS.notifications(); };
  $('#n_query').addEventListener('click', () => { st.page = 1; LOADERS.notifications(); });
  await LOADERS.notifications();
}
LOADERS.notifications = async function () {
  const st = App.state.notifications;
  const wrap = $('#nwrap'); if (!wrap || st.tab !== 'deliveries') return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.status) q.set('status', st.status);
    if (st.channel) q.set('channel', st.channel);
    const data = await api('/api/v1/admin/notification-deliveries?' + q);
    st.totalCount = data.totalCount;
    wrap.innerHTML = tableHtml([
      { l: '告警', render: r => esc(r.alertTitle || shortId(r.alertId)) },
      { l: '渠道', render: r => esc(L.channel[r.channel] || r.channel) },
      { l: '收件人', render: r => `<span class="mono">${esc(r.recipient)}</span>` },
      { l: '状态', render: r => status('notif_status', r.status) },
      { l: '尝试次数', num: true, k: 'attemptCount' }, { l: '最近尝试', render: r => fmtDT(r.lastAttemptAt) },
      { l: '发送成功', render: r => fmtDT(r.sentAt) }, { l: '错误', render: r => `<span class="text-error">${esc((r.errorMessage || '').slice(0, 60))}</span>` }
    ], data.items, { empty: emptyState('ok', { title: '没有通知投递记录', sub: '当前没有需要关注的发送结果' }) }) + pagerHtml('notifications', st);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};
