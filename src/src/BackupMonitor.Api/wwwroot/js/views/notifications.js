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

/* 整改批次 C · C1：凭据类字段（SMTP 密码 / webhook 地址）后端已改为掩码回显（"__UNCHANGED__"
 * 表示已配置但不回显明文，null 表示未配置）。密码/webhook 输入框不再回填 value，
 * 只用 placeholder 提示当前是否已配置；用户不重新输入则保存时提交掩码本身，
 * 后端据此保留库里原值不变。*/
const UNCHANGED = '__UNCHANGED__';

async function renderSettings() {
  const body = $('#notificationBody');
  try {
    const s = await api('/api/v1/admin/notification-settings');
    const secModeOpts = [['auto', '自动协商（按端口）'], ['none', '不加密'], ['starttls', 'STARTTLS（如 587）'], ['ssl', '隐式 TLS/SSL（如 465）']]
      .map(([v, t]) => `<option value="${v}" ${(s.email.securityMode || 'auto') === v ? 'selected' : ''}>${t}</option>`).join('');
    body.innerHTML = `<div class="card notification-settings">
      <p class="text-muted">设置是配置，投递 Tab 是该配置产生的运行结果。修改后约 1 分钟内对新告警生效。</p>
      <h3>邮件</h3><div class="frow inline"><label><input type="checkbox" id="n_email_en" ${s.email.enabled ? 'checked' : ''}> 启用邮件渠道</label></div>
      <div class="frow"><label>收件人（每行一个）</label><textarea id="n_email_to">${esc((s.email.recipients || []).join('\n'))}</textarea></div>
      <div class="form-grid"><div class="frow"><label>SMTP 服务器</label><input id="n_email_host" value="${esc(s.email.smtpHost || '')}"></div><div class="frow"><label>SMTP 端口</label><input id="n_email_port" type="number" value="${esc(s.email.smtpPort || 587)}"></div>
      <div class="frow"><label>安全模式</label><select id="n_email_sec">${secModeOpts}</select></div>
      <div class="frow"><label>SMTP 用户名</label><input id="n_email_user" value="${esc(s.email.smtpUsername || '')}"></div><div class="frow"><label>SMTP 密码</label><input id="n_email_pass" type="password" placeholder="${s.email.smtpPassword === UNCHANGED ? '已配置，留空则不修改' : '留空则匿名发送'}"></div>
      <div class="frow"><label>发件人地址</label><input id="n_email_from" value="${esc(s.email.fromAddress || '')}"></div></div>
      <div class="frow inline"><button class="small" id="n_email_test">发送测试邮件</button><span class="tip">用当前表单里的配置试发一封，SMTP 服务器返回的原始错误会直接显示</span></div>
      <h3>企业微信机器人</h3><div class="frow inline"><label><input type="checkbox" id="n_wecom_en" ${s.wecom.enabled ? 'checked' : ''}> 启用企业微信</label></div><div class="frow"><label>Webhook 地址</label><input id="n_wecom_url" class="mono" type="password" placeholder="${s.wecom.webhookUrl === UNCHANGED ? '已配置，留空则不修改' : '含 access_token，请从企业微信机器人管理页复制'}"></div>
      <h3>钉钉机器人</h3><div class="frow inline"><label><input type="checkbox" id="n_ding_en" ${s.dingtalk.enabled ? 'checked' : ''}> 启用钉钉</label></div><div class="frow"><label>Webhook 地址</label><input id="n_ding_url" class="mono" type="password" placeholder="${s.dingtalk.webhookUrl === UNCHANGED ? '已配置，留空则不修改' : '含 access_token，请从钉钉机器人管理页复制'}"></div>
      <button class="primary" id="n_save">保存设置</button>
    </div>`;

    const buildEmail = () => ({
      enabled: $('#n_email_en').checked,
      recipients: $('#n_email_to').value.split('\n').map(x => x.trim()).filter(Boolean),
      smtpHost: $('#n_email_host').value.trim() || null,
      smtpPort: Number($('#n_email_port').value) || 587,
      securityMode: $('#n_email_sec').value,
      smtpUsername: $('#n_email_user').value.trim() || null,
      // 留空 → 提交掩码常量，后端保留原密码；重新输入 → 提交新明文
      smtpPassword: $('#n_email_pass').value || (s.email.smtpPassword === UNCHANGED ? UNCHANGED : null),
      fromAddress: $('#n_email_from').value.trim() || null
    });

    $('#n_save').addEventListener('click', async () => {
      try {
        await api('/api/v1/admin/notification-settings', { method: 'PUT', body: {
          email: buildEmail(),
          wecom: { enabled: $('#n_wecom_en').checked, webhookUrl: $('#n_wecom_url').value.trim() || (s.wecom.webhookUrl === UNCHANGED ? UNCHANGED : null) },
          dingtalk: { enabled: $('#n_ding_en').checked, webhookUrl: $('#n_ding_url').value.trim() || (s.dingtalk.webhookUrl === UNCHANGED ? UNCHANGED : null) }
        } });
        toast('通知设置已保存', 'ok');
      } catch (e) { errToast(e); }
    });

    $('#n_email_test').addEventListener('click', async () => {
      try {
        const email = buildEmail();
        const recipient = (email.recipients || [])[0];
        if (!recipient) { errToast(new Error('请先在收件人列表里填一个邮箱地址')); return; }
        await api('/api/v1/admin/notification-settings/test', { method: 'POST', body: { email, recipient } });
        toast('测试邮件已发送，请检查收件箱', 'ok');
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
