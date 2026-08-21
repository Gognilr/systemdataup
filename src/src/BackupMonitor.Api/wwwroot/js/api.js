/* 管理端请求封装：访问令牌只驻留内存，刷新令牌由 HttpOnly Cookie 持有。 */

export const store = {
  get: key => localStorage.getItem('bm_' + key),
  set: (key, value) => localStorage.setItem('bm_' + key, value),
  del: key => localStorage.removeItem('bm_' + key)
};

let accessToken = null;
try {
  // 清理旧版本曾写入 localStorage 的令牌，避免继续暴露或误用。
  localStorage.removeItem('bm_accessToken');
  localStorage.removeItem('bm_refreshToken');
} catch (e) {}

export function setAccessToken(token) { accessToken = token || null; }
export function getAccessToken() { return accessToken; }
export function clearAuth() {
  accessToken = null;
  store.del('mustChange');
}

export async function api(path, opts = {}) {
  const doFetch = async () => {
    const headers = Object.assign({}, opts.headers || {});
    if (accessToken && !opts.noAuth) headers.Authorization = 'Bearer ' + accessToken;
    let body = opts.body;
    if (body !== undefined && typeof body !== 'string') {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(body);
    }
    const res = await fetch(path, {
      method: opts.method || 'GET',
      headers,
      body,
      credentials: 'same-origin'
    });
    const text = await res.text();
    let json = null;
    try { json = text ? JSON.parse(text) : null; } catch (e) {}
    return { res, json };
  };

  const result = await doFetch();
  if (result.res.status === 401 && !opts.noAuth && !opts.retried) {
    if (await tryRefresh()) {
      return api(path, Object.assign({}, opts, { retried: true }));
    }
    forceLogin();
    return null;
  }
  const { res, json } = result;
  if (!res.ok || (json && json.success === false)) {
    const err = (json && json.error) || {};
    // 服务端强制改密闸门：过期的旧标签页可能仍在调用业务接口，直接送回改密页，
    // 而不是让用户面对一连串没有上下文的 403。
    if (err.code === 'PASSWORD_CHANGE_REQUIRED') {
      store.set('mustChange', '1');
      if (location.hash !== '#/change-password') location.hash = '#/change-password';
    }
    let msg = err.message || ('HTTP ' + res.status);
    if (err.details) {
      const detail = Object.entries(err.details)
        .map(([key, value]) => `${key}: ${(value || []).join('；')}`)
        .join('；');
      if (detail) msg += '（' + detail + '）';
    }
    const error = new Error(msg);
    error.code = err.code;
    error.status = res.status;
    throw error;
  }
  api.lastMessage = json ? json.message : null;
  return json ? json.data : null;
}

let refreshing = null;
async function tryRefresh() {
  if (!refreshing) {
    refreshing = (async () => {
      try {
        const res = await fetch('/api/v1/auth/refresh', {
          method: 'POST',
          credentials: 'same-origin'
        });
        const json = await res.json().catch(() => null);
        if (res.ok && json && json.success && json.data) {
          setAccessToken(json.data.accessToken);
          return true;
        }
        return false;
      } catch (e) {
        return false;
      }
    })().finally(() => { setTimeout(() => { refreshing = null; }, 0); });
  }
  return refreshing;
}

export function forceLogin() {
  clearAuth();
  location.hash = '#/login';
}
